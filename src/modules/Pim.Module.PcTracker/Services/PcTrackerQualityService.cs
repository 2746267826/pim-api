using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public sealed class PcTrackerQualityService
{
    /// <summary>
    /// ActivityWatch (AW) 退役切换基准时间（2026-09-01 Asia/Shanghai）。
    /// 当查询时间窗口起点 rangeStart >= AwRetirementDate 时，质检全面转向原生采集事件（TrackerEventEntity），
    /// 不再要求 AW Bucket 和 AW Event 存在，避免报告假阳性告警。跨退役窗口（rangeStart < AwRetirementDate）
    /// 仍保留对历史 AW 采集组件的检查。
    /// ActivityWatch retirement cutoff date (2026-09-01 Asia/Shanghai).
    /// When rangeStart >= AwRetirementDate, quality checks exclusively rely on native tracker events
    /// without requiring AW buckets or events, preventing false-positive alarms. Windows spanning
    /// prior to the retirement date (rangeStart < AwRetirementDate) retain checks for legacy AW components.
    /// </summary>
    public static readonly DateTimeOffset AwRetirementDate = new(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly TimeSpan StaleBucketAge = TimeSpan.FromHours(24);
    private readonly PimDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IDataReliabilityGate? _dataReliabilityGate;

    public PcTrackerQualityService(PimDbContext db, TimeProvider timeProvider, IDataReliabilityGate? dataReliabilityGate = null)
    {
        _db = db;
        _timeProvider = timeProvider;
        _dataReliabilityGate = dataReliabilityGate;
    }

    public async Task<PcQualityResponse> GetQualityAsync(DateTime? date, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        var (rangeStart, rangeEnd) = GetRange(date, dateFrom, dateTo);
        var isPostAw = rangeStart >= AwRetirementDate;

        var buckets = await _db.Set<AwBucketEntity>()
            .AsNoTracking()
            .ToListAsync(ct);

        var events = await _db.Set<AwEventEntity>()
            .AsNoTracking()
            .Where(e => e.Timestamp >= rangeStart && e.Timestamp < rangeEnd)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var trackerEvents = await _db.Set<TrackerEventEntity>()
            .AsNoTracking()
            .Where(e => e.Timestamp >= rangeStart && e.Timestamp < rangeEnd)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var samples = await _db.Set<KeystatsSampleEntity>()
            .AsNoTracking()
            .Where(s => s.SampledAtUtc >= rangeStart && s.SampledAtUtc < rangeEnd)
            .OrderBy(s => s.PimDeviceId)
            .ThenBy(s => s.SampledAtUtc)
            .ToListAsync(ct);

        var heartbeat = await _db.Set<DaemonHeartbeatEntity>()
            .AsNoTracking()
            .Where(h => h.DaemonKind == "windows")
            .OrderByDescending(h => h.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        var issues = new List<PcQualityIssueDto>();
        var components = new List<PcQualityComponentDto>();

        if (!isPostAw)
        {
            components.Add(CheckBuckets(buckets, checkedAt, issues));
            components.Add(CheckEvents(events, issues));
        }

        if (isPostAw || trackerEvents.Count > 0)
        {
            components.Add(CheckTrackerEvents(trackerEvents, issues));
        }

        components.Add(CheckKeystats(samples, issues));
        components.Add(CheckDaemon(heartbeat, checkedAt, isPostAw, issues));
        components.Add(CheckTimeline(events, trackerEvents, samples, isPostAw, issues));

        AddDataReliabilityGate(components, issues, new[] { "S1", "S2", "S3", "S5", "S6", "S7", "S8", "S13" });

        var overallStatus = components
            .Select(c => c.Status)
            .OrderByDescending(GetSeverityRank)
            .FirstOrDefault();

        return new PcQualityResponse(
            overallStatus,
            GetLabel(overallStatus),
            GetMessage(overallStatus),
            checkedAt,
            components,
            issues,
            issues
                .Select(i => i.NextStep)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .Cast<string>()
                .ToList());
    }

    /// <summary>
    /// 把数据可信度尺子接入质量报告（#260 第 4 点）：尺子红，报告不得绿。
    /// 只做附加：新增一个 component 与对应 issue，整体状态由既有"取最严"聚合自然降级；
    /// 尺子尚未体检或结果过期时给出 Unknown 组件与明确文案，绝不静默判健康。
    /// </summary>
    private void AddDataReliabilityGate(
        List<PcQualityComponentDto> components,
        List<PcQualityIssueDto> issues,
        IReadOnlyList<string> ruleCodes)
    {
        if (_dataReliabilityGate is null)
        {
            return;
        }

        var verdict = _dataReliabilityGate.Evaluate(ruleCodes);

        components.Add(new PcQualityComponentDto(
            "data_reliability",
            "数据可信度尺子",
            verdict.Status,
            verdict.Message,
            new Dictionary<string, string>
            {
                ["redRules"] = string.Join(",", verdict.RedRules),
                ["yellowRules"] = string.Join(",", verdict.YellowRules),
                ["unknownRules"] = string.Join(",", verdict.UnknownRules),
                ["inspectedAtUtc"] = verdict.InspectedAtUtc?.ToString("O") ?? string.Empty
            }));

        foreach (var code in verdict.RedRules)
        {
            issues.Add(new PcQualityIssueDto(
                code,
                PimHealthStatus.Critical,
                "data_reliability",
                $"{code} 数据可信度尺子报红：{verdict.Message}",
                "打开「设置 → 数据可信度」查看违规样例与存量趋势"));
        }

        foreach (var code in verdict.YellowRules)
        {
            issues.Add(new PcQualityIssueDto(
                code,
                PimHealthStatus.Warning,
                "data_reliability",
                $"{code} 数据可信度尺子报黄：{verdict.Message}",
                null));
        }
    }

    private static (DateTimeOffset Start, DateTimeOffset End) GetRange(DateTime? date, DateTime? dateFrom, DateTime? dateTo)
    {
        var from = dateFrom ?? date ?? DateTime.Today;
        var to = dateTo ?? date ?? from;

        if (to < from)
        {
            (from, to) = (to, from);
        }

        var start = PcTrackerService.GetBusinessDayStartForQuery(from);
        var end = PcTrackerService.GetBusinessDayStartForQuery(to.Date.AddDays(1));
        return (start, end);
    }

    private static PcQualityComponentDto CheckBuckets(
        IReadOnlyCollection<AwBucketEntity> buckets,
        DateTimeOffset checkedAt,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();

        if (!HasBucketType(buckets, "currentwindow"))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-window-bucket",
                PimHealthStatus.Critical,
                "aw-buckets",
                "缺少 ActivityWatch 窗口数据桶。",
                "启动或重新连接 ActivityWatch 窗口监视器。"));
        }

        if (!HasBucketType(buckets, "afkstatus"))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-afk-bucket",
                PimHealthStatus.Warning,
                "aw-buckets",
                "缺少 ActivityWatch AFK 数据桶。",
                "启动或重新连接 ActivityWatch AFK 监视器。"));
        }

        if (!HasBucketType(buckets, "web.tab.current"))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-web-bucket",
                PimHealthStatus.Warning,
                "aw-buckets",
                "缺少 ActivityWatch 网页数据桶。",
                "安装或重新连接浏览器 ActivityWatch 扩展。"));
        }

        var staleBuckets = buckets.Count(b => checkedAt - b.SeenAt > StaleBucketAge);
        if (staleBuckets > 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "stale-aw-bucket",
                PimHealthStatus.Warning,
                "aw-buckets",
                "一个或多个 ActivityWatch 数据桶近期没有更新。",
                "重启 ActivityWatch 监视器，并确认上传已恢复。"));
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["bucketCount"] = buckets.Count.ToString(),
            ["staleBucketCount"] = staleBuckets.ToString()
        };

        return BuildComponent("aw-buckets", "ActivityWatch 数据桶", componentIssues, details);
    }

    private static PcQualityComponentDto CheckEvents(IReadOnlyCollection<AwEventEntity> events, List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();

        if (events.Count == 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-events",
                PimHealthStatus.Warning,
                "aw-events",
                "所选范围内没有采集到 ActivityWatch 事件。",
                "确认 ActivityWatch 数据正在上传。"));
        }
        else
        {
            if (!events.Any(IsWindowEvent))
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "missing-aw-window-events",
                    PimHealthStatus.Warning,
                    "aw-events",
                    "所选范围内没有采集到 ActivityWatch 窗口事件。",
                    "确认窗口监视器正在运行。"));
            }

            if (!events.Any(IsAfkEvent))
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "missing-aw-afk-events",
                    PimHealthStatus.Warning,
                    "aw-events",
                    "所选范围内没有采集到 ActivityWatch AFK 事件。",
                    "确认 AFK 监视器正在运行。"));
            }

            var missingSourceIds = events.Count(e => e.SourceEventId is null);
            if (missingSourceIds > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "aw-events-missing-source-id",
                    MajoritySeverity(missingSourceIds, events.Count),
                    "aw-events",
                    "部分 ActivityWatch 事件缺少来源事件 ID。",
                    "从守护程序重新上传 ActivityWatch 事件。"));
            }

            var invalidJson = events.Count(e => string.IsNullOrWhiteSpace(e.DataJson) || !IsValidJson(e.DataJson));
            if (invalidJson > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "aw-events-invalid-data-json",
                    MajoritySeverity(invalidJson, events.Count),
                    "aw-events",
                    "部分 ActivityWatch 事件缺少或包含无效 data_json。",
                    "检查守护程序序列化逻辑，并重新上传受影响事件。"));
            }
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["eventCount"] = events.Count.ToString(),
            ["windowEventCount"] = events.Count(IsWindowEvent).ToString(),
            ["afkEventCount"] = events.Count(IsAfkEvent).ToString()
        };

        return BuildComponent("aw-events", "ActivityWatch 事件", componentIssues, details);
    }

    private static PcQualityComponentDto CheckTrackerEvents(
        IReadOnlyCollection<TrackerEventEntity> events,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var overlappingCount = 0;

        if (events.Count == 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-tracker-events",
                PimHealthStatus.Warning,
                "tracker-events",
                "所选范围内没有采集到原生追踪事件。",
                "确认原生追踪器正在运行并上传数据。"));
        }
        else
        {
            if (!events.Any(IsTrackerWindowEvent))
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "missing-tracker-window-events",
                    PimHealthStatus.Warning,
                    "tracker-events",
                    "所选范围内没有采集到原生窗口事件。",
                    "确认原生窗口监视器正在运行。"));
            }

            foreach (var group in events.GroupBy(e => (e.DeviceId, e.EventType)))
            {
                DateTimeOffset? lastEnd = null;
                foreach (var evt in group.OrderBy(e => e.Timestamp))
                {
                    if (evt.Duration <= 0) continue;
                    var currentStart = evt.Timestamp;
                    var currentEnd = evt.Timestamp.AddSeconds(evt.Duration);
                    if (lastEnd is not null && currentStart.AddSeconds(1) < lastEnd.Value)
                    {
                        overlappingCount++;
                    }
                    if (lastEnd is null || currentEnd > lastEnd.Value)
                    {
                        lastEnd = currentEnd;
                    }
                }
            }

            if (overlappingCount > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "tracker-events-overlapping",
                    MajoritySeverity(overlappingCount, events.Count),
                    "tracker-events",
                    "部分原生追踪事件存在时间区间重叠。",
                    "检查追踪器事件切割与去重逻辑。"));
            }

            var excessiveDurationCount = events.Count(e => !e.IsIdle && e.Duration > 7200);
            if (excessiveDurationCount > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "tracker-events-excessive-duration",
                    PimHealthStatus.Warning,
                    "tracker-events",
                    "检测到异常超长的活动事件（超过2小时未切分）。",
                    "确认追踪器心跳切分逻辑正常运作。"));
            }

            var missingAppCount = events.Count(e => IsTrackerWindowEvent(e) && string.IsNullOrWhiteSpace(e.AppName) && string.IsNullOrWhiteSpace(e.ExePath));
            if (missingAppCount > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "tracker-events-missing-metadata",
                    MajoritySeverity(missingAppCount, events.Count),
                    "tracker-events",
                    "部分原生追踪窗口事件缺少应用程序元数据。",
                    "检查追踪器进程名与窗口信息提取。"));
            }

            var invalidJsonCount = events.Count(e => !string.IsNullOrEmpty(e.RawJson) && !IsValidJson(e.RawJson));
            if (invalidJsonCount > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "tracker-events-invalid-raw-json",
                    MajoritySeverity(invalidJsonCount, events.Count),
                    "tracker-events",
                    "部分原生追踪事件包含无效 raw_json。",
                    "检查追踪器序列化逻辑。"));
            }
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["eventCount"] = events.Count.ToString(),
            ["windowEventCount"] = events.Count(IsTrackerWindowEvent).ToString(),
            ["idleEventCount"] = events.Count(e => e.IsIdle || string.Equals(e.EventType, "idle", StringComparison.OrdinalIgnoreCase)).ToString(),
            ["overlappingCount"] = overlappingCount.ToString(),
            ["excessiveDurationCount"] = events.Count(e => !e.IsIdle && e.Duration > 7200).ToString()
        };

        return BuildComponent("tracker-events", "PC 原生追踪事件", componentIssues, details);
    }

    private static PcQualityComponentDto CheckKeystats(
        IReadOnlyCollection<KeystatsSampleEntity> samples,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var gaps = 0;
        var resets = 0;

        if (samples.Count == 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-keystats-samples",
                PimHealthStatus.Critical,
                "keystats-samples",
                "所选范围内没有采集到 KeyStats 样本。",
                "启动 KeyStats 采集，并确认守护程序正在上传。"));
        }
        else
        {
            foreach (var group in samples.GroupBy(s => s.PimDeviceId))
            {
                KeystatsSampleEntity? previous = null;
                foreach (var sample in group.OrderBy(s => s.SampledAtUtc))
                {
                    var delta = KeystatsDeltaCalculator.Calculate(previous, sample);
                    if (previous is not null && delta.IsGap)
                    {
                        gaps++;
                    }

                    if (delta.IsReset)
                    {
                        resets++;
                    }

                    previous = sample;
                }
            }

            if (gaps > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "keystats-sample-gap",
                    PimHealthStatus.Warning,
                    "keystats-samples",
                    "KeyStats 样本存在采集间断。",
                    "保持 Windows 守护程序持续运行。"));
            }

            if (resets > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "keystats-counter-reset",
                    PimHealthStatus.Warning,
                    "keystats-samples",
                    "所选范围内 KeyStats 计数器发生重置。",
                    "检查 KeyStats 或守护程序是否重启过。"));
            }
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["sampleCount"] = samples.Count.ToString(),
            ["gapCount"] = gaps.ToString(),
            ["resetCount"] = resets.ToString()
        };

        return BuildComponent("keystats-samples", "KeyStats 样本", componentIssues, details);
    }

    private static PcQualityComponentDto CheckDaemon(
        DaemonHeartbeatEntity? heartbeat,
        DateTimeOffset checkedAt,
        bool isPostAw,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var details = new Dictionary<string, string>();

        if (heartbeat is null)
        {
            details["heartbeat"] = "missing";
            componentIssues.Add(new PcQualityIssueDto(
                "missing-windows-daemon-heartbeat",
                PimHealthStatus.Unknown,
                "daemon-upload",
                "尚未收到 Windows 守护程序心跳。",
                "启动并登录 Windows 守护程序。"));
            issues.AddRange(componentIssues);
            return BuildComponent("daemon-upload", "Windows 守护程序上传", componentIssues, details);
        }

        var age = checkedAt - heartbeat.ReceivedAt;
        var lifecycle = DaemonLifecycleClassifier.Classify(heartbeat, checkedAt);
        details["receivedAt"] = heartbeat.ReceivedAt.ToString("O");
        details["ageMinutes"] = Math.Max(0, age.TotalMinutes).ToString("0.0");
        details["uploadQueueCount"] = (heartbeat.UploadQueueCount ?? 0).ToString();
        details["activityWatchState"] = heartbeat.ActivityWatchState;
        details["keyStatsState"] = heartbeat.KeyStatsState;
        details["daemonState"] = lifecycle.State;
        if (heartbeat.PlannedOfflineAt is not null)
        {
            details["plannedOfflineAt"] = heartbeat.PlannedOfflineAt.Value.ToString("O");
            details["offlineReason"] = heartbeat.OfflineReason ?? "";
        }

        if (lifecycle.State == "planned-offline")
        {
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-planned-offline",
                PimHealthStatus.Unknown,
                "daemon-upload",
                "守护程序已正常下线（关机/休眠）。",
                "Windows 守护程序将在下次开机后自动恢复。"));
        }
        else
        {
            if (age >= DaemonLifecycleClassifier.AbnormalDaemonAge)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "stale-windows-daemon-heartbeat",
                    PimHealthStatus.Critical,
                    "daemon-upload",
                    "Windows 守护程序心跳已过期。",
                    "重启 Windows 守护程序，并确认它能访问 API。"));
            }
            else if (age >= DaemonLifecycleClassifier.OnlineDaemonAge)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "old-daemon-heartbeat",
                    PimHealthStatus.Warning,
                    "daemon-upload",
                    "Windows 守护程序心跳偏旧。",
                    "检查 Windows 守护程序是否仍在运行。"));
            }
        }

        if (!string.IsNullOrWhiteSpace(heartbeat.LastError))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-last-error",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows 守护程序最近报告过错误。",
                "打开守护程序诊断信息并处理最后一次错误。"));
        }

        if (heartbeat.UploadQueueCount.GetValueOrDefault() > 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-upload-queue",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows 守护程序存在待上传队列。",
                "确认 Windows 守护程序可以访问 API。"));
        }

        var isAwUnavailable = !isPostAw && IsSourceUnavailable(heartbeat.ActivityWatchState);
        var isKeyStatsUnavailable = IsSourceUnavailable(heartbeat.KeyStatsState);

        if (isAwUnavailable || isKeyStatsUnavailable)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-source-unavailable",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows 守护程序报告采集来源不可用。",
                "在这台 PC 上启动不可用的采集来源。"));
        }

        issues.AddRange(componentIssues);
        return BuildComponent("daemon-upload", "Windows 守护程序上传", componentIssues, details);
    }

    private static PcQualityComponentDto CheckTimeline(
        IReadOnlyCollection<AwEventEntity> awEvents,
        IReadOnlyCollection<TrackerEventEntity> trackerEvents,
        IReadOnlyCollection<KeystatsSampleEntity> samples,
        bool isPostAw,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var hasActivityEvents = awEvents.Count > 0 || trackerEvents.Count > 0;
        var hasKeystatsSamples = samples.Count > 0;
        var hasKeystatsDeltaPair = samples
            .GroupBy(s => s.PimDeviceId)
            .Any(g => g.Count() >= 2);

        if (!hasActivityEvents || !hasKeystatsSamples)
        {
            var nextStep = isPostAw
                ? "先处理原生追踪器和 KeyStats 采集问题。"
                : "先处理 ActivityWatch 和 KeyStats 采集问题。";

            componentIssues.Add(new PcQualityIssueDto(
                "timeline-inputs-incomplete",
                PimHealthStatus.Warning,
                "interpreted-timeline",
                "所选范围内用于解释时间线的输入不完整。",
                nextStep));
        }
        else if (!hasKeystatsDeltaPair)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "keystats-insufficient-samples",
                PimHealthStatus.Warning,
                "interpreted-timeline",
                "KeyStats 样本过少，无法构建输入时间线增量。",
                "从同一设备至少采集两个 KeyStats 样本。"));
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["hasActivityEvents"] = hasActivityEvents.ToString(),
            ["hasActivityWatchEvents"] = (awEvents.Count > 0).ToString(),
            ["hasTrackerEvents"] = (trackerEvents.Count > 0).ToString(),
            ["hasKeystatsSamples"] = hasKeystatsSamples.ToString(),
            ["hasKeystatsDeltaPair"] = hasKeystatsDeltaPair.ToString()
        };

        return BuildComponent("interpreted-timeline", "解释时间线", componentIssues, details);
    }

    private static bool HasBucketType(IEnumerable<AwBucketEntity> buckets, string bucketType)
        => buckets.Any(b => string.Equals(b.BucketType, bucketType, StringComparison.OrdinalIgnoreCase));

    private static bool IsWindowEvent(AwEventEntity e)
        => string.Equals(e.EventType, "window", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.BucketType, "currentwindow", StringComparison.OrdinalIgnoreCase);

    private static bool IsAfkEvent(AwEventEntity e)
        => string.Equals(e.EventType, "afk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.BucketType, "afkstatus", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrackerWindowEvent(TrackerEventEntity e)
        => string.Equals(e.EventType, "window", StringComparison.OrdinalIgnoreCase);

    private static PimHealthStatus MajoritySeverity(int count, int total)
        => count > total / 2 ? PimHealthStatus.Critical : PimHealthStatus.Warning;

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSourceUnavailable(string state)
        => string.Equals(state, DaemonSourceState.Unavailable.ToString(), StringComparison.OrdinalIgnoreCase);

    private static PcQualityComponentDto BuildComponent(
        string key,
        string name,
        IReadOnlyCollection<PcQualityIssueDto> issues,
        IReadOnlyDictionary<string, string> details)
    {
        var status = issues.Count == 0
            ? PimHealthStatus.Healthy
            : issues.Select(i => i.Severity).OrderByDescending(GetSeverityRank).First();

        return new PcQualityComponentDto(key, name, status, ComponentMessage(status), details);
    }

    private static string ComponentMessage(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "组件状态正常。",
            PimHealthStatus.Warning => "组件存在采集质量警告。",
            PimHealthStatus.Critical => "组件存在严重采集质量问题。",
            _ => "组件质量状态未知。"
        };

    private static string GetLabel(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "正常",
            PimHealthStatus.Warning => "有警告",
            PimHealthStatus.Critical => "故障",
            _ => "未知"
        };

    private static string GetMessage(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "所选范围内的 PC 事实数据完整。",
            PimHealthStatus.Warning => "PC 事实数据可用，但部分采集质量问题需要关注。",
            PimHealthStatus.Critical => "所选范围内的 PC 事实数据可靠性不足。",
            _ => "暂时无法完整判断 PC 事实数据质量。"
        };

    private static int GetSeverityRank(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => 0,
            PimHealthStatus.Unknown => 1,
            PimHealthStatus.Warning => 2,
            PimHealthStatus.Critical => 3,
            _ => 0
        };
}
