using System.Globalization;
using System.Linq.Expressions;
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

    /// <summary>
    /// REQ-8 判据（AC-8.1，助手决定值，可一句话改回）：
    /// 最新内容与「查询范围末尾（不超过当前时刻）」相差超过这个量，才判定为「本库数据滞后于查询范围」。
    /// 取 24 小时是为了不把「范围末尾本来就是没人用电脑的时段」误报成缺数。
    /// </summary>
    private static readonly TimeSpan RangeBeyondDatabaseThreshold = TimeSpan.FromHours(24);

    /// <summary>REQ-8 判据：心跳与「本库最新内容」停在同一时刻、且整体滞后超过该阈值 → 归入「本库整体滞后或采集端自那时起停机」这一档（两者仅凭时间关系无法区分）。</summary>
    private static readonly TimeSpan DatabaseLagThreshold = TimeSpan.FromHours(24);

    /// <summary>REQ-8 判据：心跳与「本库最新内容」相差不超过该容差（双向）即视为两者一起停住。</summary>
    private static readonly TimeSpan HeartbeatAtHorizonTolerance = TimeSpan.FromHours(1);

    private const string StaleReasonCollectorStale = "collector-heartbeat-stale";
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

        // 覆盖判定要额外带上「起点在范围之前、但伸进范围」的事件（Mark 会按边界裁剪）：
        // 只按「起点落在范围内」取数会整条丢掉这类记录，让范围第一小时被误报成没有数据。
        var coverageEvents = trackerEvents
            .Concat(await LoadEventsCrossingRangeStartAsync(rangeStart, ct))
            .ToList();

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

        // REQ-8（#369）：能定位才叫体检结果。三样东西一起算：
        // 1) 本库最新数据时刻（含心跳）→ 区分「采集端心跳过期」与「本库数据滞后于查询范围」；
        // 2) 近 7 个业务日的事件基线 → 给本次范围一个偏高/偏低判定；
        // 3) 范围内部的缺数时段 → 直接列出哪几小时无数据、从哪个时刻起断开。
        // 用「与范围重叠」的原生事件（含起点在范围之前、伸进范围的记录），与覆盖判定同源：
        // 只用起点落在范围内的事件会让 dataHorizonUtc 早于本库在本次范围内真实存在的数据。
        var dataHorizonUtc = ComputeDataHorizon(heartbeat, events, coverageEvents, samples);
        // 「这本库本身就旧」是整库属性，不是某次查询范围的属性 —— 所以内容地平线要跨全库取，
        // 否则查询一个较早的范围时，会把「本库停在很久以前」误判成「采集端刚停机」。
        var contentHorizonUtc = await ComputeDatabaseContentHorizonAsync(ct);
        var daysInRange = Math.Max(1, (int)Math.Ceiling((rangeEnd - rangeStart).TotalDays));
        var baseline = (await BuildTrackerBaselineAsync(rangeStart, ct))
            .WithCurrentDailyEventCount(trackerEvents.Count / (double)daysInRange);
        // 查询当前业务日时「范围末尾」还在未来：缺数判定、尾部断档、trailingGapMinutes 必须共用
        // 同一个有效终点（= min(范围末尾, 现在)），否则会把还没到的小时报成缺数。
        var coverageEnd = EffectiveRangeEnd(rangeEnd, checkedAt);
        var coverage = BuildCoverage(
            rangeStart,
            coverageEnd,
            coverageEvents,
            samples,
            // 尾部断档只在"设备此刻不属于已声明的计划内离线"时才补：声明比最近心跳更新 = 设备还停着。
            // 不能再用"planned_offline_at 非空"—— 心跳不再清空该标记，否则尾部断档会永久不再上报。
            includeTrailingGap: heartbeat is not null && !DaemonLifecycleClassifier.IsCurrentlyPlannedOffline(heartbeat));

        var issues = new List<PcQualityIssueDto>();
        var components = new List<PcQualityComponentDto>();

        if (!isPostAw)
        {
            components.Add(CheckBuckets(buckets, checkedAt, issues));
            components.Add(CheckEvents(events, issues));
        }

        if (isPostAw || trackerEvents.Count > 0)
        {
            // 用「与范围重叠」的那批（含起点在范围之前、伸进范围的记录）：覆盖判定用的是它，
            // 「有没有窗口事件 / 事件条数」也必须同源，否则会自相矛盾。
            components.Add(CheckTrackerEvents(coverageEvents, baseline, coverage, rangeStart, rangeEnd, coverageEnd, checkedAt, issues));
        }

        components.Add(CheckKeystats(samples, issues));
        components.Add(CheckDaemon(heartbeat, checkedAt, isPostAw, rangeStart, rangeEnd, dataHorizonUtc, contentHorizonUtc, issues));
        components.Add(CheckTimeline(events, coverageEvents, samples, isPostAw, issues));

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
            new Dictionary<string, object?>
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
                "打开「设置 → 数据可信度」查看违规样例与历史欠账"));
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
        var details = new Dictionary<string, object?>
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
        var details = new Dictionary<string, object?>
        {
            ["eventCount"] = events.Count.ToString(),
            ["windowEventCount"] = events.Count(IsWindowEvent).ToString(),
            ["afkEventCount"] = events.Count(IsAfkEvent).ToString()
        };

        return BuildComponent("aw-events", "ActivityWatch 事件", componentIssues, details);
    }

    /// <param name="events">
    /// 与查询范围重叠的原生事件（含起点在范围之前、伸进范围的记录）—— 与覆盖判定同源。
    /// </param>
    private static PcQualityComponentDto CheckTrackerEvents(
        IReadOnlyCollection<TrackerEventEntity> events,
        TrackerEventBaseline baseline,
        CoverageGaps coverage,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset coverageEnd,
        DateTimeOffset checkedAt,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var overlappingCount = 0;

        // 「范围内一个事件都没有」用的是覆盖判定那一批：起点在范围之前、但伸进范围的事件
        // 同样说明这段范围有数据（只按「起点落在范围内」取数会把它漏掉）。
        if (coverage.TrackerEventCount == 0)
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
                    var currentEnd = EventEnd(evt.Timestamp, evt.Duration);
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

        // AC-8.3：范围内部的缺数时段要能被直接看到（不是一句「数据不完整」）。
        if (coverage.Gaps.Count > 0)
        {
            var first = coverage.Gaps[0];
            var last = coverage.Gaps[^1];
            // REQ-4（#373）：details.disconnectedFromUtc 取的是**最早一段**断档的起点。
            // 旧文案写「最近一次中断自 …」，多段断档时会指向最早那段却声称是最近 —— 与字段语义相反。
            // 现在两段都点名：最早一段（= disconnectedFromUtc）与最近一段各自给出起止。
            var description = coverage.Gaps.Count == 1
                ? $"该段为 {FormatLocal(first.StartUtc)} 到 {FormatLocal(first.EndUtc)}" +
                  "（该段起点即 disconnectedFromUtc，同时也是最近一段）。"
                : $"最早一段为 {FormatLocal(first.StartUtc)} 到 {FormatLocal(first.EndUtc)}（disconnectedFromUtc 即该段起点）；" +
                  $"最近一段为 {FormatLocal(last.StartUtc)} 到 {FormatLocal(last.EndUtc)}。";
            componentIssues.Add(new PcQualityIssueDto(
                "tracker-events-missing-hours",
                PimHealthStatus.Warning,
                "tracker-events",
                $"检测到 {coverage.Gaps.Count} 段连续缺数（本地时间）：{coverage.DescribeGaps()}；{description}",
                "核对这段时间内 Windows 守护程序是否在运行、是否上报失败。"));
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, object?>
        {
            ["eventCount"] = events.Count.ToString(),
            ["windowEventCount"] = events.Count(IsTrackerWindowEvent).ToString(),
            ["idleEventCount"] = events.Count(e => e.IsIdle || string.Equals(e.EventType, "idle", StringComparison.OrdinalIgnoreCase)).ToString(),
            ["overlappingCount"] = overlappingCount.ToString(),
            ["excessiveDurationCount"] = events.Count(e => !e.IsIdle && e.Duration > 7200).ToString(),
            // AC-8.2：可比基线 + 判定 + 依据（不是只有一个绝对数）。
            ["baselineMethod"] = TrackerEventBaseline.Method,
            ["baselineEventCount"] = baseline.MedianDailyEventCount.ToString("0.#", CultureInfo.InvariantCulture),
            ["baselineDays"] = TrackerEventBaseline.Days.ToString(),
            ["currentDailyEventCount"] = baseline.CurrentDailyEventCount.ToString("0.#", CultureInfo.InvariantCulture),
            ["deviationRatio"] = baseline.DeviationRatio?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            ["verdict"] = baseline.Verdict,
            ["verdictBasis"] = baseline.Basis,
            ["rangeStartUtc"] = rangeStart.ToString("O"),
            ["rangeEndUtc"] = rangeEnd.ToString("O"),
            // 缺数判定实际使用的有效终点（查询当前业务日时 = 现在，而不是还在未来的业务日末尾）。
            ["coverageEndUtc"] = coverageEnd.ToString("O"),
            // AC-8.3：哪几小时无数据 / 从哪个时刻起断开。
            ["missingHourCount"] = coverage.MissingHourCount.ToString(),
            ["missingHours"] = coverage.DescribeMissingHours(),
            ["disconnectedFromUtc"] = coverage.DisconnectedFromUtc?.ToString("O") ?? string.Empty,
            // REQ-5（#377）：缺数时段的机器可读版本，与同一份 CoverageGaps 同源生成 ——
            // 与 issues 里的本地化文案不可能出现「文案里有、字段里没有」的时段（AC-5.2）。
            // 既有 missingHours / missingHourCount 原样保留（AC-5.3）。
            ["missingSegments"] = coverage.Gaps
                .Select(gap => new PcQualityMissingSegmentDto(gap.StartUtc.ToString("O"), gap.EndUtc.ToString("O")))
                .ToList(),
            ["lastDataAtUtc"] = coverage.LastDataAtUtc?.ToString("O") ?? string.Empty,
            ["trailingGapMinutes"] = Math
                .Round(TrailingGapMinutes(coverage, rangeStart, coverageEnd))
                .ToString("0", CultureInfo.InvariantCulture),
            ["trailingGapFromUtc"] = coverage.TrailingGapStartUtc?.ToString("O") ?? string.Empty,
            ["coverageEmpty"] = (coverage.LastDataAtUtc is null).ToString()
        };

        return BuildComponent("tracker-events", "PC 原生追踪事件", componentIssues, details);
    }

    /// <summary>
    /// 近 <see cref="TrackerEventBaseline.Days"/> 个业务日的事件基线（中位数）。
    /// 业务日起点之间恒为 24h（Asia/Shanghai 无夏令时），因此直接按 24h 回推即可。
    /// </summary>
    private async Task<TrackerEventBaseline> BuildTrackerBaselineAsync(DateTimeOffset rangeStart, CancellationToken ct)
    {
        var baselineStart = rangeStart.AddDays(-TrackerEventBaseline.Days);
        var timestamps = await _db.Set<TrackerEventEntity>()
            .AsNoTracking()
            .Where(e => e.Timestamp >= baselineStart && e.Timestamp < rangeStart)
            .Select(e => e.Timestamp)
            .ToListAsync(ct);

        var dailyCounts = new int[TrackerEventBaseline.Days];
        foreach (var timestamp in timestamps)
        {
            var index = (int)Math.Floor((timestamp - baselineStart).TotalDays);
            if (index >= 0 && index < dailyCounts.Length)
                dailyCounts[index]++;
        }

        return TrackerEventBaseline.Create(dailyCounts);
    }

    /// <summary>范围内部的缺数时段：只报「两侧都有数据」的中间断档，起止空缺交给心跳/数据滞后判据。</summary>
    /// <summary>
    /// 起点早于 <paramref name="rangeStart"/>、但结束时刻落在范围之内的事件（跨范围起点的那一条）。
    /// <para>只有这类事件的结束时刻才可能晚于 <paramref name="rangeStart"/>，而结束时刻 = 起点 + 时长，
    /// 所以先用 <c>max(duration)</c> 把候选窗口收窄到 <c>[rangeStart - maxDuration, rangeStart)</c>，
    /// 再在内存里按结束时刻过滤 —— 避免把 <c>起点 + 时长</c> 写进 SQL 的 WHERE（翻译风险高）。</para>
    /// </summary>
    private async Task<List<TrackerEventEntity>> LoadEventsCrossingRangeStartAsync(
        DateTimeOffset rangeStart,
        CancellationToken ct)
    {
        var source = _db.Set<TrackerEventEntity>().AsNoTracking();
        var plausible = source.Where(e => e.Duration > 0 && e.Duration <= MaxPlausibleEventDurationSeconds);
        // REQ-3（#372）：一次可空 MAX 代替「AnyAsync + MaxAsync」两次往返，且走 duration 索引。
        var maxDurationSeconds = await plausible
            .Select(NullableSelector((TrackerEventEntity e) => e.Duration))
            .MaxAsync(ct) ?? 0;
        var lookbackSeconds = LookbackSeconds(maxDurationSeconds);
        if (lookbackSeconds <= 0)
            return new List<TrackerEventEntity>();

        var lookbackStart = ClampAddSeconds(rangeStart, -lookbackSeconds);
        var candidates = await source
            .Where(e => e.Timestamp >= lookbackStart && e.Timestamp < rangeStart)
            .ToListAsync(ct);

        return candidates
            .Where(e => EventEnd(e.Timestamp, e.Duration) > rangeStart)
            .ToList();
    }

    private static CoverageGaps BuildCoverage(
        DateTimeOffset rangeStart,
        DateTimeOffset coverageEnd,
        IReadOnlyCollection<TrackerEventEntity> trackerEvents,
        IReadOnlyCollection<KeystatsSampleEntity> samples,
        bool includeTrailingGap)
    {
        var coveredHours = new SortedSet<int>();
        DateTimeOffset? lastDataAtUtc = null;

        void Mark(DateTimeOffset start, DateTimeOffset end)
        {
            var clippedStart = start < rangeStart ? rangeStart : start;
            var clippedEnd = end > coverageEnd ? coverageEnd : end;

            // 零长区间（零时长事件 / KeyStats 采样点）也要算「这个小时有数据」，
            // 否则只有采样、没有窗口事件的小时会被误报成缺数。
            if (clippedEnd <= clippedStart)
            {
                if (start >= rangeStart && start < coverageEnd)
                {
                    coveredHours.Add((int)Math.Floor((start - rangeStart).TotalHours));
                    if (lastDataAtUtc is null || start > lastDataAtUtc)
                        lastDataAtUtc = start;
                }

                return;
            }

            if (lastDataAtUtc is null || clippedEnd > lastDataAtUtc)
                lastDataAtUtc = clippedEnd;

            var firstHour = (int)Math.Floor((clippedStart - rangeStart).TotalHours);
            var lastHour = (int)Math.Ceiling((clippedEnd - rangeStart).TotalHours) - 1;
            for (var hour = Math.Max(0, firstHour); hour <= lastHour; hour++)
                coveredHours.Add(hour);
        }

        foreach (var e in trackerEvents)
            Mark(e.Timestamp, EventEnd(e.Timestamp, e.Duration));
        foreach (var sample in samples)
            Mark(sample.SampledAtUtc, sample.SampledAtUtc);

        var totalHours = (int)Math.Ceiling((coverageEnd - rangeStart).TotalHours);
        var gaps = new List<CoverageGap>();
        var missingHourStarts = new List<DateTimeOffset>();
        int? previousCovered = null;

        for (var hour = 0; hour < totalHours; hour++)
        {
            if (coveredHours.Contains(hour))
            {
                // 只有当「之前已经有数据」时，中间的断档才算缺数时段；
                // 范围开头的空白是「本库那时还没有数据」，不属于同一次中断。
                if (previousCovered is int previous && hour - previous > 1)
                {
                    gaps.Add(new CoverageGap(
                        rangeStart.AddHours(previous + 1),
                        rangeStart.AddHours(hour)));
                    for (var missing = previous + 1; missing < hour; missing++)
                        missingHourStarts.Add(rangeStart.AddHours(missing));
                }

                previousCovered = hour;
            }
        }

        // 尾部断档：最后一条数据之后到（有效）范围末尾的连续空白。
        // 这是「从哪个时刻起就再没有数据」的直接答案；只有 ≥1 小时才计入，避免把收工前的空档变成噪音。
        // 计划内下线（关机/休眠）由调用方抑制 —— 那种空白有明确解释。
        var trailingGapStart = default(DateTimeOffset?);
        if (includeTrailingGap && lastDataAtUtc is not null)
        {
            var trailingStart = lastDataAtUtc.Value;
            if (coverageEnd - trailingStart >= MinimumReportedGap)
            {
                trailingGapStart = trailingStart;
                gaps.Add(new CoverageGap(trailingStart, coverageEnd));
                // 第一个「整小时都没数据」的小时：数据恰好停在整点时，该小时本身就算全缺。
                // 但如果尾部是「零长记录」（零时长 / 脏时长 / KeyStats 采样点）停在整点上，
                // 该小时已经被记为有数据，就不能再列进缺数列表（同一小时不该同时出现两处）。
                var firstMissingHour = (int)Math.Ceiling((trailingStart - rangeStart).TotalHours);
                if (previousCovered is int covered)
                    firstMissingHour = Math.Max(firstMissingHour, covered + 1);
                for (var hour = firstMissingHour; hour < totalHours; hour++)
                {
                    missingHourStarts.Add(rangeStart.AddHours(hour));
                }
            }
        }

        return new CoverageGaps(gaps, missingHourStarts, lastDataAtUtc, trailingGapStart, trackerEvents.Count);
    }

    /// <summary>
    /// 缺数判定 / 尾部断档 / 范围缺口共用的「有效范围末尾」：范围末尾在未来时取当前时刻。
    /// 查询尚未结束的业务日时，还没到的小时不是缺数。
    /// </summary>
    private static DateTimeOffset EffectiveRangeEnd(DateTimeOffset rangeEnd, DateTimeOffset checkedAt)
        => rangeEnd < checkedAt ? rangeEnd : checkedAt;

    /// <summary>
    /// 单条事件的合理时长上界（30 天）：超过它的记录按脏数据对待（结束时刻记为该事件的起点，即零贡献）。
    /// 既不该把内容地平线抬到 9999 年（那会把「内容是旧的」误诊成「只有心跳停」），
    /// 也不该让回看窗口大到把整张表捞出来。
    /// <para>用常量（而不是 <see cref="TimeSpan"/>）是因为它要进 SQL 过滤条件；
    /// <c>Duration &lt;= 常量</c> 在 PostgreSQL 与 .NET 上都会把 <c>NaN</c> 排除掉
    /// （PostgreSQL 里 <c>NaN</c> 比任何有限值都大，<c>NaN &lt;= x</c> 为假），
    /// 否则一行 <c>NaN</c> 会让 <c>MAX(duration)</c> 变成 <c>NaN</c>。</para>
    /// </summary>
    public const double MaxPlausibleEventDurationSeconds = 30 * 24 * 60 * 60;

    private static readonly TimeSpan MaxPlausibleEventDuration = TimeSpan.FromSeconds(MaxPlausibleEventDurationSeconds);

    /// <summary>
    /// 事件结束时刻。负时长、NaN、以及超过 <see cref="MaxPlausibleEventDuration"/> 的脏时长都按
    /// 「零长事件」处理（结束时刻 = 起点），不抛异常、也不假装内容延续到很久以后。
    /// </summary>
    private static DateTimeOffset EventEnd(DateTimeOffset timestamp, double durationSeconds)
    {
        if (double.IsNaN(durationSeconds) || durationSeconds <= 0)
            return timestamp;

        return durationSeconds > MaxPlausibleEventDuration.TotalSeconds
            ? timestamp
            : ClampAddSeconds(timestamp, durationSeconds);
    }

    /// <summary>按最大时长回推候选窗口时用的回看秒数（脏时长被截到合理上界）。</summary>
    private static double LookbackSeconds(double maxDurationSeconds)
        => double.IsNaN(maxDurationSeconds) || maxDurationSeconds <= 0
            ? 0
            : Math.Min(maxDurationSeconds, MaxPlausibleEventDuration.TotalSeconds);

    /// <summary>
    /// 加减秒数但把结果截在 <see cref="DateTimeOffset"/> 可表示范围内。用于两个地方：
    /// 事件结束时刻，以及「按最大时长回推的候选窗口起点」—— 后者的参数是在 EF 里当常量求值的，
    /// 极长时长会在求值阶段就抛 <c>ArgumentOutOfRangeException</c>，光包住结果计算不够。
    /// </summary>
    private static DateTimeOffset ClampAddSeconds(DateTimeOffset value, double seconds)
    {
        if (double.IsNaN(seconds) || seconds == 0)
            return value;

        try
        {
            return value.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 越界：截到可表示的一端。用 try/catch 而不是先比 TotalSeconds ——
            // 非零偏移的 DateTimeOffset 能加到的钟面范围与 UTC 差值不是一回事。
            return seconds > 0 ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>缺数时段的最小上报长度：低于这个长度的空白不单列（避免逐日噪音）。</summary>
    private static readonly TimeSpan MinimumReportedGap = TimeSpan.FromHours(1);

    /// <summary>
    /// 查询范围内「最后一条数据之后」到范围末尾的空白（分钟）。
    /// 范围内完全没有数据时返回整个有效范围长度（而不是 0）——「整段都没有数据」不该被读成「没有尾部空白」。
    /// </summary>
    private static double TrailingGapMinutes(CoverageGaps coverage, DateTimeOffset rangeStart, DateTimeOffset effectiveRangeEnd)
    {
        var from = coverage.LastDataAtUtc ?? rangeStart;
        return Math.Max(0, (effectiveRangeEnd - from).TotalMinutes);
    }

    /// <summary>本库「最新数据」时刻：心跳、AW / 原生事件结束时刻、KeyStats 样本时刻的最大值。</summary>
    private static DateTimeOffset? ComputeDataHorizon(
        DaemonHeartbeatEntity? heartbeat,
        IReadOnlyCollection<AwEventEntity> awEvents,
        IReadOnlyCollection<TrackerEventEntity> trackerEvents,
        IReadOnlyCollection<KeystatsSampleEntity> samples)
    {
        DateTimeOffset? horizon = heartbeat?.ReceivedAt;

        foreach (var e in awEvents)
            Consider(EventEnd(e.Timestamp, e.Duration));
        foreach (var e in trackerEvents)
            Consider(EventEnd(e.Timestamp, e.Duration));
        foreach (var sample in samples)
            Consider(sample.SampledAtUtc);

        return horizon;

        void Consider(DateTimeOffset value)
        {
            if (horizon is null || value > horizon)
                horizon = value;
        }
    }

    /// <summary>
    /// 全库范围的最新内容时刻（不含心跳）：AW 事件、原生事件、KeyStats 样本的最大时刻。
    /// 用于判断「这本库本身就旧」还是「采集端现在停了」——后者只有在内容另有新数据时才成立。
    /// 口径说明：事件按**结束时刻**（起点 + 时长）计，与 <see cref="ComputeDataHorizon"/> 一致。
    /// </summary>
    private async Task<DateTimeOffset?> ComputeDatabaseContentHorizonAsync(CancellationToken ct)
    {
        // 事件按**结束时刻**（Timestamp + Duration）计，与 ComputeDataHorizon 同口径。
        var awHorizon = await MaxEventEndAsync(
            _db.Set<AwEventEntity>().AsNoTracking(),
            e => e.Timestamp,
            e => e.Duration,
            e => e.Duration > 0 && e.Duration <= MaxPlausibleEventDurationSeconds,
            from => e => e.Timestamp >= from,
            ct);
        var trackerHorizon = await MaxEventEndAsync(
            _db.Set<TrackerEventEntity>().AsNoTracking(),
            e => e.Timestamp,
            e => e.Duration,
            e => e.Duration > 0 && e.Duration <= MaxPlausibleEventDurationSeconds,
            from => e => e.Timestamp >= from,
            ct);
        var sampleLatest = await _db.Set<KeystatsSampleEntity>().AsNoTracking()
            .OrderByDescending(s => s.SampledAtUtc)
            .Select(s => (DateTimeOffset?)s.SampledAtUtc)
            .FirstOrDefaultAsync(ct);

        DateTimeOffset? horizon = null;
        Consider(awHorizon);
        Consider(trackerHorizon);
        Consider(sampleLatest);

        return horizon;

        void Consider(DateTimeOffset? candidate)
        {
            if (candidate is not null && (horizon is null || candidate > horizon))
                horizon = candidate;
        }
    }

    /// <summary>
    /// 一张事件表的「内容结束时刻」：全表 <c>max(Timestamp + Duration)</c>（空表返回 null）。
    /// 不能只用「起点最新那一条 + 它自己的时长」推断 —— 一条更早开始、持续更久的事件可能结束得更晚，
    /// 会把本库的内容终点报早，让「读数为什么旧」给出错误的时刻。
    /// <para>
    /// REQ-3（#372）：聚合一律走**可空 <c>MAX(裸列)</c>**，一次往返即得结果 ——
    /// 旧写法「<c>AnyAsync</c> 探空 + <c>MaxAsync</c>」把每条聚合拆成两次往返（两张表共多 4 次）。
    /// 空集时 <c>MAX</c> 返回 NULL，语义与「先 Any 再 Max」完全一致，只是少了占位查询。
    /// 配合 <c>duration</c> 索引，<c>max(duration)</c> 由全表顺序扫描变为索引反向扫描（AC-3.1）。
    /// </para>
    /// </summary>
    private static async Task<DateTimeOffset?> MaxEventEndAsync<TEntity>(
        IQueryable<TEntity> source,
        Expression<Func<TEntity, DateTimeOffset>> timestampSelector,
        Expression<Func<TEntity, double>> durationSelector,
        Expression<Func<TEntity, bool>> plausibleDurationFilter,
        Func<DateTimeOffset, Expression<Func<TEntity, bool>>> windowFilter,
        CancellationToken ct)
        where TEntity : class
    {
        var maxTimestamp = await source
            .Select(NullableSelector(timestampSelector))
            .MaxAsync(ct);
        if (maxTimestamp is null)
            return null;

        // 只有起点落在 [maxTimestamp - maxDuration, maxTimestamp] 内的事件，其结束时刻才可能晚于 maxTimestamp；
        // 因此在这个窗口内取 max(Timestamp + Duration) 与全表结果一致，无需全表扫描。
        // 窗口按「合理时长」估算：脏时长（NaN / 超过 30 天 / 负数）本来就不贡献内容时长。
        var maxDurationSeconds = await source
            .Where(plausibleDurationFilter)
            .Select(NullableSelector(durationSelector))
            .MaxAsync(ct) ?? 0;
        var windowStart = ClampAddSeconds(maxTimestamp.Value, -LookbackSeconds(maxDurationSeconds));

        var horizon = maxTimestamp.Value;
        var timestampOf = timestampSelector.Compile();
        var durationOf = durationSelector.Compile();
        foreach (var entity in await source.Where(windowFilter(windowStart)).ToListAsync(ct))
        {
            var end = EventEnd(timestampOf(entity), durationOf(entity));
            if (end > horizon)
                horizon = end;
        }

        return horizon;
    }

    /// <summary>
    /// 把 <c>e =&gt; e.Column</c> 变成 <c>e =&gt; (T?)e.Column</c>，让 <c>MAX(裸列)</c> 在空集时返回 null。
    /// <para>只加一层可空转换，不把算式写进聚合表达式 —— 后者是本仓库刻意避免的高翻译风险写法
    /// （见 <c>PcQualityContentHorizonSqlTranslationTests</c>）。</para>
    /// </summary>
    private static Expression<Func<TEntity, TResult?>> NullableSelector<TEntity, TResult>(
        Expression<Func<TEntity, TResult>> selector)
        where TResult : struct
        => Expression.Lambda<Func<TEntity, TResult?>>(
            Expression.Convert(selector.Body, typeof(TResult?)),
            selector.Parameters);

    /// <summary>近 7 个业务日事件数基线（中位数）与本次范围的日均对比。</summary>
    private sealed record TrackerEventBaseline(
        double MedianDailyEventCount,
        double CurrentDailyEventCount)
    {
        public const int Days = 7;
        public const string Method = "近 7 个业务日事件数中位数";
        private const double LowRatio = 0.5;
        private const double HighRatio = 2.0;

        public static TrackerEventBaseline Create(int[] dailyCounts, double currentDailyEventCount = 0)
        {
            var sorted = dailyCounts.OrderBy(x => x).ToArray();
            var median = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
            return new TrackerEventBaseline(median, currentDailyEventCount);
        }

        public double? DeviationRatio
            => MedianDailyEventCount > 0 ? CurrentDailyEventCount / MedianDailyEventCount : null;

        public string Verdict
        {
            get
            {
                if (MedianDailyEventCount <= 0)
                    return "无基线";

                var ratio = CurrentDailyEventCount / MedianDailyEventCount;
                if (ratio < LowRatio)
                    return "偏低";
                if (ratio > HighRatio)
                    return "偏高";
                return "在基线区间内";
            }
        }

        public string Basis
            => MedianDailyEventCount <= 0
                ? "基线窗口内没有任何事件，无法判定（需要先恢复采集）。"
                : $"本次日均 {CurrentDailyEventCount:0.#} 条 ÷ 基线中位数 {MedianDailyEventCount:0.#} 条 = " +
                  $"{DeviationRatio:0.###}（<{LowRatio:0.##} 偏低，>{HighRatio:0.##} 偏高）。";

        public TrackerEventBaseline WithCurrentDailyEventCount(double value) => this with { CurrentDailyEventCount = value };
    }

    private sealed record CoverageGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    private sealed record CoverageGaps(
        IReadOnlyList<CoverageGap> Gaps,
        IReadOnlyList<DateTimeOffset> MissingHourStarts,
        DateTimeOffset? LastDataAtUtc,
        DateTimeOffset? TrailingGapStartUtc,
        int TrackerEventCount)
    {
        private const int MaxListedHours = 24;

        public int MissingHourCount => MissingHourStarts.Count;

        public DateTimeOffset? DisconnectedFromUtc => Gaps.Count > 0 ? Gaps[0].StartUtc : null;

        public string DescribeMissingHours()
            => string.Join("、", MissingHourStarts.Take(MaxListedHours).Select(FormatLocal));

        public string DescribeGaps()
            => string.Join("；", Gaps.Select(gap => $"{FormatLocal(gap.StartUtc)}–{FormatLocal(gap.EndUtc)}"));
    }

    private static string FormatLocal(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, ResolveBusinessTimeZone()).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveBusinessTimeZone()
    {
        const string primary = "Asia/Shanghai";
        const string fallback = "China Standard Time";
        try { return TimeZoneInfo.FindSystemTimeZoneById(primary); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(fallback); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById(fallback); }
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
        var details = new Dictionary<string, object?>
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
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset? dataHorizonUtc,
        DateTimeOffset? contentHorizonUtc,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var details = new Dictionary<string, object?>
        {
            ["rangeStartUtc"] = rangeStart.ToString("O"),
            ["rangeEndUtc"] = rangeEnd.ToString("O")
        };

        if (heartbeat is null)
        {
            details["heartbeat"] = "missing";
            details["staleReason"] = "heartbeat-missing";
            // 没有心跳就没有可比较的时间关系，但调用方仍然需要知道「本库到底有没有内容」：
            // 空库给 no-content，有内容但缺心跳给 heartbeat-missing（都写进 staleCause）。
            details["dataHorizonUtc"] = dataHorizonUtc?.ToString("O") ?? string.Empty;
            details["contentHorizonUtc"] = contentHorizonUtc?.ToString("O") ?? string.Empty;
            details["staleCause"] = contentHorizonUtc is null ? "no-content" : "heartbeat-missing";
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

        details["dataHorizonUtc"] = dataHorizonUtc?.ToString("O") ?? string.Empty;
        details["contentHorizonUtc"] = contentHorizonUtc?.ToString("O") ?? string.Empty;

        // REQ-8（#369）：心跳红灯**原样保留**（不为了消红而降级判据）；
        // 这里另外算一份「成因」，只描述本库/心跳的时间关系，**不参杂心跳年龄**，
        // 也不断言「一定是本库旧」或「一定是采集端停机」（只有心跳时刻时无法断言）：
        //   collector-heartbeat-stale        本库内容另有更新的数据，只有心跳停 → 心跳通道的问题
        //   content-and-heartbeat-frozen     心跳与内容一起停在很久以前，且查询范围超出内容 → 两种可能
        //   query-range-beyond-database-horizon 查询范围超出本库内容（心跳另有判断）→ 本库没有这段数据
        //   database-or-collector-frozen     心跳与内容一起停在很久以前，范围未超出 → 两种可能
        //   no-content                       本库没有任何事件/样本，无法判断
        //   heartbeat-missing                本库有内容但没有心跳，没有可比较的时间关系
        //   planned-offline / none           计划内下线 / 时间关系无可归因异常
        var effectiveRangeEnd = EffectiveRangeEnd(rangeEnd, checkedAt);
        var rangeShortfall = contentHorizonUtc is null
            ? (TimeSpan?)null
            : effectiveRangeEnd - contentHorizonUtc.Value;
        var databaseLag = contentHorizonUtc is null ? TimeSpan.Zero : checkedAt - contentHorizonUtc.Value;
        var rangeBeyondDatabase = rangeShortfall is not null && rangeShortfall.Value >= RangeBeyondDatabaseThreshold;
        // 「内容比心跳新」与「两者一起停住」都用双向容差判定，避免单边不等式把「内容更旧」也算成一起停住。
        var contentNewerThanHeartbeat = contentHorizonUtc is not null
            && contentHorizonUtc.Value - heartbeat.ReceivedAt > HeartbeatAtHorizonTolerance;
        var heartbeatAtContentHorizon = contentHorizonUtc is not null
            && (contentHorizonUtc.Value - heartbeat.ReceivedAt).Duration() <= HeartbeatAtHorizonTolerance;
        var libraryFrozen = heartbeatAtContentHorizon && databaseLag >= DatabaseLagThreshold;

        details["effectiveRangeEndUtc"] = effectiveRangeEnd.ToString("O");
        details["rangeShortfallMinutes"] = rangeShortfall is null
            ? string.Empty
            : Math.Round(Math.Max(0, rangeShortfall.Value.TotalMinutes)).ToString("0", CultureInfo.InvariantCulture);
        details["databaseLagMinutes"] = Math.Max(0, databaseLag.TotalMinutes).ToString("0.0");
        details["libraryFrozenAtHorizon"] = libraryFrozen.ToString();
        details["heartbeatStaleAt"] = (age >= DaemonLifecycleClassifier.AbnormalDaemonAge).ToString();
        details["staleCause"] = contentHorizonUtc is null
            ? "no-content"
            : string.Equals(lifecycle.State, "planned-offline", StringComparison.Ordinal)
                ? "planned-offline"
                : contentNewerThanHeartbeat
                    ? "collector-heartbeat-stale"
                    : libraryFrozen && rangeBeyondDatabase
                        ? "content-and-heartbeat-frozen"
                        : rangeBeyondDatabase
                            ? "query-range-beyond-database-horizon"
                            : libraryFrozen
                                ? "database-or-collector-frozen"
                                // 时间关系上没有可归因的异常（心跳确实过期只由 staleReason 表达）：
                                // 这里不因为「心跳年龄大」就写成 collector-heartbeat-stale ——
                                // 那个码专指「本库内容另有更新的数据，只有心跳停」。
                                : "none";

        // 附加说明（Warning）：只描述「本库有没有覆盖这段范围 / 是不是整体停住」，
        // 措辞不排除「采集端停机」这种可能（因为只有时间关系时两种情况无法区分）。
        if (!string.Equals(lifecycle.State, "planned-offline", StringComparison.Ordinal)
            && contentHorizonUtc is not null)
        {
            if (libraryFrozen)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    rangeBeyondDatabase ? "content-and-heartbeat-frozen" : "database-or-collector-frozen",
                    PimHealthStatus.Warning,
                    "daemon-upload",
                    $"本库最新内容与心跳都停在 {FormatLocal(contentHorizonUtc.Value)}（距今 {databaseLag.TotalHours:0.#} 小时）" +
                    (rangeBeyondDatabase
                        ? $"，且查询范围末尾 {FormatLocal(effectiveRangeEnd)} 超出本库内容 {rangeShortfall!.Value.TotalHours:0.#} 小时。"
                        : "。") +
                    "可能是滞后快照 / 同步中断，也可能是采集端自那时起停机 —— 请对照 dataHorizonUtc、contentHorizonUtc、databaseLagMinutes 判断。",
                    "对照 dataHorizonUtc / contentHorizonUtc / databaseLagMinutes 确认本库是否为滞后快照，" +
                    "并核对采集端在该时刻之后是否还有上报 —— 两者都可能，先取证据再下结论。"));
            }
            else if (rangeBeyondDatabase)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "range-beyond-database-horizon",
                    PimHealthStatus.Warning,
                    "daemon-upload",
                    $"本库最新内容止于 {FormatLocal(contentHorizonUtc.Value)}，比查询范围末尾 " +
                    $"{FormatLocal(effectiveRangeEnd)} 落后 {rangeShortfall!.Value.TotalHours:0.#} 小时 —— " +
                    "这段范围在本库里没有数据。",
                    "确认查询范围是否超出了本库已同步的数据。"));
            }
        }

        if (lifecycle.State == "planned-offline")
        {
            details["staleReason"] = "planned-offline";
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-planned-offline",
                PimHealthStatus.Unknown,
                "daemon-upload",
                "守护程序已正常下线（关机/休眠）。",
                "Windows 守护程序将在下次开机后自动恢复。"));
        }
        else if (age >= DaemonLifecycleClassifier.AbnormalDaemonAge)
        {
            // 心跳红灯不因「库可能旧」而降级（WO 明确不做第 6 条）。
            details["staleReason"] = StaleReasonCollectorStale;
            componentIssues.Add(new PcQualityIssueDto(
                "stale-windows-daemon-heartbeat",
                PimHealthStatus.Critical,
                "daemon-upload",
                "Windows 守护程序心跳已过期。",
                "重启 Windows 守护程序，并确认它能访问 API。"));
        }
        else if (age >= DaemonLifecycleClassifier.OnlineDaemonAge)
        {
            details["staleReason"] = "collector-heartbeat-old";
            componentIssues.Add(new PcQualityIssueDto(
                "old-daemon-heartbeat",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows 守护程序心跳偏旧。",
                "检查 Windows 守护程序是否仍在运行。"));
        }
        else
        {
            details["staleReason"] = "none";
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

    /// <param name="rangeTrackerEvents">
    /// 与查询范围重叠的原生事件（含起点在范围之前、伸进范围的记录）—— 与覆盖判定同源。
    /// </param>
    private static PcQualityComponentDto CheckTimeline(
        IReadOnlyCollection<AwEventEntity> awEvents,
        IReadOnlyCollection<TrackerEventEntity> rangeTrackerEvents,
        IReadOnlyCollection<KeystatsSampleEntity> samples,
        bool isPostAw,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var hasActivityEvents = awEvents.Count > 0 || rangeTrackerEvents.Count > 0;
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
        var details = new Dictionary<string, object?>
        {
            ["hasActivityEvents"] = hasActivityEvents.ToString(),
            ["hasActivityWatchEvents"] = (awEvents.Count > 0).ToString(),
            ["hasTrackerEvents"] = (rangeTrackerEvents.Count > 0).ToString(),
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
        IReadOnlyDictionary<string, object?> details)
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
