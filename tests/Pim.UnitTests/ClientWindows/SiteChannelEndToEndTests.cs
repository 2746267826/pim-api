using System;
using System.Net;
using System.Text;
using System.Text.Json;
using Pim.Client.Core;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 站点级通道的进程内端到端：真实 HttpListener 桥接（隔离端口）→ 有界通道 →
/// SiteUploadLoop → 桩 API 服务器。仅 Windows 本地运行（CI 不执行，避免
/// HttpListener URL ACL 与端口占用问题）。
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class SiteChannelEndToEndTests : IDisposable
{
    private const int BridgePort = 15677;
    private const int StubPort = 15678;

    private HttpListener? _stub;
    private NativeTrackerService? _tracker;

    [Fact]
    public async Task BridgeToUploadLoop_DeliversNormalizedEventsToServer()
    {
        // Windows-only, local-only：CI 的过滤清单不包含本类，非 Windows 跳过。
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("CI") == "true")
        {
            return;
        }

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _stub = new HttpListener();
        // 注意：ApiClient.NormalizeServerUrl 会把 localhost 改写成 127.0.0.1，
        // 因此桩必须按 127.0.0.1 前缀监听，否则 http.sys 会以 400 拒绝 Host
        // 不匹配的请求（且请求不会进入处理器）。前缀必须以 / 结尾，故监听根
        // 路径、在处理器里按路径过滤。
        _stub.Prefixes.Add($"http://127.0.0.1:{StubPort}/");
        _stub.Start();
        _ = Task.Run(async () =>
        {
            while (_stub != null && _stub.IsListening)
            {
                var ctx = await _stub.GetContextAsync();
                seen.Enqueue($"{ctx.Request.HttpMethod} {ctx.Request.Url?.PathAndQuery}");
                if (ctx.Request.Url?.AbsolutePath?.EndsWith("/browser-tt/upload") != true)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                received.TrySetResult(body);
                var bytes = Encoding.UTF8.GetBytes("""{"code":0,"data":1}""");
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 200;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var api = new ApiClient();
        api.SetBaseUrl($"http://localhost:{StubPort}");
        var config = new TrackerConfig
        {
            Enabled = true,
            BrowserBridgePort = BridgePort,
            UploadIntervalSeconds = 5,
            PollIntervalSeconds = 60,
            HealthReportIntervalSeconds = 3600,
        };
        _tracker = new NativeTrackerService(api, config);
        _tracker.Start();

        // Give the bridge a moment to bind, then push a site batch the way the
        // Time Tracker fork does.
        await Task.Delay(1500);
        using (var client = new HttpClient())
        {
            var payload = """
[
  {"kind":"focus","host":"GitHub.COM","startMs":1000,"endMs":6001,"date":"2026-09-03"},
  {"kind":"bogus","host":"ignored.example"},
  {"kind":"tick","host":"github.com","startMs":2000,"durationMs":30000,"date":"2026-09-03"}
]
""";
            var content = new StringContent("""{"events":""" + payload + """}""", Encoding.UTF8, "application/json");
            var res = await client.PostAsync($"http://localhost:{BridgePort}/browser/site/heartbeat", content);
            Assert.True(res.IsSuccessStatusCode, $"site heartbeat failed: {res.StatusCode}");
        }

        var winner = await Task.WhenAny(received.Task, Task.Delay(20000));
        if (winner != received.Task)
        {
            Assert.Fail($"""upload loop did not deliver in time. received={_tracker.SiteEventsReceived} dropped={_tracker.SiteEventsDropped} uploaded={_tracker.SiteEventsUploaded} failures={_tracker.SiteUploadFailures} lastError={_tracker.SiteLastError ?? "none"} siteConnected={_tracker.SiteConnected} seen=[{string.Join(" | ", seen)}]""");
        }
        var body = received.Task.Result;
        Assert.Contains("api/v1", api.CurrentBaseUrl); // sanity: stub base in use

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(Environment.MachineName, root.GetProperty("deviceId").GetString());
        var events = root.GetProperty("events");
        Assert.Equal(2, events.GetArrayLength());
        var first = events[0];
        Assert.Equal("focus", first.GetProperty("kind").GetString());
        Assert.Equal("github.com", first.GetProperty("host").GetString());
        Assert.Equal(5001, first.GetProperty("durationMs").GetInt64());
        // 计数器在响应处理完成后自增，轮询等待避免与桩的 TCS 完成时序竞争
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_tracker.SiteEventsUploaded < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }
        Assert.True(_tracker.SiteEventsUploaded >= 2, $"uploaded counter {_tracker.SiteEventsUploaded}");
        Assert.True(_tracker.SiteConnected, "site channel should report connected");
    }

    public void Dispose()
    {
        _tracker?.Dispose();
        try { _stub?.Stop(); _stub?.Close(); } catch { }
    }
}
