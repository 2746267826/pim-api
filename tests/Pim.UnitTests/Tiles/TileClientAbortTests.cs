using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Tiles;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Tiles;

/// <summary>
/// issue #299 回归：客户端中断（拖动/缩放导致浏览器取消在途瓦片请求）不是服务端故障。
/// 端到端断言两件事——请求不得被记为 5xx，且指标里不得出现 5xx 脉冲。
/// 测试通过 <c>WebApplicationFactory</c> 走完整管道（Serilog 请求日志 + ExceptionMiddleware + 指标中间件），
/// 确保断言的是真实链路行为，而不是某一层的局部行为。
/// </summary>
public class TileClientAbortTests
{
    private readonly ITestOutputHelper _output;

    public TileClientAbortTests(ITestOutputHelper output) => _output = output;

    private static WebApplicationFactory<Program> CreateFactory(string cacheDir, HttpMessageHandler handler)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.UseSetting("PIM_OPS_KEY", "test-ops-key");
            b.UseSetting("Tiles:CacheDirectory", cacheDir);
            b.ConfigureServices(services =>
                services.AddHttpClient<TileService>().ConfigurePrimaryHttpMessageHandler(() => handler));
        });

    /// <summary>
    /// 读取瓦片端点的指标计数。
    ///
    /// Prometheus 默认注册表是**进程级**的，同一测试进程里的其它用例（含并行执行的）
    /// 都会写入同一批计数器，因此这里只能比较"测试动作前后"的增量，不能断言绝对值，
    /// 否则用例之间会互相污染、按执行顺序随机失败。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, double>> TileMetricsAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "test-ops-key");
        var text = await client.GetStringAsync("/metrics");

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("http_requests_received_total", StringComparison.Ordinal))
                continue;
            if (!line.Contains("/api/v1/tiles/{z}/{x}/{y}.png", StringComparison.Ordinal))
                continue;

            var space = line.LastIndexOf(' ');
            if (space < 0)
                continue;

            var code = line.Split("code=\"", 2)[1].Split('"')[0];
            result[code] = result.TryGetValue(code, out var current)
                ? current + double.Parse(line[(space + 1)..], System.Globalization.CultureInfo.InvariantCulture)
                : double.Parse(line[(space + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }

        _output.WriteLine("tile metrics: " + string.Join(", ", result.Select(pair => $"{pair.Key}={pair.Value}")));
        return result;
    }

    private static double CountFor(IReadOnlyDictionary<string, double> metrics, string code)
        => metrics.TryGetValue(code, out var value) ? value : 0d;

    /// <summary>
    /// 轮询直到指定状态码的增量达到期望值。
    ///
    /// 指标是在请求收尾的 finally 里写入的，与"客户端收到取消/响应"之间存在极短的竞态窗口，
    /// 固定 sleep 要么拖慢用例、要么在高负载 CI 上偶发读到旧快照。这里改成轮询，
    /// 并且把"该请求已被计入"当作后续断言的前提条件 —— 只有确认请求已结算，
    /// "没有出现 5xx" 才是有意义的结论，否则可能只是尚未写入。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, double>> WaitForTileMetricAsync(
        WebApplicationFactory<Program> factory,
        IReadOnlyDictionary<string, double> before,
        string code,
        double expectedDelta,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        IReadOnlyDictionary<string, double> current = before;

        while (DateTime.UtcNow < deadline)
        {
            current = await TileMetricsAsync(factory);
            if (CountFor(current, code) - CountFor(before, code) >= expectedDelta)
                return current;

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out waiting for tile metric code=\"{code}\" to increase by {expectedDelta} within {timeout}.");
        return current;
    }

    /// <summary>
    /// 客户端中断时，日志与指标都不得出现 5xx。
    /// 期望状态码是 499（nginx 惯例的 Client Closed Request）：Serilog 默认分级"&gt;499 才算 Error"，
    /// 因此 499 不会落成 Error 级请求日志，也不会命中 `code=~"5.."` 的 5xx 告警规则。
    /// </summary>
    [Fact]
    public async Task Aborted_tile_request_is_not_recorded_as_server_error()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "pim-tile-abort-" + Guid.NewGuid().ToString("N"));
        var enteredUpstream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = CreateFactory(cacheDir, new HangingHandler(enteredUpstream));
        var client = factory.CreateClient();
        var before = await TileMetricsAsync(factory);

        using var cts = new CancellationTokenSource();
        var requestTask = client.GetAsync("/api/v1/tiles/9/1/1.png", cts.Token);

        // 等上游请求真正发出后再中断，确保取消发生在下载途中（复现 issue 的场景）。
        await enteredUpstream.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);

        // 先确认这一请求已被计入（499 增量达到 1），再断言它没有以 5xx 计入。
        var after = await WaitForTileMetricAsync(factory, before, "499", expectedDelta: 1, TimeSpan.FromSeconds(15));

        Assert.Equal(1d, CountFor(after, "499") - CountFor(before, "499"));
        var serverErrors = after.Where(pair => pair.Key.StartsWith('5'))
            .Sum(pair => pair.Value - CountFor(before, pair.Key));
        Assert.Equal(0d, serverErrors);
    }

    /// <summary>
    /// 上游故障必须仍然是服务端错误：修复不能把真实故障一起"洗白"。
    /// 上游 404 → 502（既有行为，回归保护）。
    /// </summary>
    [Fact]
    public async Task Upstream_failure_still_reports_502()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "pim-tile-abort-" + Guid.NewGuid().ToString("N"));
        using var factory = CreateFactory(cacheDir, new StaticStatusHandler(HttpStatusCode.NotFound));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tiles/9/1/1.png");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    /// 上游不可达（非取消类异常）仍是 500，并且指标里必须如实记成 500
    /// —— 指标中间件要能看见异常转换后的真实状态码，否则 5xx 告警永远不响。
    /// </summary>
    [Fact]
    public async Task Unexpected_upstream_failure_is_recorded_as_500_in_metrics()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "pim-tile-abort-" + Guid.NewGuid().ToString("N"));
        using var factory = CreateFactory(cacheDir, new ThrowingHandler());
        var client = factory.CreateClient();
        var before = await TileMetricsAsync(factory);

        var response = await client.GetAsync("/api/v1/tiles/9/1/1.png");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var after = await WaitForTileMetricAsync(factory, before, "500", expectedDelta: 1, TimeSpan.FromSeconds(15));
        Assert.Equal(1d, CountFor(after, "500") - CountFor(before, "500"));
    }

    /// <summary>
    /// 指标中间件被移到异常中间件外侧（早于 UseRouting）后，`endpoint` 标签仍须正确填充。
    /// prometheus-net 靠自身的 CaptureRouteDataMiddleware 捕获路由数据，但这是**外部依赖的行为**，
    /// 一旦其版本行为变化，指标会静默退化成 endpoint=""，Grafana 按 endpoint 聚合的面板随之失效。
    /// 这里显式守住该契约。
    /// </summary>
    [Fact]
    public async Task Tile_metric_keeps_endpoint_label_after_middleware_reorder()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "pim-tile-abort-" + Guid.NewGuid().ToString("N"));
        using var factory = CreateFactory(cacheDir, new StaticStatusHandler(HttpStatusCode.OK));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PIM-Ops-Key", "test-ops-key");

        await client.GetAsync("/api/v1/tiles/9/3/3.png");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var text = await client.GetStringAsync("/metrics");
            var labelled = text.Split('\n').Any(line =>
                line.StartsWith("http_requests_received_total", StringComparison.Ordinal)
                && line.Contains("endpoint=\"/api/v1/tiles/{z}/{x}/{y}.png\"", StringComparison.Ordinal));
            if (labelled)
                return;

            await Task.Delay(100);
        }

        Assert.Fail("Tile requests never appeared with a non-empty endpoint label after the metrics middleware reorder.");
    }

    private sealed class HangingHandler(TaskCompletionSource enteredUpstream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            enteredUpstream.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable: Task.Delay(Timeout.Infinite) only completes via cancellation");
        }
    }

    private sealed class StaticStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("not an image") });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("upstream exploded");
    }
}
