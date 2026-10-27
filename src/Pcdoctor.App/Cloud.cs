using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Pcdoctor.App;

/// <summary>和 Iris 服务器（阿里云香港，代码在独立仓库 iris-cloud）的全部通信：发送内测反馈包、检查更新。
///
/// · 服务器地址在构建时写进程序（tools/build_app.py --feedback-url / --latest-url），没有地址的构建完全不联网，
///   反馈改为导出文件。
/// · 只在用户同意之后发送（设置里的 BetaConsent == "yes"），发送的内容在 Services.FeedbackPackage 里组装，已去隐私。
/// · 先写进本机的待发送目录再上传：断网、服务器出错时留着，下次启动或下次发送时重试，最多保留 30 个。
///
/// 接口约定（iris-cloud 实现）：
///   POST {FeedbackUrl}  正文是 gzip 压缩的 JSON；请求头 X-Iris-Install（随机安装编号）、X-Iris-Version、X-Iris-Kind
///                       成功 200 {"ok": true}；413 太大；429 太频繁；400 格式不对（不重试）
///   GET  {LatestUrl}    {"version": "1.0.0-beta.2", "url": "下载地址", "notes": "更新说明", "sha256": "…"}</summary>
public static class Cloud
{
    public static string? FeedbackUrl { get; } = Meta("IrisFeedbackUrl");
    public static string? LatestUrl { get; } = Meta("IrisLatestUrl");
    public const int MaxBytes = 2 * 1024 * 1024;
    const int MaxOutbox = 30;

    static string? Meta(string key)
    {
        var v = typeof(Cloud).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(v) || !v.StartsWith("https://", StringComparison.Ordinal) ? null : v;
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    static readonly SemaphoreSlim FlushLock = new(1, 1);

    public static byte[] Gzip(JsonNode node)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(node.ToJsonString()));
        return ms.ToArray();
    }

    /// <summary>放进待发送目录。文件名带时间和类型，内容已压缩。返回文件路径。</summary>
    public static string Enqueue(string kind, JsonObject payload)
    {
        var bytes = Gzip(payload);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException($"反馈包太大（{bytes.Length / 1024} KB），没有发送");
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Safe(kind)}.json.gz";
        var path = Path.Combine(AppData.Outbox, name);
        File.WriteAllBytes(path, bytes);
        // 只留最新的 30 个，防止长期断网时越积越多
        foreach (var old in Directory.GetFiles(AppData.Outbox, "*.json.gz").OrderByDescending(f => f).Skip(MaxOutbox))
            try { File.Delete(old); } catch { }
        return path;
    }

    static string Safe(string s) => new(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(24).ToArray());

    public sealed record FlushResult(int Sent, int Pending, string? Error);

    /// <summary>把待发送目录里的包按时间顺序发出去。网络错误或服务器 5xx 就停，留到下次；400 / 413 的包删除（重发也不会成功）。</summary>
    public static async Task<FlushResult> Flush(string installId, CancellationToken ct = default)
    {
        if (FeedbackUrl == null) return new(0, Directory.GetFiles(AppData.Outbox, "*.json.gz").Length, "没有配置服务器");
        await FlushLock.WaitAsync(ct);
        try
        {
            int sent = 0;
            string? error = null;
            foreach (var f in Directory.GetFiles(AppData.Outbox, "*.json.gz").OrderBy(x => x))
            {
                var kind = Path.GetFileName(f).Split('-', 2)[1].Replace(".json.gz", "");
                using var req = new HttpRequestMessage(HttpMethod.Post, FeedbackUrl) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(f, ct)) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
                req.Headers.Add("X-Iris-Install", installId);
                req.Headers.Add("X-Iris-Version", AppData.Version);
                req.Headers.Add("X-Iris-Kind", kind);
                try
                {
                    using var resp = await Http.SendAsync(req, ct);
                    var code = (int)resp.StatusCode;
                    if (resp.IsSuccessStatusCode) { File.Delete(f); sent++; continue; }
                    var why = await Reason(resp, ct);
                    if (code is 400 or 413) { File.Delete(f); error = $"服务器拒收了一个反馈包（{code}{why}）"; continue; }
                    error = code == 429 ? "发送太频繁，稍后会自动重试" : $"服务器暂时出错（{code}{why}），稍后会自动重试";
                    AppData.LogError("发送反馈", new HttpRequestException($"HTTP {code}{why}"));
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    error = "连不上 Iris 服务器，联网后会自动重试";
                    break;
                }
            }
            return new(sent, Directory.GetFiles(AppData.Outbox, "*.json.gz").Length, error);
        }
        finally { FlushLock.Release(); }
    }

    /// <summary>服务器返回的出错原因（读不到就是空字符串），见 Insights.ServerReason。</summary>
    static async Task<string> Reason(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return Pcdoctor.Core.Insights.ServerReason(await resp.Content.ReadAsStringAsync(ct)); }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ 检查更新
    public static async Task<JsonObject?> CheckUpdate(CancellationToken ct = default)
    {
        if (LatestUrl == null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var j = JsonNode.Parse(await Http.GetStringAsync(LatestUrl, cts.Token)) as JsonObject;
            var v = j?["version"]?.ToString();
            var url = j?["url"]?.ToString();
            if (v == null || url == null || !url.StartsWith("https://", StringComparison.Ordinal)) return null;
            return Pcdoctor.Core.Insights.CompareVersions(v, AppData.Version) > 0
                ? new JsonObject { ["version"] = v, ["url"] = url, ["notes"] = j!["notes"]?.ToString() ?? "", ["sha256"] = j["sha256"]?.ToString() ?? "" }
                : null;
        }
        catch { return null; }  // 检查更新失败不打扰用户
    }
}
