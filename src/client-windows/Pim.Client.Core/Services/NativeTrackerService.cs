using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using Pim.Client.Core.Models;
using Pim.Client.Core.Utils;

namespace Pim.Client.Core.Services;

public sealed class NativeTrackerService : IDisposable
{
    private readonly ApiClient _api;
    private readonly TrackerConfig _config;
    private readonly IWindowResolver _windowResolver;
    private readonly IIdleDetector _idleDetector;
    private readonly BrowserBridgeService _bridge;
    private readonly TrackerSessionManager _sessionManager;
    private readonly TrackerStateManager _stateManager;
    private readonly TrackerLogger _logger;
    /// <summary>
    /// 采集循环使用的时钟。生产用系统时钟；测试注入固定时钟以验证"休眠/断档补齐"这类时间相关行为
    /// （WO-ISSUES-396-400-20261007 §七.9 允许为 src/client-windows 做时钟注入）。
    /// </summary>
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<TrackerWindowInfo> _windowChannel = Channel.CreateUnbounded<TrackerWindowInfo>();
    private readonly ConcurrentQueue<TrackerEventForUpload> _uploadQueue = new();
    private readonly ConcurrentQueue<SiteEventDto> _siteUploadQueue = new();
    private readonly object _statsLock = new();
    private readonly object _siteStatsLock = new();
    private long _pollCount;
    private long _eventsUploaded;
    private long _uploadFailures;
    private long _siteEventsUploaded;
    private long _siteUploadFailures;
    private string? _lastError;
    private string? _siteLastError;
    private bool _hookActive = true;
    private bool _running;
    private Task? _pollTask;
    private Task? _hookTask;
    private Task? _uploadTask;
    private Task? _healthTask;
    private Task? _browserTask;
    private Task? _siteUploadTask;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastPollTime;
    private DateTimeOffset _lastEmittedEventEnd = DateTimeOffset.MinValue;
    private TrackerWindowInfo? _lastWindow;
    private IntPtr _hookHandle = IntPtr.Zero;
    private IntPtr _hookHandle2 = IntPtr.Zero;
    private Win32Hook.WinEventProc? _hookCallback;

    public Action<string>? Log { get; set; }

    public long PollCount { get { lock (_statsLock) return _pollCount; } }
    public long EventsUploaded { get { lock (_statsLock) return _eventsUploaded; } }
    public long UploadFailures { get { lock (_statsLock) return _uploadFailures; } }
    public string? LastError { get { lock (_statsLock) return _lastError; } }
    public bool HookActive { get { lock (_statsLock) return _hookActive; } }
    public long SessionsCreated => _sessionManager.SessionsCreated;
    public bool BrowserConnected => _bridge.IsConnected;
    public double? BrowserHeartbeatAgeSeconds
    {
        get
        {
            var hb = _bridge.LastHeartbeatTime;
            if (hb == DateTimeOffset.MinValue) return null;
            return (DateTimeOffset.UtcNow - hb).TotalSeconds;
        }
    }
    public IReadOnlyList<BrowserConnection> GetBrowserConnections() => _bridge.GetConnectionsSnapshot();
    public IReadOnlyDictionary<string, BrowserConnection> BrowserConnections => _bridge.Connections;

    // —— 站点级数据通道（Time Tracker fork） ——
    public bool SiteConnected => _bridge.IsSiteConnected;
    public long SiteEventsReceived => _bridge.SiteEventsReceived;
    public long SiteEventsDropped => _bridge.SiteEventsDropped;
    public long SiteEventsUploaded { get { lock (_siteStatsLock) return _siteEventsUploaded; } }
    public long SiteUploadFailures { get { lock (_siteStatsLock) return _siteUploadFailures; } }
    public string? SiteLastError { get { lock (_siteStatsLock) return _siteLastError; } }
    public double? SiteLastEventAgeSeconds
    {
        get
        {
            var t = _bridge.SiteLastBatchTime;
            if (t is null) return null;
            return (DateTimeOffset.UtcNow - t.Value).TotalSeconds;
        }
    }

    public NativeTrackerService(
        ApiClient api,
        TrackerConfig? config = null,
        IWindowResolver? windowResolver = null,
        IIdleDetector? idleDetector = null,
        BrowserBridgeService? bridge = null,
        TrackerLogger? logger = null,
        TrackerStateManager? stateManager = null,
        TimeProvider? timeProvider = null)
    {
        _api = api;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _config = config ?? new TrackerConfig();
        _windowResolver = windowResolver ?? new DefaultWindowResolver();
        _idleDetector = idleDetector ?? new WindowsIdleDetector();
        _logger = logger ?? new TrackerLogger(_config.LogRetentionDays);
        _bridge = bridge ?? new BrowserBridgeService(_config.BrowserBridgePort, _logger);
        _stateManager = stateManager ?? new TrackerStateManager();
        _sessionManager = new TrackerSessionManager(_config, _logger);
        _sessionManager.SessionClosed += OnSessionClosed;
        _startedAt = _timeProvider.GetUtcNow();
        _lastPollTime = _startedAt;
    }

    public void Start()
    {
        if (!_config.Enabled || _running)
        {
            if (!_config.Enabled) _logger.Info("Tracker", "Tracker disabled via config");
            return;
        }

        _running = true;
        var now = _timeProvider.GetUtcNow();
        _startedAt = now;

        // Startup gap detection: check if there is an unrecorded offline gap since last daemon exit/shutdown
        var persisted = _stateManager.LoadState();
        if (persisted?.LastPollTime is not null)
        {
            var offlineDuration = now - persisted.LastPollTime.Value;
            if (offlineDuration.TotalSeconds > _config.GapThresholdSeconds)
            {
                _logger.Info("Tracker", $"Offline gap detected on startup: {offlineDuration.TotalSeconds:F1}s since last exit");
                _sessionManager.HandleGap(persisted.LastPollTime.Value, now);
                EnqueueGapChunks(persisted.LastPollTime.Value, now, isStartup: true);
            }
        }

        _lastPollTime = now;
        _stateManager.SaveState(now, now, Environment.MachineName);

        _logger.Info("Tracker", $"Starting NativeTrackerService poll={_config.PollIntervalSeconds}s idle={_config.IdleThresholdSeconds}s gap={_config.GapThresholdSeconds}s port={_config.BrowserBridgePort}");

        try { _bridge.Start(); _logger.Info("Tracker", "BrowserBridge started"); } catch (Exception ex) { _logger.Error("Tracker", "BrowserBridge failed to start", ex); }

        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _hookTask = Task.Run(() => HookLoopAsync(_cts.Token));
        _uploadTask = Task.Run(() => UploadLoopAsync(_cts.Token));
        _healthTask = Task.Run(() => HealthLoopAsync(_cts.Token));
        _siteUploadTask = Task.Run(() => SiteUploadLoopAsync(_cts.Token));
        _browserTask = Task.Run(() => BrowserLoopAsync(_cts.Token));

        _logger.Info("Tracker", "NativeTrackerService started");
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_running) return;
        _running = false;

        var now = _timeProvider.GetUtcNow();
        _sessionManager.CloseCurrent(now);
        _stateManager.SaveState(now, now, Environment.MachineName);

        try
        {
            await FlushQueueAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn("Tracker", $"Flush on stop failed: {ex.Message}");
        }

        try { _cts.Cancel(); } catch { }
        _bridge.Stop();
        _logger.Info("Tracker", "NativeTrackerService stopped");
    }

    public void Stop()
    {
        try
        {
            StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch
        {
            try { _sessionManager.CloseCurrent(DateTimeOffset.UtcNow); } catch { }
            try { _cts.Cancel(); } catch { }
            try { _bridge.Stop(); } catch { }
        }
    }

    public void HandleSuspend()
    {
        _logger.Info("Tracker", "Suspend signal received, closing current session and flushing");
        var now = _timeProvider.GetUtcNow();
        _sessionManager.CloseCurrent(now);
        _stateManager.SaveState(now, now, Environment.MachineName);
        try
        {
            FlushQueueAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warn("Tracker", $"Suspend flush failed: {ex.Message}");
        }
    }

    public void HandleResume()
    {
        var now = _timeProvider.GetUtcNow();
        _logger.Info("Tracker", $"Resume signal received at {now:O}");
        var persisted = _stateManager.LoadState();
        if (persisted?.LastPollTime is not null)
        {
            var elapsed = now - persisted.LastPollTime.Value;
            if (elapsed.TotalSeconds > _config.GapThresholdSeconds)
            {
                _sessionManager.HandleGap(persisted.LastPollTime.Value, now);
                EnqueueGapChunks(persisted.LastPollTime.Value, now, isStartup: false);
            }
        }
        _lastPollTime = now;
        _stateManager.SaveState(now, now, Environment.MachineName);
    }

    public async Task FlushQueueAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        while (!_uploadQueue.IsEmpty && !linkedCts.IsCancellationRequested)
        {
            var batch = new List<TrackerEventForUpload>();
            while (batch.Count < _config.UploadBatchSize && _uploadQueue.TryDequeue(out var ev))
            {
                batch.Add(ev);
            }

            if (batch.Count == 0) break;

            var req = new TrackerEventsUploadRequest
            {
                DeviceId = Environment.MachineName,
                Events = batch
            };

            try
            {
                var resp = await _api.PostAsync<ApiResponse<int>>("/pc/tracker/upload", req, linkedCts.Token).ConfigureAwait(false);
                if (resp is not null)
                {
                    lock (_statsLock)
                    {
                        _eventsUploaded += batch.Count;
                        _lastError = null;
                    }
                }
                else
                {
                    lock (_statsLock)
                    {
                        _uploadFailures++;
                        _lastError = "Flush upload returned null";
                    }
                    // 失败必须把这批放回队列：FlushQueueAsync 是**进程即将停止**时（休眠/关机/Stop）
                    // 的最后一次送数机会，取出来又不还回去等于直接丢数据
                    // （WO-ISSUES-396-400-20261007 REQ-5 反面行为：不得丢弃待上报数据）。
                    foreach (var ev in batch) _uploadQueue.Enqueue(ev);
                    break;
                }
            }
            catch (Exception ex)
            {
                lock (_statsLock)
                {
                    _uploadFailures++;
                    _lastError = ex.Message;
                }
                foreach (var ev in batch) _uploadQueue.Enqueue(ev);
                break;
            }
        }
    }

    private void OnSessionClosed(TrackerSession session)
    {
        var evs = SessionToEvents(session, session.EndedAt ?? DateTimeOffset.UtcNow);
        foreach (var e in evs)
        {
            EnqueueEventSafely(e);
        }
    }

    private void EnqueueEventSafely(TrackerEventForUpload ev)
    {
        if (DateTimeOffset.TryParse(ev.Timestamp, out var ts))
        {
            if (_lastEmittedEventEnd != DateTimeOffset.MinValue && ts < _lastEmittedEventEnd)
            {
                var overlap = (_lastEmittedEventEnd - ts).TotalSeconds;
                if (overlap > 0 && overlap < 0.5)
                {
                    ev.Timestamp = _lastEmittedEventEnd.ToString("O");
                    ev.Duration -= overlap;
                    if (ev.Duration <= 0) return;
                }
            }

            var parsedStart = DateTimeOffset.Parse(ev.Timestamp);
            var end = parsedStart.AddSeconds(ev.Duration);
            if (end > _lastEmittedEventEnd)
            {
                _lastEmittedEventEnd = end;
            }
        }
        _uploadQueue.Enqueue(ev);
    }

    private void EnqueueGapChunks(DateTimeOffset gapStart, DateTimeOffset gapEnd, bool isStartup)
    {
        var cur = gapStart;
        while (cur < gapEnd)
        {
            var nextDayBoundary = BusinessDayUtils.GetNextBusinessDayStart(cur);
            var chunkEnd = gapEnd;
            if (chunkEnd > nextDayBoundary)
                chunkEnd = nextDayBoundary;
            if ((chunkEnd - cur).TotalSeconds > 1800)
                chunkEnd = cur.AddSeconds(1800);

            var segDuration = (chunkEnd - cur).TotalSeconds;
            if (segDuration > 0)
            {
                EnqueueEventSafely(new TrackerEventForUpload
                {
                    Timestamp = cur.ToString("O"),
                    Duration = segDuration,
                    EventType = "gap",
                    IsIdle = false,
                    IsMediaActive = false,
                    Date = BusinessDayUtils.GetBusinessDateString(cur),
                    RawJson = new { gapStart = cur, gapEnd = chunkEnd, isStartup }
                });
            }
            cur = chunkEnd;
        }
    }

    public List<TrackerEventForUpload> SessionToEvents(TrackerSession session, DateTimeOffset endedAt)
    {
        var duration = session.DurationSecs ?? (endedAt - session.StartedAt).TotalSeconds;
        if (duration <= 0) return new List<TrackerEventForUpload>();

        // Idle sessions are single event
        if (session.IsIdle)
        {
            return new List<TrackerEventForUpload>
            {
                new TrackerEventForUpload
                {
                    Timestamp = session.StartedAt.ToString("O"),
                    Duration = duration,
                    EventType = "idle",
                    ExePath = session.ExePath,
                    AppName = session.AppName,
                    DisplayName = session.AppName,
                    WindowTitle = session.WindowTitle,
                    IsIdle = true,
                    IsMediaActive = session.IsMediaActive,
                    Date = BusinessDayUtils.GetBusinessDateString(session.StartedAt),
                    RawJson = new { sessionId = session.Id, isIdle = true }
                }
            };
        }

        var hbForWindow = _bridge.LastHeartbeat;

        // #310：非浏览器会话永远不该产出页面（web-page）记录。会话管理已从源头拒绝
        // 归属，这里再兜一道——历史上已写盘/已入队的会话（旧版本产出的数据）同样要被
        // 拦住，否则升级后仍会继续上传假页面记录。
        var visitsSource = TrackerSessionManager.IsBrowserApp(session.AppName)
            ? session.PageVisits
            : new List<TrackerPageVisit>();

        var validVisits = visitsSource
            .Where(p => !string.IsNullOrWhiteSpace(p.Url) && (p.DurationSecs ?? 0) > 0)
            .OrderBy(p => p.StartedAt)
            .ToList();

        if (validVisits.Count == 0)
        {
            // Non-browser or browser without recorded page visits: single window event
            return new List<TrackerEventForUpload>
            {
                new TrackerEventForUpload
                {
                    Timestamp = session.StartedAt.ToString("O"),
                    Duration = duration,
                    EventType = "window",
                    ExePath = session.ExePath,
                    AppName = session.AppName,
                    DisplayName = session.AppName,
                    WindowTitle = session.WindowTitle,
                    CommandLine = null,
                    IsIdle = false,
                    IsMediaActive = session.IsMediaActive,
                    Date = BusinessDayUtils.GetBusinessDateString(session.StartedAt),
                    RawJson = new { sessionId = session.Id },
                    PageVisitCount = 0,
                    PageVisitDuration = 0,
                    Browser = hbForWindow?.Browser,
                    InstanceId = hbForWindow?.InstanceId
                }
            };
        }

        // Decompose browser session into non-overlapping segments (window and web-page)
        var list = new List<TrackerEventForUpload>();
        var cur = session.StartedAt;

        foreach (var pv in validVisits)
        {
            var pvStart = pv.StartedAt;
            var pvEnd = pv.EndedAt ?? pvStart.AddSeconds(pv.DurationSecs ?? 0);
            if (pvEnd > endedAt) pvEnd = endedAt;

            // Gap before this page visit
            if (pvStart > cur)
            {
                var gapDur = (pvStart - cur).TotalSeconds;
                if (gapDur >= 0.05)
                {
                    list.Add(new TrackerEventForUpload
                    {
                        Timestamp = cur.ToString("O"),
                        Duration = gapDur,
                        EventType = "window",
                        ExePath = session.ExePath,
                        AppName = session.AppName,
                        DisplayName = session.AppName,
                        WindowTitle = session.WindowTitle,
                        IsIdle = false,
                        IsMediaActive = session.IsMediaActive,
                        Date = BusinessDayUtils.GetBusinessDateString(cur),
                        RawJson = new { sessionId = session.Id },
                        Browser = hbForWindow?.Browser,
                        InstanceId = hbForWindow?.InstanceId
                    });
                }
            }

            // The page visit event itself
            var visitStart = pvStart > cur ? pvStart : cur;
            var visitDur = (pvEnd - visitStart).TotalSeconds;
            if (visitDur >= 0.05)
            {
                list.Add(new TrackerEventForUpload
                {
                    Timestamp = visitStart.ToString("O"),
                    Duration = visitDur,
                    EventType = "web-page",
                    ExePath = session.ExePath,
                    AppName = session.AppName,
                    DisplayName = pv.Domain ?? session.AppName,
                    WindowTitle = pv.WindowTitle ?? session.WindowTitle,
                    Url = pv.Url,
                    Domain = pv.Domain,
                    PagePath = null,
                    Audible = hbForWindow?.Audible,
                    Incognito = hbForWindow?.Incognito,
                    TabCount = hbForWindow?.TabCount,
                    IsIdle = false,
                    IsMediaActive = false,
                    Date = BusinessDayUtils.GetBusinessDateString(visitStart),
                    RawJson = new { sessionId = session.Id, pageVisit = pv },
                    Browser = hbForWindow?.Browser,
                    InstanceId = hbForWindow?.InstanceId
                });
            }

            if (pvEnd > cur)
                cur = pvEnd;
        }

        // Tail segment after last page visit
        if (cur < endedAt)
        {
            var tailDur = (endedAt - cur).TotalSeconds;
            if (tailDur >= 0.05)
            {
                list.Add(new TrackerEventForUpload
                {
                    Timestamp = cur.ToString("O"),
                    Duration = tailDur,
                    EventType = "window",
                    ExePath = session.ExePath,
                    AppName = session.AppName,
                    DisplayName = session.AppName,
                    WindowTitle = session.WindowTitle,
                    IsIdle = false,
                    IsMediaActive = session.IsMediaActive,
                    Date = BusinessDayUtils.GetBusinessDateString(cur),
                    RawJson = new { sessionId = session.Id },
                    Browser = hbForWindow?.Browser,
                    InstanceId = hbForWindow?.InstanceId
                });
            }
        }

        return list;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_config.PollIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        await DoPollAsync(ct).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await DoPollAsync(ct).ConfigureAwait(false);
        }
    }

    public Task DoPollAsync(CancellationToken ct = default)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            // Gap detection
            var elapsed = now - _lastPollTime;
            if (elapsed.TotalSeconds > _config.GapThresholdSeconds)
            {
                _logger.Info("Tracker", $"Gap detected: {elapsed.TotalSeconds:F1}s since last poll");
                _sessionManager.HandleGap(_lastPollTime, now);
                EnqueueGapChunks(_lastPollTime, now, isStartup: false);
            }
            _lastPollTime = now;
            lock (_statsLock) _pollCount++;
            _stateManager.SaveState(now, now, Environment.MachineName);

            // Screen-off & Idle detection
            var isScreenOff = _idleDetector.IsScreenOff();
            var idleDuration = _idleDetector.GetIdleDuration();
            if (isScreenOff)
            {
                if (!_sessionManager.IsIdle)
                    _sessionManager.HandleScreenOff(now);
            }
            else if (idleDuration.TotalSeconds > _config.IdleThresholdSeconds)
            {
                var effectiveThreshold = _config.IdleThresholdSeconds;
                if (_sessionManager.IsBrowserMediaActive)
                    effectiveThreshold *= 3;

                if (idleDuration.TotalSeconds > effectiveThreshold)
                {
                    if (!_sessionManager.IsIdle)
                        _sessionManager.HandleIdleStarted(now, idleDuration);
                }
            }
            else if (_sessionManager.IsIdle)
            {
                var win = _windowResolver.GetForegroundWindowInfo();
                _sessionManager.HandleIdleEnded(now, win);
            }

            // Window resolution: check foreground window and checkpoint if long-running
            if (!_sessionManager.IsIdle)
            {
                var window = _windowResolver.GetForegroundWindowInfo();
                if (window is not null)
                {
                    if (_lastWindow is null
                        || !string.Equals(_lastWindow.AppName, window.AppName, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(_lastWindow.WindowTitle, window.WindowTitle, StringComparison.Ordinal))
                    {
                        _sessionManager.HandleWindowChange(window, now);
                        _lastWindow = window;
                        _logger.Debug("Tracker", $"Poll window: {window.AppName} title={window.WindowTitle}");
                    }
                    else
                    {
                        _sessionManager.CheckpointIfNeeded(now, window);
                    }
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.Error("Tracker", "Poll error", ex);
            lock (_statsLock) _lastError = ex.Message;
        }
        return Task.CompletedTask;
    }

    private async Task HookLoopAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.Warn("Tracker", "Hook not supported on non-Windows, using poll-only mode");
            lock (_statsLock) _hookActive = false;
            return;
        }

        try
        {
            _logger.Info("Tracker", "Registering Win32 hooks EVENT_SYSTEM_FOREGROUND and EVENT_OBJECT_NAMECHANGE");
            _hookCallback = OnWinEvent;
            var hook1 = Win32Hook.TryRegister(0x0003, 0x0003, _hookCallback);
            var hook2 = Win32Hook.TryRegister(0x800C, 0x800C, _hookCallback);

            if (hook1 == IntPtr.Zero && hook2 == IntPtr.Zero)
            {
                _logger.Warn("Tracker", "Hook registration failed, fallback to poll");
                lock (_statsLock) _hookActive = false;
                return;
            }

            _hookHandle = hook1;
            _hookHandle2 = hook2;
            lock (_statsLock) _hookActive = true;
            _logger.Info("Tracker", $"Hook registered successfully h1={hook1} h2={hook2}");

            while (!ct.IsCancellationRequested)
            {
                Win32Hook.PumpMessages(100);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("Tracker", "Hook loop error", ex);
            lock (_statsLock) { _hookActive = false; _lastError = ex.Message; }
        }
        finally
        {
            try { Win32Hook.Unhook(_hookHandle); } catch { }
            try { Win32Hook.Unhook(_hookHandle2); } catch { }
        }
    }

    private DateTimeOffset _lastHookEventTime = DateTimeOffset.MinValue;
    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastHookEventTime).TotalMilliseconds < 500) return;
            _lastHookEventTime = now;
            var window = _windowResolver.GetForegroundWindowInfo();
            if (window is null) return;
            _sessionManager.HandleWindowChange(window, now);
            _lastWindow = window;
            _logger.Debug("Tracker", $"Hook event {eventType:X} window {window.AppName} title {window.WindowTitle}");
        }
        catch (Exception ex)
        {
            _logger.Warn("Tracker", $"Hook callback error: {ex.Message}");
        }
    }

    private async Task UploadLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_config.UploadIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            List<TrackerEventForUpload>? batch = null;
            try
            {
                if (_uploadQueue.IsEmpty) continue;
                batch = new List<TrackerEventForUpload>();
                while (batch.Count < _config.UploadBatchSize && _uploadQueue.TryDequeue(out var ev))
                    batch.Add(ev);

                if (batch.Count == 0) continue;

                var req = new TrackerEventsUploadRequest
                {
                    DeviceId = Environment.MachineName,
                    Events = batch
                };

                var result = await _api.PostAsync<ApiResponse<int>>("/pc/tracker/upload", req, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    lock (_statsLock) _eventsUploaded += batch.Count;
                    _logger.Info("Tracker", $"Uploaded {batch.Count} events -> {result.Data} saved");
                    Log?.Invoke($"[Tracker] Uploaded {batch.Count} events -> {result.Data} saved");
                    lock (_statsLock) _lastError = null;
                    batch = null;
                }
                else
                {
                    lock (_statsLock) { _uploadFailures++; _lastError = "Upload returned null response"; }
                    _logger.Warn("Tracker", "Upload returned null response");
                    foreach (var ev in batch) _uploadQueue.Enqueue(ev);
                    batch = null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (HttpRequestException ex)
            {
                lock (_statsLock) { _uploadFailures++; _lastError = ex.Message; }
                _logger.Error("Tracker", $"Upload Http error: {ex.Message}", ex);
                var status = ex.StatusCode;
                var isClientError = status.HasValue && (int)status.Value >= 400 && (int)status.Value < 500;
                // 401 必须按"可重试"处理（WO-ISSUES-396-400-20261007 REQ-5 反面行为）：
                // 它表示"令牌没续上"，不是"这批数据不合格"。旧行为把 401 当客户端错误丢掉整批，
                // 于是休眠唤醒后那批 gap 补齐分片被静默丢弃 —— 数据永久丢失。
                // 现在改为重投队列，等续期成功后随下一轮补传（不丢队列、不登出）。
                var isRetryableClientError = status == HttpStatusCode.RequestTimeout
                    || status == HttpStatusCode.Unauthorized
                    || (int?)status == 429;
                if ((!isClientError || isRetryableClientError) && batch is not null)
                {
                    foreach (var ev in batch) _uploadQueue.Enqueue(ev);
                }
                else if (isClientError && !isRetryableClientError)
                {
                    _logger.Warn("Tracker", $"Dropping batch due to client error {(int)status!} {status}, not requeuing {batch?.Count ?? 0} events");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                lock (_statsLock) { _uploadFailures++; _lastError = ex.Message; }
                _logger.Error("Tracker", "Upload loop error", ex);
                if (batch is not null)
                {
                    foreach (var ev in batch) _uploadQueue.Enqueue(ev);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Drains site-level events from the bridge channel and uploads them to
    /// /pc/browser-tt/upload with the same retry semantics as the window
    /// tracker: requeue on server/transport errors, drop on other 4xx.
    /// </summary>
    private async Task SiteUploadLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.UploadIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            List<SiteEventDto>? batch = null;
            try
            {
                var drained = 0;
                while (drained < 5000 && _bridge.SiteReader.TryRead(out var ev))
                {
                    _siteUploadQueue.Enqueue(ev);
                    drained++;
                }
                if (_siteUploadQueue.IsEmpty) continue;

                batch = new List<SiteEventDto>();
                while (batch.Count < _config.UploadBatchSize && _siteUploadQueue.TryDequeue(out var ev))
                    batch.Add(ev);
                if (batch.Count == 0) continue;

                // 日期归属在本机推导（focus/tick 取结束时间的本地日期，visit 取当天），
                // 避免服务器时区不同导致按日聚合错位。
                foreach (var ev in batch)
                {
                    if (!string.IsNullOrEmpty(ev.Date)) continue;
                    var basisMs = ev.EndMs ?? ev.StartMs;
                    ev.Date = basisMs is { } ms
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd")
                        : DateTimeOffset.Now.ToString("yyyy-MM-dd");
                }

                var req = new SiteEventsUploadRequest
                {
                    DeviceId = Environment.MachineName,
                    Events = batch
                };
                var result = await _api.PostAsync<ApiResponse<int>>("/pc/browser-tt/upload", req, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    lock (_siteStatsLock) _siteEventsUploaded += batch.Count;
                    _logger.Info("Tracker", $"Site batch uploaded {batch.Count} events -> {result.Data} saved");
                    lock (_siteStatsLock) _siteLastError = null;
                    batch = null;
                }
                else
                {
                    lock (_siteStatsLock) { _siteUploadFailures++; _siteLastError = "Upload returned null response"; }
                    _logger.Warn("Tracker", "Site upload returned null response");
                    foreach (var ev in batch) _siteUploadQueue.Enqueue(ev);
                    batch = null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (HttpRequestException ex)
            {
                lock (_siteStatsLock) { _siteUploadFailures++; _siteLastError = ex.Message; }
                _logger.Error("Tracker", $"Site upload Http error: {ex.Message}", ex);
                var status = ex.StatusCode;
                var isClientError = status.HasValue && (int)status.Value >= 400 && (int)status.Value < 500;
                // 同上传主链路：401 = 令牌没续上，按可重试处理，不丢队列（REQ-5 反面行为）。
                var isRetryableClientError = status == HttpStatusCode.RequestTimeout
                    || status == HttpStatusCode.Unauthorized
                    || (int?)status == 429;
                if ((!isClientError || isRetryableClientError) && batch is not null)
                {
                    foreach (var ev in batch) _siteUploadQueue.Enqueue(ev);
                }
                else if (isClientError && !isRetryableClientError)
                {
                    _logger.Warn("Tracker", $"Dropping site batch due to client error {(int)status!} {status}, not requeuing {batch?.Count ?? 0} events");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                lock (_siteStatsLock) { _siteUploadFailures++; _siteLastError = ex.Message; }
                _logger.Error("Tracker", $"Site upload error: {ex.Message}", ex);
                if (batch is not null)
                {
                    foreach (var ev in batch) _siteUploadQueue.Enqueue(ev);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_config.HealthReportIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var req = new TrackerHealthRequest
                {
                    DeviceId = Environment.MachineName,
                    Status = "running",
                    UptimeSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                    HookActive = HookActive,
                    PollCount = PollCount,
                    SessionsCreated = SessionsCreated,
                    EventsUploaded = EventsUploaded,
                    UploadFailures = UploadFailures,
                    LastError = LastError,
                    BrowserConnected = BrowserConnected,
                    BrowserHeartbeatAgeSeconds = BrowserHeartbeatAgeSeconds,
                    SiteConnected = SiteConnected,
                    SiteLastEventAgeSeconds = SiteLastEventAgeSeconds,
                    SiteEventsUploaded = SiteEventsUploaded,
                    SiteLastError = SiteLastError
                };
                await _api.PostAsync<ApiResponse<string>>("/pc/tracker/health", req, ct).ConfigureAwait(false);
                _logger.Debug("Tracker", $"Health reported: hook={req.HookActive} polls={req.PollCount} sessions={req.SessionsCreated} uploaded={req.EventsUploaded}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.Warn("Tracker", $"Health report failed: {ex.Message}");
            }
        }
    }

    private async Task BrowserLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var hb in _bridge.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _sessionManager.UpdateBrowserHeartbeat(hb);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Warn("Tracker", $"Browser loop error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        _cts.Dispose();
        _bridge.Dispose();
        _logger.Dispose();
    }

    private static class Win32Hook
    {
        public static IntPtr TryRegister(uint eventMin, uint eventMax, WinEventProc proc)
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            try { return SetWinEventHook(eventMin, eventMax, IntPtr.Zero, proc, 0, 0, 0); } catch { return IntPtr.Zero; }
        }
        public static void Unhook(IntPtr h) { if (h != IntPtr.Zero) try { UnhookWinEvent(h); } catch { } }
        public static void PumpMessages(int timeoutMs)
        {
            try
            {
                MSG msg;
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch { }
        }
        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    }
}
