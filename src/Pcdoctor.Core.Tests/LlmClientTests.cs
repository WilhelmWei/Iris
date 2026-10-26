using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Pcdoctor.Core;

namespace Pcdoctor.Core.Tests;

/// <summary>本机假接口：按顺序返回 Replies 里的内容，记录收到的请求（对应 test_judge.py 的 FakeDeepSeek）。</summary>
internal sealed class FakeDeepSeek : IDisposable
{
    public record Req(string Path, string? Auth, string ContentType, string BodyText, JsonObject Body);

    readonly HttpListener _listener = new();
    readonly CancellationTokenSource _stop = new();
    public readonly ConcurrentQueue<(int status, string content, int delayMs)> Replies = new();
    public readonly List<Req> Requests = [];
    public int Port { get; }
    public string BaseUrl => $"http://localhost:{Port}";

    public FakeDeepSeek()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://localhost:{Port}/"); // Windows 上普通用户只能监听 localhost
        _listener.Start();
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        lock (Requests)
            Requests.Add(new Req(ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["Authorization"], ctx.Request.ContentType ?? "",
                                 text, (JsonObject)JsonNode.Parse(text)!));
        Replies.TryDequeue(out var r);
        if (r.delayMs > 0)
        {
            try { await Task.Delay(r.delayMs, _stop.Token); } catch { }
        }
        var payload = r.status == 200
            ? new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = r.content } }),
                ["usage"] = new JsonObject { ["total_tokens"] = 100 },
            }
            : new JsonObject { ["error"] = new JsonObject { ["message"] = r.content } };
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        try
        {
            ctx.Response.StatusCode = r.status == 0 ? 200 : r.status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

/// <summary>移植自 test_judge.py 的 CallModelTest，另加 DNS 回退、超时和取消。</summary>
public class LlmClientTests : IDisposable
{
    readonly FakeDeepSeek _fake = new();
    readonly HttpClient _http = new(LlmClient.CreateHandler(useProxy: false)) { Timeout = Timeout.InfiniteTimeSpan };

    public LlmClientTests() => LlmClient.ResetDnsFallback();

    public void Dispose()
    {
        _http.Dispose();
        _fake.Dispose();
        LlmClient.ResetDnsFallback();
    }

    Task<LlmResult> Call(string model = "deepseek-chat", string key = "sk-test", string? baseUrl = null, HttpClient? http = null,
                         TimeSpan? timeout = null, IProgress<string>? progress = null, CancellationToken ct = default) =>
        LlmClient.CallAsync("系统提示", T.J("""{"items": [], "中文": "是"}"""), model, baseUrl ?? _fake.BaseUrl, key,
                            timeout ?? TimeSpan.FromSeconds(10), progress, http ?? _http, ct);

    [Fact]
    public async Task RequestShapeAndRetry()
    {
        _fake.Replies.Enqueue((200, "这不是 JSON", 0));
        _fake.Replies.Enqueue((200, "```json\n{\"headline\": \"好\"}\n```", 0));
        var r = await Call();
        T.SameJson(T.J("""{"headline": "好"}"""), r.Output);
        Assert.Equal(2, r.Usage.Count);
        var (first, second) = (_fake.Requests[0], _fake.Requests[1]);
        Assert.Equal("/chat/completions", first.Path);
        Assert.Equal("Bearer sk-test", first.Auth);
        Assert.Equal("application/json", first.ContentType);
        T.SameJson(T.J("""{"type": "json_object"}"""), first.Body["response_format"]);
        Assert.Equal(0.2, Py.Num(first.Body["temperature"]));
        Assert.Equal(8000, Py.Num(first.Body["max_tokens"]));
        Assert.Equal("deepseek-chat", Py.S(first.Body["model"]));
        // 证据包按 Python json.dumps(ensure_ascii=False) 的格式发送
        Assert.Equal("{\"items\": [], \"中文\": \"是\"}", Py.S(first.Body["messages"]![1]!["content"]));
        Assert.StartsWith("{\"model\": \"deepseek-chat\", \"messages\": [{\"role\": \"system\"", first.BodyText);
        var msgs = (JsonArray)second.Body["messages"]!;
        Assert.Equal(4, msgs.Count); // 带上了上次的错误输出和纠正要求
        Assert.Equal("这不是 JSON", Py.S(msgs[2]!["content"]));
        Assert.Contains("不是合法 JSON", Py.S(msgs[3]!["content"]));
    }

    [Fact]
    public async Task TwoBadOutputsThrow()
    {
        _fake.Replies.Enqueue((200, "不是 JSON", 0));
        _fake.Replies.Enqueue((200, "还不是", 0));
        var e = await Assert.ThrowsAsync<LlmException>(() => Call());
        Assert.Contains("不是合法 JSON", e.Message);
    }

    [Fact]
    public async Task ReasonerHasNoJsonMode()
    {
        _fake.Replies.Enqueue((200, "{}", 0));
        await Call(model: "deepseek-reasoner");
        Assert.False(_fake.Requests[0].Body.ContainsKey("response_format"));
        Assert.False(_fake.Requests[0].Body.ContainsKey("temperature"));
    }

    [Theory]
    [InlineData(401, "API Key 不对")]
    [InlineData(402, "账户余额不足")]
    [InlineData(429, "请求太频繁")]
    [InlineData(500, "模型接口返回错误 500")]
    public async Task HttpErrorMessages(int status, string hint)
    {
        _fake.Replies.Enqueue((status, "invalid api key", 0));
        var e = await Assert.ThrowsAsync<LlmException>(() => Call(key: "bad"));
        Assert.Contains(hint, e.Message);
        Assert.Contains("invalid api key", e.Message);
        Assert.Equal(status, e.StatusCode);
    }

    [Fact]
    public async Task ConnectionRefused()
    {
        var port = _fake.Port;
        _fake.Dispose();
        var e = await Assert.ThrowsAsync<LlmException>(() => Call(baseUrl: $"http://127.0.0.1:{port}"));
        Assert.StartsWith($"连不上模型接口 http://127.0.0.1:{port}/chat/completions", e.Message);
    }

    [Fact]
    public async Task DnsFailureFallsBackToIpv4()
    {
        _fake.Replies.Enqueue((200, "{\"headline\": \"ok\"}", 0));
        var calls = new List<AddressFamily>();
        var messages = new List<string>();
        var real = LlmClient.Resolve;
        LlmClient.Resolve = (host, family, ct) =>
        {
            lock (calls) calls.Add(family);
            if (calls.Count == 1) throw new SocketException((int)SocketError.HostNotFound); // 第一次模拟路由器 DNS 出错
            // 两个回环地址都给：测试服务监听 localhost，有的系统只绑定 ::1（GitHub 的 Ubuntu 机器），有的只绑定 127.0.0.1
            return Task.FromResult(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback });
        };
        try
        {
            var r = await Call(progress: new SyncProgress(messages)); // localhost 也走 Resolve，可以模拟解析失败
            T.SameJson(T.J("""{"headline": "ok"}"""), r.Output);
        }
        finally
        {
            LlmClient.Resolve = real;
        }
        Assert.Equal([AddressFamily.Unspecified, AddressFamily.InterNetwork], calls); // 第二次已改为只查 IPv4
        Assert.True(LlmClient.Ipv4Only);
        Assert.Equal(["域名解析失败，改为只查 IPv4 地址后重试…"], messages);
    }

    [Fact]
    public async Task DnsFailureGivesUpAfterFourTries()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var (real, realDelay) = (LlmClient.Resolve, LlmClient.Delay);
        LlmClient.Resolve = (_, _, _) => { Interlocked.Increment(ref calls); throw new SocketException((int)SocketError.TryAgain); };
        LlmClient.Delay = (t, _) => { delays.Add(t); return Task.CompletedTask; };
        try
        {
            var e = await Assert.ThrowsAsync<LlmException>(() => Call());
            Assert.Contains("连不上模型接口", e.Message);
        }
        finally
        {
            (LlmClient.Resolve, LlmClient.Delay) = (real, realDelay);
        }
        Assert.Equal(4, calls);
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)], delays); // 第 1 次失败后立即改 IPv4 重试，之后隔 4、6 秒
    }

    [Fact]
    public async Task TimeoutAndCancel()
    {
        _fake.Replies.Enqueue((200, "{}", 5000));
        var e = await Assert.ThrowsAsync<LlmException>(() => Call(timeout: TimeSpan.FromMilliseconds(300)));
        Assert.Contains("没有响应", e.Message);

        _fake.Replies.Enqueue((200, "{}", 5000));
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(ct: cts.Token));
    }

    sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) { lock (sink) sink.Add(value); }
    }
}
