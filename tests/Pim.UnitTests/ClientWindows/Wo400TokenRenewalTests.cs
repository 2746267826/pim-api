using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// WO-ISSUES-396-400-20261007 · REQ-5（#400）注入用例：
/// <list type="number">
///   <item><description>AC-5.3：刷新接口连续失败时不得丢队列、不得登出，恢复后必须把积压数据补传；</description></item>
///   <item><description>AC-5.1（注入等价）：令牌到期前主动续期 —— 连续 3 个令牌周期内业务接口的 401 计数为 0；</description></item>
///   <item><description>并发 401 只触发一次真实刷新（旧实现用 volatile bool 直接放行后来者，
///     被放行的请求拿着旧令牌拿到 401，上传循环把整批数据当"客户端错误"丢掉）。</description></item>
/// </list>
/// 传输层通过桩 handler 注入；时钟通过 <see cref="TimeProvider"/> 注入。桩 handler 拦截全部请求，
/// 因此测试不接触真实网络（也不调用 <c>SetBaseUrl</c>：那会重建 HttpClient 并销毁桩）。
/// </summary>
public sealed class Wo400TokenRenewalTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tokenPath;
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 6, 0, 0, TimeSpan.Zero);

    public Wo400TokenRenewalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pim-wo400-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _tokenPath = Path.Combine(_dir, "token.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Ac5_3_RefreshFailsRepeatedly_KeepsQueueAndLogin_ThenRetransmitsEverything()
    {
        var clock = new MutableTimeProvider(T0);
        var handler = new AuthStubHandler(() => clock.GetUtcNow()) { RefreshFailuresRemaining = 3 };
        var api = CreateApi(handler);
        var auth = new AuthService(api, _tokenPath, clock);
        var tracker = CreateTracker(api, clock);

        await auth.LoginAsync("u", "p");                       // 令牌有效期 15 分钟（T0+15）
        await tracker.DoPollAsync();                           // 建立基线
        clock.Advance(TimeSpan.FromMinutes(40));               // 进程静默 40 分钟（令牌早已过期）
        await tracker.DoPollAsync();                           // 产出 gap 补齐分片并进入待上报队列

        // 令牌已过期且续期（刷新接口）连续失败：既不得登出，也不得把队列丢掉。
        Assert.True(auth.IsAccessTokenRenewalDue);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await tracker.FlushQueueAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(tracker.UploadFailures > 0, "续期失败时上传必须如实记录失败");
        Assert.Equal(0, handler.SuccessfulUploads);            // 一次都没发出去（没有拿过期令牌硬打）
        Assert.Equal(3, handler.RefreshCalls);                 // 每次尝试各续期一次，说明队列一直在
        Assert.True(auth.HasSavedToken, "刷新失败不得把用户登出（凭证文件必须还在）");
        Assert.False(string.IsNullOrEmpty(auth.CurrentAccessToken), "刷新失败不得清空 access token");

        // 刷新恢复 → 积压数据必须被补传，且一条不少（含静默区间补齐的 gap 分片）。
        handler.RefreshFailuresRemaining = 0;
        await tracker.FlushQueueAsync(TimeSpan.FromSeconds(5));

        Assert.True(handler.SuccessfulUploads > 0, "刷新恢复后必须把积压数据补传");
        var gaps = handler.AllGapEvents().OrderBy(g => g.Start).ToList();
        Assert.NotEmpty(gaps);
        Assert.Equal(T0, gaps[0].Start);
        Assert.Equal(T0.AddMinutes(40), gaps[^1].End);
        Assert.True(auth.HasSavedToken);
    }

    [Fact]
    public async Task Ac5_1_TokenIsRenewedBeforeExpiry_ThreeTokenCyclesWithoutASingle401()
    {
        // 令牌有效期 15 分钟，连续跑 45 分钟（3 个周期），每 1 分钟一次业务请求。
        // 旧行为是"每周期先 401、再刷新、再重试"（生产实测每 15~16 分钟一轮）；
        // 新行为必须在令牌到期前就换好，因此业务接口一次 401 都不该出现。
        var clock = new MutableTimeProvider(T0);
        var handler = new AuthStubHandler(() => clock.GetUtcNow());
        var api = CreateApi(handler);
        var auth = new AuthService(api, _tokenPath, clock);

        await auth.LoginAsync("u", "p");

        for (int minute = 0; minute < 45; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await api.PostAsync<ApiResponse<int>>("/pc/tracker/upload",
                new TrackerEventsUploadRequest { DeviceId = "PC-01", Events = new List<TrackerEventForUpload>() });
        }

        Assert.Equal(0, handler.BusinessUnauthorized);
        Assert.True(handler.RefreshCalls >= 3, $"三个令牌周期至少各续期一次，实际 {handler.RefreshCalls}");
        Assert.Equal(45, handler.SuccessfulUploads);
        Assert.True(auth.HasSavedToken);
    }

    [Fact]
    public async Task Concurrent401s_TriggerExactlyOneRealRefresh()
    {
        var clock = new MutableTimeProvider(T0);
        var handler = new AuthStubHandler(() => clock.GetUtcNow()) { BusinessResponseDelayMs = 50 };
        var api = CreateApi(handler);
        var auth = new AuthService(api, _tokenPath, clock);
        await auth.LoginAsync("u", "p");

        // 服务端侧令牌被轮换（客户端仍持有旧令牌且尚未进入临期窗口）→ 8 个并发请求同时拿到 401。
        handler.InvalidateServerSideToken();

        var tasks = Enumerable.Range(0, 8).Select(_ =>
            api.PostAsync<ApiResponse<int>>("/pc/tracker/upload",
                new TrackerEventsUploadRequest { DeviceId = "PC-01", Events = new List<TrackerEventForUpload>() }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(8, handler.BusinessUnauthorized);   // 每个请求各吃一次 401
        Assert.Equal(1, handler.RefreshCalls);           // 8 个并发 401 只续期一次
        Assert.Equal(8, handler.SuccessfulUploads);      // 续期后全部重试成功，没有一个被丢弃
    }

    // ---- helpers ------------------------------------------------------------------

    private static ApiClient CreateApi(AuthStubHandler handler) => new(() => handler);

    private static NativeTrackerService CreateTracker(ApiClient api, TimeProvider clock)
    {
        var logger = new TrackerLogger(1);
        var statePath = Path.Combine(Path.GetTempPath(), "pim-wo400-state-" + Guid.NewGuid().ToString("N") + ".json");
        return new NativeTrackerService(
            api,
            new TrackerConfig { Enabled = true, GapThresholdSeconds = 60 },
            windowResolver: new NullWindowResolver(),
            idleDetector: new NullIdleDetector(),
            bridge: new BrowserBridgeService(15698, logger),
            logger: logger,
            stateManager: new TrackerStateManager(statePath),
            timeProvider: clock);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableTimeProvider(DateTimeOffset now) => _now = now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class NullWindowResolver : IWindowResolver
    {
        public TrackerWindowInfo? GetForegroundWindowInfo() => null;
    }

    private sealed class NullIdleDetector : IIdleDetector
    {
        public TimeSpan GetIdleDuration() => TimeSpan.Zero;
        public bool IsScreenOff() => false;
    }

    /// <summary>
    /// 桩 handler：/auth/login 发一个有效期 15 分钟的令牌；/auth/refresh 可配置为连续失败（传输层异常）；
    /// 业务接口校验 Bearer 是否等于服务端当前有效令牌，不等则回 401 —— 这就是真实的令牌过期形态。
    /// </summary>
    private sealed class AuthStubHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private int _tokenSeq;
        private string _serverToken = string.Empty;

        public AuthStubHandler(Func<DateTimeOffset> clock) => Clock = clock;

        public Func<DateTimeOffset> Clock { get; }
        public int RefreshFailuresRemaining { get; set; }
        public int BusinessResponseDelayMs { get; set; }
        public int RefreshCalls { get; private set; }
        public int BusinessUnauthorized { get; private set; }
        public int SuccessfulUploads { get; private set; }
        public List<string> UploadBodies { get; } = new();

        public void InvalidateServerSideToken()
        {
            lock (_lock) _serverToken = "server-side-rotated";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return IssueTokens();
            }

            if (path.EndsWith("/auth/refresh", StringComparison.Ordinal))
            {
                lock (_lock) RefreshCalls++;
                if (RefreshFailuresRemaining > 0)
                {
                    RefreshFailuresRemaining--;
                    // 网络未就绪 / 服务端不可用：传输层异常，不是 401。
                    throw new HttpRequestException("refresh endpoint unavailable (simulated)");
                }

                return IssueTokens();
            }

            if (BusinessResponseDelayMs > 0)
            {
                await Task.Delay(BusinessResponseDelayMs, cancellationToken);
            }

            string current;
            lock (_lock) current = _serverToken;
            var bearer = request.Headers.Authorization?.Parameter;
            if (string.IsNullOrEmpty(bearer) || bearer != current)
            {
                lock (_lock) BusinessUnauthorized++;
                return Error(HttpStatusCode.Unauthorized);
            }

            if (path.EndsWith("/pc/tracker/upload", StringComparison.Ordinal))
            {
                lock (_lock) { SuccessfulUploads++; UploadBodies.Add(body); }
            }

            return Json(HttpStatusCode.OK, "{\"code\":0,\"message\":\"\",\"data\":0}");
        }

        private HttpResponseMessage IssueTokens()
        {
            var token = "access-" + Interlocked.Increment(ref _tokenSeq);
            lock (_lock) _serverToken = token;
            var payload = new
            {
                code = 0,
                message = "",
                data = new
                {
                    accessToken = token,
                    refreshToken = "refresh-" + _tokenSeq,
                    expiresAt = Clock().AddMinutes(15),
                    user = new { id = "u1", username = "u", displayName = "U", role = "user" }
                }
            };
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(payload));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error(HttpStatusCode status)
            => Json(status, "{\"code\":401,\"message\":\"unauthorized\",\"data\":null}");

        public List<(DateTimeOffset Start, DateTimeOffset End)> AllGapEvents()
        {
            var result = new List<(DateTimeOffset, DateTimeOffset)>();
            foreach (var body in UploadBodies)
            {
                if (string.IsNullOrWhiteSpace(body)) continue;
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("events", out var events)) continue;
                foreach (var ev in events.EnumerateArray())
                {
                    if (!ev.TryGetProperty("eventType", out var type) || type.GetString() != "gap") continue;
                    var raw = ev.GetProperty("rawJson");
                    if (raw.ValueKind == JsonValueKind.String)
                    {
                        using var rawDoc = JsonDocument.Parse(raw.GetString()!);
                        result.Add((rawDoc.RootElement.GetProperty("gapStart").GetDateTimeOffset(),
                                    rawDoc.RootElement.GetProperty("gapEnd").GetDateTimeOffset()));
                    }
                    else
                    {
                        result.Add((raw.GetProperty("gapStart").GetDateTimeOffset(),
                                    raw.GetProperty("gapEnd").GetDateTimeOffset()));
                    }
                }
            }

            return result;
        }
    }
}
