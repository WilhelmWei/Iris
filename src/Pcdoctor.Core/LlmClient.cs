using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pcdoctor.Core;

/// <summary>调用模型失败（已转成给用户看的中文说明）。StatusCode 是 HTTP 状态码，连不上时为 null。</summary>
public sealed class LlmException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>模型调用结果：解析好的输出（可能不是对象，交给 Judge.Run 校验）、每次请求的 usage、最后一次的原始文本。</summary>
public sealed record LlmResult(JsonNode? Output, List<JsonNode?> Usage, string RawText);

/// <summary>
/// 调用 OpenAI 兼容的 /chat/completions 接口（对应 run_judge.py 的 post_chat / call_model / parse_json）。
/// 域名解析失败时（有些路由器处理 IPv6 地址查询会出错），第一次失败后改为只查 IPv4，之后隔几秒重试，最多 4 次。
/// </summary>
public static class LlmClient
{
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultModel = "deepseek-chat";
    public const int MaxTokens = 8000;
    public const int DnsRetries = 4;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);
    const string RetryPrompt = "上面的输出不是合法 JSON。请只输出一个符合格式的 JSON 对象。";

    // 和 Python 版一样是进程级的：一旦切到只查 IPv4，之后的请求都只查 IPv4
    static volatile bool _ipv4Only;
    public static bool Ipv4Only => _ipv4Only;

    // 测试用的替换点
    internal static Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> Resolve =
        (host, family, ct) => Dns.GetHostAddressesAsync(host, family, ct);
    internal static Func<TimeSpan, CancellationToken, Task> Delay = (t, ct) => Task.Delay(t, ct);
    internal static void ResetDnsFallback() => _ipv4Only = false;

    static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>
    /// 带 DNS 回退的连接处理器。useProxy=false 时不走系统代理（本机测试用）。
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool useProxy = true, IWebProxy? proxy = null)
    {
        var h = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync,
        };
        if (proxy is not null) h.Proxy = proxy;
        return h;
    }

    /// <summary>域名解析失败（对应 Python 的 socket.gaierror）。</summary>
    sealed class DnsFailure(string host, Exception inner) : Exception($"域名解析失败：{host}（{inner.Message}）", inner);

    static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        IPAddress[] addrs;
        if (IPAddress.TryParse(host, out var ip)) addrs = [ip];
        else
        {
            try
            {
                addrs = await Resolve(host, _ipv4Only ? AddressFamily.InterNetwork : AddressFamily.Unspecified, ct).ConfigureAwait(false);
            }
            catch (SocketException e)
            {
                throw new DnsFailure(host, e);
            }
            if (addrs.Length == 0) throw new DnsFailure(host, new SocketException((int)SocketError.HostNotFound));
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static bool IsDnsFailure(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is DnsFailure) return true;
            if (x is SocketException se && se.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NoRecovery)
                return true;
            if (x is HttpRequestException hre && hre.HttpRequestError == HttpRequestError.NameResolutionError) return true;
        }
        return false;
    }

    /// <summary>parse_json()：模型偶尔会在 JSON 外面包一层 ```json 代码块。解析失败抛 JsonException。</summary>
    public static JsonNode? ParseJson(string text)
    {
        text = text.Trim();
        var m = Regex.Match(text, @"```(?:json)?\s*(\{.*\})\s*```", RegexOptions.Singleline);
        if (m.Success) text = m.Groups[1].Value;
        return PyJson.Loads(text);
    }

    /// <summary>请求体（与 Python 版相同：reasoner 模型不加 temperature 和 response_format）。</summary>
    public static JsonObject BuildBody(string model, JsonArray messages)
    {
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["max_tokens"] = MaxTokens };
        if (!model.Contains("reasoner", StringComparison.Ordinal))
        {
            body["temperature"] = 0.2;
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        return body;
    }

    static string Hint(int code) => code switch
    {
        401 => "（API Key 不对）",
        402 => "（账户余额不足）",
        429 => "（请求太频繁，稍后再试）",
        _ => "",
    };

    /// <summary>post_chat()：发一次请求，返回解析后的响应 JSON。</summary>
    public static async Task<JsonNode?> PostChatAsync(string url, string apiKey, JsonObject body, TimeSpan timeout,
                                                      IProgress<string>? progress = null, HttpClient? http = null,
                                                      CancellationToken ct = default)
    {
        http ??= Shared.Value;
        var payload = Encoding.UTF8.GetBytes(PyJson.Dumps(body));
        for (int attempt = 1; attempt <= DnsRetries; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new ByteArrayContent(payload);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            try
            {
                using var resp = await http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
                var bytes = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(bytes);
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    throw new LlmException($"模型接口返回错误 {code}{Hint(code)}：{Py.Head(text, 500)}", code);
                }
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException e)
                {
                    throw new LlmException($"模型接口返回的不是 JSON：{Py.Head(text, 200)}", (int)resp.StatusCode, e);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException e)
            {
                throw new LlmException($"连不上模型接口 {url}：等待超过 {timeout.TotalSeconds:0} 秒没有响应", null, e);
            }
            catch (HttpRequestException e)
            {
                if (!IsDnsFailure(e) || attempt == DnsRetries)
                    throw new LlmException($"连不上模型接口 {url}：{Innermost(e).Message}", null, e);
                // 域名解析失败：有些路由器处理 IPv6 地址查询（AAAA）会出错，导致整次解析失败。
                // 第一次失败后改为只查 IPv4，再失败就隔几秒重试
                if (!_ipv4Only)
                {
                    _ipv4Only = true;
                    progress?.Report("域名解析失败，改为只查 IPv4 地址后重试…");
                }
                else
                {
                    progress?.Report($"域名解析又失败了，{2 * attempt} 秒后第 {attempt + 1} 次重试…");
                    await Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                }
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    static Exception Innermost(Exception e)
    {
        while (e.InnerException is not null) e = e.InnerException;
        return e;
    }

    /// <summary>
    /// call_model()：发系统提示 + 证据包，返回解析好的输出。输出不是合法 JSON 时追加一轮纠正要求重试一次，
    /// 还不行抛 LlmException。evidence 按 Python 的 json.dumps(ensure_ascii=False) 格式发送。
    /// </summary>
    public static async Task<LlmResult> CallAsync(string systemPrompt, JsonNode evidence, string model = DefaultModel,
                                                  string baseUrl = DefaultBaseUrl, string apiKey = "", TimeSpan? timeout = null,
                                                  IProgress<string>? progress = null, HttpClient? http = null,
                                                  CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = PyJson.Dumps(evidence) },
        };
        var body = BuildBody(model, messages);
        var usage = new List<JsonNode?>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var data = await PostChatAsync(url, apiKey, body, timeout ?? DefaultTimeout, progress, http, ct).ConfigureAwait(false);
            usage.Add(Py.Clone(Py.Get(data, "usage")));
            var choices = Py.Get(data, "choices");
            var first = Py.Truthy(choices) ? Py.Iter(choices).FirstOrDefault() : null;
            var text = Py.StrOr(Py.Get(Py.Get(first, "message"), "content"));
            try
            {
                return new LlmResult(ParseJson(text), usage, text);
            }
            catch (JsonException e)
            {
                if (attempt == 1) throw new LlmException($"模型两次输出都不是合法 JSON：{e.Message}", null, e);
                var retry = (JsonArray)messages.DeepClone();
                retry.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
                retry.Add(new JsonObject { ["role"] = "user", ["content"] = RetryPrompt });
                body["messages"] = retry;
            }
        }
        throw new InvalidOperationException("unreachable");
    }
}
