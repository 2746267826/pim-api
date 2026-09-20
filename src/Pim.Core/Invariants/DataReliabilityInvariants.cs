using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.Core.Invariants;

/// <summary>
/// 数据可信度 13 根基准尺子（S1–S13）生产实现。
/// 全部满足纯函数约定，无数据库/网络依赖，供 CI 与体检服务共用。
/// </summary>
public static class DataReliabilityInvariants
{
    private static readonly TimeZoneInfo ShanghaiTimeZone = ResolveShanghaiTimeZone();

    private static TimeZoneInfo ResolveShanghaiTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }

    #region S1–S5: 尺子组一 · 数据自洽

    /// <summary>构造违规结构化字段表（键固定英文，供导出与下钻展示）。</summary>
    private static IReadOnlyDictionary<string, string> Fields(params (string Key, string? Value)[] pairs)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            fields[key] = value ?? string.Empty;
        }

        return fields;
    }

    /// <summary>把可能是 Unspecified/Local 的时间统一成 UTC，避免导出出现时区漂移。</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>
    /// 把"按设备分别判定"的多份结论合并成一条尺子结论（S6 这类需要逐设备核对声明的尺子使用）。
    /// 语义：任一台设备报红即整条报红；没有红线但有设备无法判定（数据源缺失 / 未接线）则整条记未知
    /// ——不能因为"其它设备都通过"就替没数据的设备背书；只有全部通过才算通过。
    /// </summary>
    /// <param name="invariantCode">尺子的不变量编号，例如 <c>INV-P20</c>，只用于拼装结论文案。</param>
    /// <param name="perDeviceResults">每台设备各自的判定结果。</param>
    /// <param name="options">阈值配置（只用于样例数量上限）。</param>
    public static InvariantResult CombineDeviceVerdicts(
        string invariantCode,
        IReadOnlyList<InvariantResult> perDeviceResults,
        InvariantOptions? options = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var results = perDeviceResults ?? Array.Empty<InvariantResult>();

        if (results.Count == 0 || results.All(result => result.Status == InvariantStatus.Unknown))
        {
            return InvariantResult.Unknown($"{invariantCode} UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        var failed = results.Where(result => result.Status == InvariantStatus.Fail).ToList();
        if (failed.Count > 0)
        {
            var samples = failed.SelectMany(result => result.Samples).Take(opt.MaxSampleCount).ToList();
            var violations = failed.SelectMany(result => result.Violations).Take(opt.MaxSampleCount).ToList();
            var earliestOccurrences = failed
                .Select(result => result.EarliestOccurrence)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();
            var latestOccurrences = failed
                .Select(result => result.LatestOccurrence)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            int total = failed.Sum(result => result.TotalViolations);
            int newCount = failed.Sum(result => result.NewViolations);
            int historical = failed.Sum(result => result.HistoricalViolations);

            return InvariantResult.Failure(
                $"{invariantCode} FAIL: 检测到 {total} 处违规（覆盖 {failed.Count} 台设备）",
                total,
                newCount,
                historical,
                samples,
                earliestOccurrences.Count > 0 ? earliestOccurrences.Min() : null,
                latestOccurrences.Count > 0 ? latestOccurrences.Max() : null,
                note,
                fallback,
                violations: violations);
        }

        if (results.Any(result => result.Status == InvariantStatus.Unknown))
        {
            return InvariantResult.Unknown($"{invariantCode} UNKNOWN: 部分设备无数据可判定", note, fallback);
        }

        // 任一设备只判到黄线时，整条尺子必须是黄线 —— 绝不能因为"没有设备报红"
        // 就把存量违规折成绿灯（那会让面板显示"全绿"而实际上有设备存在存量欠账）。
        var warned = results.Where(result => result.Status == InvariantStatus.Warning).ToList();
        if (warned.Count > 0)
        {
            var samples = warned.SelectMany(result => result.Samples).Take(opt.MaxSampleCount).ToList();
            var violations = warned.SelectMany(result => result.Violations).Take(opt.MaxSampleCount).ToList();
            var earliestOccurrences = warned
                .Select(result => result.EarliestOccurrence)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();
            var latestOccurrences = warned
                .Select(result => result.LatestOccurrence)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            int total = warned.Sum(result => result.TotalViolations);
            int newCount = warned.Sum(result => result.NewViolations);
            int historical = warned.Sum(result => result.HistoricalViolations);

            return InvariantResult.Failure(
                $"{invariantCode} WARN: 检测到 {total} 处存量违规（覆盖 {warned.Count} 台设备，无新增）",
                total,
                newCount,
                historical,
                samples,
                earliestOccurrences.Count > 0 ? earliestOccurrences.Min() : null,
                latestOccurrences.Count > 0 ? latestOccurrences.Max() : null,
                note,
                fallback,
                isWarning: true,
                violations: violations);
        }

        return InvariantResult.Success($"{invariantCode} PASS: 全部 {results.Count} 台设备均通过", note, fallback);
    }

    /// <summary>
    /// 业务键的不可逆摘要（SHA-256 前 16 个十六进制字符）。
    /// 业务键本身可能包含经纬度等精确个人数据，判据内部照常按原键分组，但对外只暴露摘要。
    /// </summary>
    private static string ObfuscateBusinessKey(string businessKey) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(businessKey)))[..16];

    /// <summary>S11 结构化违规引用：批次号 + 语义不自洽的原因（业务时间取窗口起点）。</summary>
    private static InvariantViolation BatchViolation(BatchSyncStatusRecord batch, string reason) =>
        new(
            Id: batch.BatchId,
            DeviceId: string.Empty,
            OccurredAtUtc: ToUtc(batch.WindowStartUtc),
            Fields: Fields(
                ("status", batch.Status),
                ("failedCount", batch.FailedCount.ToString()),
                ("acceptedCount", batch.AcceptedCount.ToString()),
                ("rejectedCount", batch.RejectedCount.ToString()),
                ("skippedCount", batch.SkippedCount.ToString()),
                ("reason", reason)));

    /// <summary>
    /// S2 (INV-P17): 超长事件的三态分布。
    /// 判定顺序必须与 <see cref="CheckS2_OverlongEventEvidence"/> 完全一致：
    /// 先排除"明确的空档"，再看操作活跃（键鼠输入密度 ≥ T1a），再看观看活跃（媒体活动），
    /// 三者都不满足归入"疑似未收尾"。
    /// 只统计时长超过 T1b 超长线的事件；空输入返回全 0，不抛异常。
    /// 目的：让设置页一眼看出"有多少时长其实是没人收尾的挂机时间"（EPIC #254 §5、#260 §3）。
    /// </summary>
    public static S2ThreeStateDistribution ClassifyS2ThreeStates(
        IEnumerable<LongEventCandidate> events,
        InvariantOptions? options = null)
    {
        var (opt, _, _) = InvariantOptions.Resolve(options);

        double inputActiveSeconds = 0;
        double mediaActiveSeconds = 0;
        double suspectedUnclosedSeconds = 0;
        double declaredGapSeconds = 0;
        int inputActiveCount = 0;
        int mediaActiveCount = 0;
        int suspectedUnclosedCount = 0;

        foreach (var candidate in events ?? Array.Empty<LongEventCandidate>())
        {
            var durationMinutes = (candidate.EndTime - candidate.StartTime).TotalMinutes;
            if (durationMinutes <= opt.LongEventThresholdMinutes)
            {
                continue;
            }

            var durationSeconds = Math.Max(0, durationMinutes * 60);

            // 态 3: 明确的空档 —— 它本来就声明"这里没有人"，不属于活跃时长。
            if (candidate.IsGapOrOffline || IsDeclaredGapEventType(candidate.EventType))
            {
                declaredGapSeconds += durationSeconds;
                continue;
            }

            // 态 1: 操作活跃
            double inputDensity = (candidate.Keystrokes + candidate.MouseClicks) / durationMinutes;
            if (inputDensity >= opt.MinInputDensityPerMinute)
            {
                inputActiveSeconds += durationSeconds;
                inputActiveCount++;
                continue;
            }

            // 态 2: 观看活跃
            if (candidate.IsMediaActive || candidate.IsAudible)
            {
                mediaActiveSeconds += durationSeconds;
                mediaActiveCount++;
                continue;
            }

            suspectedUnclosedSeconds += durationSeconds;
            suspectedUnclosedCount++;
        }

        return new S2ThreeStateDistribution(
            InputActiveSeconds: inputActiveSeconds,
            MediaActiveSeconds: mediaActiveSeconds,
            SuspectedUnclosedSeconds: suspectedUnclosedSeconds,
            TotalSeconds: inputActiveSeconds + mediaActiveSeconds + suspectedUnclosedSeconds,
            InputActiveCount: inputActiveCount,
            MediaActiveCount: mediaActiveCount,
            SuspectedUnclosedCount: suspectedUnclosedCount,
            DeclaredGapSeconds: declaredGapSeconds);
    }

    /// <summary>事件类型是否属于"缺数据"类（gap / sleep / shutdown / offline）。</summary>
    private static bool IsDeclaredGapEventType(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType))
        {
            return false;
        }

        return eventType.Contains("gap", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("sleep", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("shutdown", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("offline", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把违规分档写成一句人话，例如"新增 3 / 存量 11"，供体检接口与质量报告共用。
    /// </summary>
    public static string DescribeViolationSplit(InvariantResult result) =>
        $"新增 {result.NewViolations} / 存量 {result.HistoricalViolations}";

    /// <summary>
    /// S1 (INV-P16): 同类型事件不重叠
    /// 判据: 同设备、同事件类型的事件区间两两不相交（不存在 A.start &lt; B.end &amp;&amp; B.start &lt; A.end）。
    /// 阈值: 重叠对数 = 0（针对新增数据；存量走黄线）。
    /// 为什么是这个阈值: 同一设备在同一时刻不可能产生两个同级别的互斥前台焦点或互斥状态，重叠说明采集端或入库去重损坏。
    /// </summary>
    public static InvariantResult CheckS1_NoOverlap(
        IEnumerable<EventTimeSpan> events,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);

        var list = events?.ToList() ?? new List<EventTimeSpan>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-P16 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        var groups = list.GroupBy(e => (e.DeviceId, e.EventType));

        int totalViolations = 0;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();
        DateTime? earliest = null;
        DateTime? latest = null;

        foreach (var group in groups)
        {
            var sorted = group.OrderBy(e => e.StartTime).ThenBy(e => e.EndTime).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    var a = sorted[i];
                    var b = sorted[j];

                    // 由于已按 StartTime 排序，若 b.StartTime >= a.EndTime 则与 a 不可能再相交
                    if (b.StartTime >= a.EndTime)
                        break;

                    if (a.StartTime < b.EndTime && b.StartTime < a.EndTime)
                    {
                        totalViolations++;
                        var overlapEnd = a.EndTime < b.EndTime ? a.EndTime : b.EndTime;
                        bool isNew = overlapEnd >= cutoff;
                        if (isNew) newViolations++; else historicalViolations++;

                        earliest = earliest == null || a.StartTime < earliest ? a.StartTime : earliest;
                        latest = latest == null || overlapEnd > latest ? overlapEnd : latest;

                        if (samples.Count < opt.MaxSampleCount)
                        {
                            samples.Add($"Device={a.DeviceId}, Type={a.EventType}: [{a.StartTime:yyyy-MM-dd HH:mm:ss} ~ {a.EndTime:yyyy-MM-dd HH:mm:ss}] overlaps with [{b.StartTime:yyyy-MM-dd HH:mm:ss} ~ {b.EndTime:yyyy-MM-dd HH:mm:ss}] (New={isNew})");
                            violations.Add(new InvariantViolation(
                                Id: string.IsNullOrEmpty(a.EventId) ? $"{a.DeviceId}:{a.StartTime:O}" : a.EventId,
                                DeviceId: a.DeviceId,
                                OccurredAtUtc: ToUtc(a.StartTime),
                                Fields: Fields(
                                    ("eventType", a.EventType),
                                    ("startUtc", ToUtc(a.StartTime).ToString("O")),
                                    ("endUtc", ToUtc(a.EndTime).ToString("O")),
                                    ("overlapWithId", b.EventId),
                                    ("overlapSeconds", (overlapEnd - b.StartTime).TotalSeconds.ToString("F0")),
                                    ("isNew", isNew ? "true" : "false"))));
                        }
                    }
                }
            }
        }

        if (totalViolations > 0)
        {
            return InvariantResult.Failure(
                $"INV-P16 FAIL: 检测到 {totalViolations} 对同类型事件重叠 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                earliest,
                latest,
                note,
                fallback,
                violations: violations);
        }

        return InvariantResult.Success("INV-P16 PASS: 无同类型事件重叠", note, fallback);
    }

    /// <summary>
    /// S2 (INV-P17): 超长事件必须拿得出活动证据（三态判定）
    /// 判据: 任何 > 30 分钟 (T1b) 的事件，其区间内必须命中以下三态之一：
    ///   1. 操作活跃: 键鼠输入密度 &gt;= 1 次/分钟 (T1a) —— (按键增量 + 点击增量) ÷ 时长(分钟)
    ///   2. 观看活跃: 区间内存在媒体活动 (is_media_active / audible)
    ///   3. 明确的空档: 事件类型为"缺数据"类 (gap, shutdown, sleep 等)，它本来就声明"这里没有人"
    ///   三者都不满足 ⇒ 判「疑似未收尾」，必须显式标记，且不得计入活跃时长。
    /// 阈值: 超长线 30.0 分钟 (T1b)，输入密度线 1.0 次/分钟 (T1a)。
    /// 为什么是这个阈值: 真实人类操作或媒体播放即使挂机也会有心跳或媒体活动，超过 30m 没有任何信号大概率是采集端未收到退出事件造成的僵尸时长。
    /// </summary>
    public static InvariantResult CheckS2_OverlongEventEvidence(
        IEnumerable<LongEventCandidate> events,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);

        var list = events?.ToList() ?? new List<LongEventCandidate>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-P17 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        int totalViolations = 0;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();
        DateTime? earliest = null;
        DateTime? latest = null;

        foreach (var e in list)
        {
            var durationMinutes = (e.EndTime - e.StartTime).TotalMinutes;
            if (durationMinutes <= opt.LongEventThresholdMinutes)
            {
                continue; // 不属于超长事件，通过
            }

            // 三态判定（与 ClassifyS2ThreeStates 共用同一套口径）
            // 态 3: 明确的空档
            if (e.IsGapOrOffline || IsDeclaredGapEventType(e.EventType))
            {
                continue;
            }

            // 态 1: 操作活跃
            double inputDensity = durationMinutes > 0 ? (e.Keystrokes + e.MouseClicks) / durationMinutes : 0;
            if (inputDensity >= opt.MinInputDensityPerMinute)
            {
                continue;
            }

            // 态 2: 观看活跃
            if (e.IsMediaActive || e.IsAudible)
            {
                continue;
            }

            // 都不满足：判「疑似未收尾」
            totalViolations++;
            bool isNew = e.EndTime >= cutoff;
            if (isNew) newViolations++; else historicalViolations++;

            earliest = earliest == null || e.StartTime < earliest ? e.StartTime : earliest;
            latest = latest == null || e.EndTime > latest ? e.EndTime : latest;

            if (samples.Count < opt.MaxSampleCount)
            {
                samples.Add($"Device={e.DeviceId}, Event={e.EventId ?? e.EventType}, App={e.AppName ?? "N/A"}, Duration={durationMinutes:F1}m > {opt.LongEventThresholdMinutes:F1}m: 疑似未收尾 (无操作密度[{inputDensity:F2}/min < {opt.MinInputDensityPerMinute:F1}], 无媒体活动, 非明确空档)");
                violations.Add(new InvariantViolation(
                    Id: string.IsNullOrEmpty(e.EventId) ? $"{e.DeviceId}:{e.StartTime:O}" : e.EventId,
                    DeviceId: e.DeviceId,
                    OccurredAtUtc: ToUtc(e.StartTime),
                    Fields: Fields(
                        ("eventType", e.EventType),
                        ("app", e.AppName),
                        ("startUtc", ToUtc(e.StartTime).ToString("O")),
                        ("endUtc", ToUtc(e.EndTime).ToString("O")),
                        ("durationMinutes", durationMinutes.ToString("F1")),
                        ("inputDensityPerMinute", inputDensity.ToString("F2")),
                        ("isNew", isNew ? "true" : "false"))));
            }
        }

        if (totalViolations > 0)
        {
            return InvariantResult.Failure(
                $"INV-P17 FAIL: 检测到 {totalViolations} 个疑似未收尾超长事件 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                earliest,
                latest,
                note,
                fallback,
                violations: violations);
        }

        return InvariantResult.Success("INV-P17 PASS: 所有超长事件均有合规的活动证据或为明确空档", note, fallback);
    }

    /// <summary>
    /// S3 (INV-P18): 原始活动事件聚合：三态过滤与重叠区间合并去重。
    /// 1. 过滤三态：
    ///    - Gap/休眠/离线：排除出活跃区间，计入 GapSeconds；
    ///    - Idle：排除出活跃区间，计入 IdleSeconds；
    ///    - 疑似未收尾（时长 > 30m 且无输入密度且无媒体/音频）：排除出活跃区间，计入 SuspectedUnclosedSeconds；
    ///    - 活跃（操作活跃或观看活跃）：进入待合并活跃区间。
    /// 2. 区间合并去重：
    ///    - 排序后对重叠区间进行合并（同一时刻只计入一次），杜绝 window 与 web-page 等并发事件时长直接累加。
    /// </summary>
    public static List<DailyActiveDuration> AggregateDailyActiveDurations(
        IEnumerable<RawActivityEvent> events,
        InvariantOptions? options = null)
    {
        var (opt, _, _) = InvariantOptions.Resolve(options);
        var list = events?.ToList() ?? new List<RawActivityEvent>();
        var result = new List<DailyActiveDuration>();

        var groups = list.GroupBy(e => (e.DeviceId, e.BusinessDate));
        foreach (var g in groups)
        {
            double gapSec = 0;
            double idleSec = 0;
            double unclosedSec = 0;
            var activeIntervals = new List<(DateTime Start, DateTime End)>();

            foreach (var ev in g)
            {
                if (ev.DurationSeconds <= 0) continue;

                bool isGap = ev.EventType.Equals("gap", StringComparison.OrdinalIgnoreCase) ||
                             ev.EventType.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
                             ev.EventType.Equals("sleep", StringComparison.OrdinalIgnoreCase);

                if (isGap)
                {
                    gapSec += ev.DurationSeconds;
                }
                else if (ev.IsIdle)
                {
                    idleSec += ev.DurationSeconds;
                }
                else
                {
                    bool isMedia = ev.IsMediaActive || ev.Audible;
                    bool isSuspectedUnclosed = ev.DurationSeconds > (opt.LongEventThresholdMinutes * 60.0) &&
                                               !isMedia &&
                                               ev.InputDensityPerMinute < 1.0;

                    if (isSuspectedUnclosed)
                    {
                        unclosedSec += ev.DurationSeconds;
                    }
                    else
                    {
                        var start = ev.Timestamp;
                        var end = ev.Timestamp.AddSeconds(ev.DurationSeconds);
                        if (end > start)
                        {
                            activeIntervals.Add((start, end));
                        }
                    }
                }
            }

            // 区间合并去重
            activeIntervals.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(DateTime Start, DateTime End)>();
            foreach (var interval in activeIntervals)
            {
                if (merged.Count == 0 || interval.Start > merged[^1].End)
                {
                    merged.Add(interval);
                }
                else
                {
                    var last = merged[^1];
                    if (interval.End > last.End)
                    {
                        merged[^1] = (last.Start, interval.End);
                    }
                }
            }

            double rawActiveSec = activeIntervals.Sum(i => (i.End - i.Start).TotalSeconds);
            double mergedActiveSec = merged.Sum(m => (m.End - m.Start).TotalSeconds);
            double overlapRemovedSec = Math.Max(0, rawActiveSec - mergedActiveSec);

            result.Add(new DailyActiveDuration
            {
                DeviceId = g.Key.DeviceId,
                Date = g.Key.BusinessDate,
                ActiveDurationSeconds = mergedActiveSec,
                MergedActiveSeconds = mergedActiveSec,
                OverlapRemovedSeconds = overlapRemovedSec,
                IdleSeconds = idleSec,
                GapSeconds = gapSec,
                SuspectedUnclosedSeconds = unclosedSec
            });
        }

        return result;
    }

    /// <summary>
    /// S3 (INV-P18): 单日时长有界
    /// 判据: 按 Asia/Shanghai 04:00 起算的单日活跃时长（仅三态前两态）：
    ///   硬上限: 单日活跃合计 &lt;= 24h
    ///   警告线: 单日活跃合计 &lt;= 清醒窗口 (默认 16h) × 90%
    /// 阈值: 硬上限 24.0 小时 (T5)，清醒窗口 16.0 小时，警告比例 0.9 (14.4h)。
    /// 为什么是这个阈值: 一天物理上只有 24 小时；人类正常作息清醒时间约 16 小时，超过 14.4 小时说明极高强度活跃或存在异常累积。
    /// </summary>
    public static InvariantResult CheckS3_DailyDurationBounded(
        IEnumerable<DailyActiveDuration> dailyDurations,
        InvariantOptions? options = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var hardCapSeconds = opt.MaxDailyActiveHours * 3600.0;
        var warningSeconds = opt.AwakeWindowHours * opt.AwakeWindowWarningRatio * 3600.0;

        var list = dailyDurations?.ToList() ?? new List<DailyActiveDuration>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-P18 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        int totalViolations = 0;
        int warningCount = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();

        foreach (var d in list.OrderByDescending(d => d.ActiveDurationSeconds))
        {
            if (d.ActiveDurationSeconds > hardCapSeconds)
            {
                totalViolations++;
                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"Date={d.Date}, Device={d.DeviceId}: 合并活跃={d.ActiveDurationSeconds / 3600.0:F2}h (去重重叠 {d.OverlapRemovedSeconds / 3600.0:F2}h), Idle={d.IdleSeconds / 3600.0:F2}h, Gap={d.GapSeconds / 3600.0:F2}h, 剔除疑似未收尾={d.SuspectedUnclosedSeconds / 3600.0:F2}h > 硬上限 {opt.MaxDailyActiveHours:F1}h");
                    violations.Add(new InvariantViolation(
                        Id: $"{d.DeviceId}:{d.Date}",
                        DeviceId: d.DeviceId,
                        OccurredAtUtc: ResolveBusinessDayStartUtc(d.Date),
                        Fields: Fields(
                            ("date", d.Date),
                            ("activeHours", (d.ActiveDurationSeconds / 3600.0).ToString("F2")),
                            ("idleHours", (d.IdleSeconds / 3600.0).ToString("F2")),
                            ("gapHours", (d.GapSeconds / 3600.0).ToString("F2")),
                            ("suspectedUnclosedHours", (d.SuspectedUnclosedSeconds / 3600.0).ToString("F2")))));
                }
            }
            else if (d.ActiveDurationSeconds > warningSeconds)
            {
                warningCount++;
                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"[Warning] Date={d.Date}, Device={d.DeviceId}: 合并活跃={d.ActiveDurationSeconds / 3600.0:F2}h (去重重叠 {d.OverlapRemovedSeconds / 3600.0:F2}h), Idle={d.IdleSeconds / 3600.0:F2}h, Gap={d.GapSeconds / 3600.0:F2}h, 剔除疑似未收尾={d.SuspectedUnclosedSeconds / 3600.0:F2}h > 警告线 {warningSeconds / 3600.0:F2}h");
                }
            }
            else if (samples.Count < 2 && d.ActiveDurationSeconds > 0)
            {
                samples.Add($"Date={d.Date}, Device={d.DeviceId}: 合并活跃={d.ActiveDurationSeconds / 3600.0:F2}h (去重重叠 {d.OverlapRemovedSeconds / 3600.0:F2}h), Idle={d.IdleSeconds / 3600.0:F2}h, Gap={d.GapSeconds / 3600.0:F2}h, 剔除疑似未收尾={d.SuspectedUnclosedSeconds / 3600.0:F2}h");
            }
        }

        if (totalViolations > 0)
        {
            return InvariantResult.Failure(
                $"INV-P18 FAIL: 检测到 {totalViolations} 个单日合并活跃时长超过硬上限 {opt.MaxDailyActiveHours:F1}h",
                totalViolations,
                totalViolations,
                0,
                samples,
                null,
                null,
                note,
                fallback,
                violations: violations);
        }

        if (warningCount > 0)
        {
            return InvariantResult.Warning(
                $"INV-P18 WARN: 单日合并活跃时长未超硬上限，但存在 {warningCount} 天超过清醒窗口警告线 {warningSeconds / 3600.0:F1}h",
                samples: samples,
                thresholdNote: note,
                thresholdFallback: fallback);
        }

        double maxDayHours = list.Max(d => d.ActiveDurationSeconds) / 3600.0;
        string detailSuffix = samples.Count > 0 ? $". 明细样例: [{string.Join("; ", samples)}]" : string.Empty;
        return InvariantResult.Success($"INV-P18 PASS: 单日活跃时长经区间合并去重与三态过滤后符合生理与物理上限 (最大单日 {maxDayHours:F2}h <= 警告线 {opt.AwakeWindowHours * opt.AwakeWindowWarningRatio:F1}h){detailSuffix}", note, fallback);
    }

    /// <summary>
    /// S4 (INV-C18): 业务键唯一（不重复）
    /// 判据:
    ///   定位: (device, recorded_at, lat, lon) 唯一
    ///   手机事件: (device, package, event_time, event_type) 唯一
    ///   PC 事件: (device, timestamp, duration, event_type, app_name, browser, instance_id) 唯一
    /// 阈值: 新增重复行 = 0（存量走黄线）。
    /// 为什么是这个阈值: 重复事件会导致时长与频次双重虚高，破坏聚合指标的可信度。
    /// </summary>
    public static InvariantResult CheckS4_BusinessKeyUnique(
        IEnumerable<BusinessRecordKey> records,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);

        var list = records?.ToList() ?? new List<BusinessRecordKey>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-C18 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        var groups = list.GroupBy(r => (r.Domain, r.UniqueKey));

        int totalViolations = 0;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();
        DateTime? earliest = null;
        DateTime? latest = null;

        foreach (var g in groups)
        {
            var count = g.Count();
            if (count > 1)
            {
                int dups = count - 1;
                totalViolations += dups;

                foreach (var item in g.Skip(1))
                {
                    bool isNew = item.Timestamp >= cutoff;
                    if (isNew) newViolations++; else historicalViolations++;

                    earliest = earliest == null || item.Timestamp < earliest ? item.Timestamp : earliest;
                    latest = latest == null || item.Timestamp > latest ? item.Timestamp : latest;
                }

                if (samples.Count < opt.MaxSampleCount)
                {
                    var first = g.First();
                    // 业务键里可能含经纬度（定位域），样例与导出只输出不可逆摘要，避免把精确坐标带出去。
                    string opaqueKey = ObfuscateBusinessKey(first.UniqueKey);
                    samples.Add($"Domain={first.Domain}, Device={first.DeviceId}, KeyDigest={opaqueKey}: 重复出现 {count} 次");
                    violations.Add(new InvariantViolation(
                        Id: opaqueKey,
                        DeviceId: first.DeviceId,
                        OccurredAtUtc: ToUtc(first.Timestamp),
                        Fields: Fields(
                            ("domain", first.Domain),
                            ("duplicateCount", count.ToString()))));
                }
            }
        }

        if (totalViolations > 0)
        {
            return InvariantResult.Failure(
                $"INV-C18 FAIL: 检测到 {totalViolations} 行业务键重复 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                earliest,
                latest,
                note,
                fallback,
                violations: violations);
        }

        return InvariantResult.Success("INV-C18 PASS: 业务键唯一无重复", note, fallback);
    }

    /// <summary>
    /// S5 (INV-P19): 时钟可信
    /// 判据: 事件时间戳 &lt;= 服务端接收时间 + 容差。
    /// 阈值: 容差 5.0 分钟 (ClockSkewToleranceMinutes)。
    /// 为什么是这个阈值: 客户端时钟可能与网络授时存在少许偏差或时钟漂移，5 分钟为工业标准网络时间容限；超过 5 分钟属于严重超前或时钟穿越。
    /// </summary>
    public static InvariantResult CheckS5_ClockTrustworthy(
        IEnumerable<ClockEventItem> items,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);
        var toleranceSeconds = opt.ClockSkewToleranceMinutes * 60.0;

        var list = items?.ToList() ?? new List<ClockEventItem>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-P19 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        int totalViolations = 0;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();
        DateTime? earliest = null;
        DateTime? latest = null;

        foreach (var item in list)
        {
            var skewSeconds = (item.EventTime - item.ServerReceivedTime).TotalSeconds;
            if (skewSeconds > toleranceSeconds)
            {
                totalViolations++;
                bool isNew = item.ServerReceivedTime >= cutoff;
                if (isNew) newViolations++; else historicalViolations++;

                earliest = earliest == null || item.EventTime < earliest ? item.EventTime : earliest;
                latest = latest == null || item.EventTime > latest ? item.EventTime : latest;

                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"Device={item.DeviceId}, Event={item.EventId}: EventTime={item.EventTime:yyyy-MM-dd HH:mm:ss} 超前 ReceivedTime={item.ServerReceivedTime:yyyy-MM-dd HH:mm:ss} 达到 {skewSeconds / 60.0:F1}m > 容差 {opt.ClockSkewToleranceMinutes:F1}m");
                    violations.Add(new InvariantViolation(
                        Id: string.IsNullOrEmpty(item.EventId) ? $"{item.DeviceId}:{item.EventTime:O}" : item.EventId,
                        DeviceId: item.DeviceId,
                        OccurredAtUtc: ToUtc(item.EventTime),
                        Fields: Fields(
                            ("serverReceivedUtc", ToUtc(item.ServerReceivedTime).ToString("O")),
                            ("skewMinutes", (skewSeconds / 60.0).ToString("F1")))));
                }
            }
        }

        if (totalViolations > 0)
        {
            bool isWarning = newViolations == 0 && historicalViolations > 0;
            return InvariantResult.Failure(
                $"INV-P19 {(isWarning ? "WARN" : "FAIL")}: 检测到 {totalViolations} 个事件时钟超前 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                earliest,
                latest,
                note,
                fallback,
                isWarning: isWarning,
                violations: violations);
        }

        return InvariantResult.Success("INV-P19 PASS: 所有事件时间戳均在合理时钟容差范围内", note, fallback);
    }

    #endregion

    #region S6–S9: 尺子组二 · 覆盖完整

    /// <summary>
    /// S6 (INV-P20): 设备必须自己声明下线
    /// 判据:
    ///   1. 空档必须被声明：设备无数据的时间段必须有"正常下线"声明（关机/休眠/planned offline）；没有声明的空档 &gt; 30 分钟 = 红
    ///   2. 上传必须及时：created_at - 事件时间 的 p99 &lt;= 30 分钟 (T2)
    ///   3. 停摆必须可解释：相邻事件间隔 &gt; 30 分钟且未声明下线 = 红
    /// 阈值: 无声明空档阈值 30.0 分钟 (T2)，上传滞后 p99 阈值 30.0 分钟 (T2)。
    /// 为什么是这个阈值: 现代操作系统关机与睡眠都有系统钩子；若无声明突然停止 30m，说明采集端崩溃或掉线；上传 p99 超过 30m 表明链路堆积积压严重。
    ///
    /// 实现口径（#254 S6，本轮修正）：
    ///   1. 空档按「上一段**结束**（滚动最大值）→ 下一段**开始**」计算，并按业务时间做 T4 新增/存量分档。
    ///      旧实现取「相邻起点之差」，把事件自身时长也当成空档 —— 实测把 29 处真实空档放大成 72 处；
    ///   2. 上传滞后 p99 排除系统合成的 gap 事件（其 created_at - timestamp 恒等于断档时长，不是链路延迟。
    ///      实测：含 gap 时 p99 = 425.9 分钟，排除后 19.2 分钟，阈值 30 分钟）；
    ///   3. 仅有存量违规时降级为黄线（与 S11 同一模式，T4）。
    /// </summary>
    public static InvariantResult CheckS6_OfflineDeclared(
        DeviceActivityTrace trace,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var gapThresholdMinutes = opt.UndeclaredOfflineGapMinutes;
        var p99LagMinutesThreshold = opt.MaxUploadLagP99Minutes;

        // 声明时刻与空档边界的允许偏差：客户端在"停止出数"前后数分钟内才写声明，
        // 且心跳上报本身有延迟，因此边界留 5 分钟宽限。
        const double declarationGraceMinutes = 5.0;

        if (trace == null || trace.EventIntervals == null || trace.EventIntervals.Count == 0)
        {
            return InvariantResult.Unknown("INV-P20 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);

        int totalViolations = 0;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();
        DateTime? earliest = null;
        DateTime? latest = null;

        // 1. 检查事件区间之间的空档是否被声明覆盖。
        //    空档 = 「截至上一段的滚动最大结束时刻」→「下一段开始」。用滚动最大值而不是
        //    前一段的结束，是为了同时覆盖区间互相重叠的输入（重叠时后一段整体落在前一段内部）。
        var sortedIntervals = trace.EventIntervals
            .OrderBy(i => i.StartTime)
            .ThenBy(i => i.EndTime)
            .ToList();

        var cursor = sortedIntervals[0].EndTime;
        for (int i = 1; i < sortedIntervals.Count; i++)
        {
            var interval = sortedIntervals[i];
            var gapStart = cursor;
            var gapEnd = interval.StartTime;
            var gapMinutes = (gapEnd - gapStart).TotalMinutes;

            if (gapMinutes > gapThresholdMinutes)
            {
                // 下线声明是否解释了这个空档。声明有两种形态，必须分别判定
                // （见 OfflineDeclaration 的注释）：
                //   * 区间声明（Start < End）：客户端明确给出了离线起止，
                //     要求它**完整覆盖**这个空档（允许边界宽限）；
                //   * 时点声明（Start == End）：心跳/退出钩子只给了一个"我正要下线"的时刻，
                //     没有终止信息，因此只要求该时刻**落在空档范围内**。
                // 关键约束：两种形态都**不得**把一次声明当成"此后永久离线"——
                // 实测有一次 exit 声明 7 秒后设备就恢复出数，若按永久处理会掩盖之后所有真实断档。
                bool declared = trace.Declarations != null && trace.Declarations.Any(d =>
                {
                    if (!string.Equals(d.DeviceId, trace.DeviceId, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (d.EndTime > d.StartTime)
                    {
                        // 区间声明：必须盖住整个空档
                        return d.StartTime <= gapStart.AddMinutes(declarationGraceMinutes) &&
                               d.EndTime >= gapEnd.AddMinutes(-declarationGraceMinutes);
                    }

                    // 时点声明：时刻落在空档内（含边界宽限）
                    return d.StartTime >= gapStart.AddMinutes(-declarationGraceMinutes) &&
                           d.StartTime <= gapEnd.AddMinutes(declarationGraceMinutes);
                });

                if (!declared)
                {
                    totalViolations++;
                    bool isNew = gapEnd >= cutoff;
                    if (isNew) newViolations++; else historicalViolations++;

                    earliest = earliest == null || gapStart < earliest ? gapStart : earliest;
                    latest = latest == null || gapEnd > latest ? gapEnd : latest;

                    if (samples.Count < opt.MaxSampleCount)
                    {
                        samples.Add($"Device={trace.DeviceId}: [{gapStart:yyyy-MM-dd HH:mm:ss} ~ {gapEnd:yyyy-MM-dd HH:mm:ss}] 存在 {gapMinutes:F1}m 无声明空档 (> {gapThresholdMinutes:F1}m)");
                        violations.Add(new InvariantViolation(
                            Id: $"{trace.DeviceId}:undeclared-gap:{i}",
                            DeviceId: trace.DeviceId,
                            OccurredAtUtc: ToUtc(gapStart),
                            Fields: Fields(
                                ("kind", "undeclared-gap"),
                                ("gapStartUtc", ToUtc(gapStart).ToString("O")),
                                ("gapEndUtc", ToUtc(gapEnd).ToString("O")),
                                ("gapMinutes", gapMinutes.ToString("F1")),
                                ("isNew", isNew ? "true" : "false"))));
                    }
                }
            }

            if (interval.EndTime > cursor)
            {
                cursor = interval.EndTime;
            }
        }

        // 2. 检查上传滞后 p99。系统合成的 gap 事件必须排除：它们的 timestamp 是断档起点、
        //    created_at 是重启后补传时刻，两者之差恒等于断档时长，不代表上传链路延迟。
        var realSamples = trace.UploadLagSamples?.Where(s => !s.IsSyntheticGap).ToList()
            ?? new List<UploadLagSample>();
        if (realSamples.Count > 0)
        {
            var lags = realSamples
                .Select(s => Math.Max(0, (s.CreatedAt - s.EventTime).TotalMinutes))
                .OrderBy(v => v)
                .ToList();

            int p99Index = (int)Math.Ceiling(lags.Count * 0.99) - 1;
            p99Index = Math.Clamp(p99Index, 0, lags.Count - 1);
            double p99Lag = lags[p99Index];

            if (p99Lag > p99LagMinutesThreshold)
            {
                var worst = realSamples
                    .OrderByDescending(s => (s.CreatedAt - s.EventTime).TotalMinutes)
                    .First();
                bool isNew = worst.CreatedAt >= cutoff;
                if (isNew) newViolations++; else historicalViolations++;

                totalViolations++;
                earliest = earliest == null || worst.EventTime < earliest ? worst.EventTime : earliest;
                latest = latest == null || worst.CreatedAt > latest ? worst.CreatedAt : latest;

                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"Device={trace.DeviceId}: 上传滞后 p99={p99Lag:F1}m 超过阈值 {p99LagMinutesThreshold:F1}m");
                    violations.Add(new InvariantViolation(
                        Id: $"{trace.DeviceId}:upload-lag-p99",
                        DeviceId: trace.DeviceId,
                        OccurredAtUtc: ToUtc(worst.EventTime),
                        Fields: Fields(
                            ("kind", "upload-lag-p99"),
                            ("p99LagMinutes", p99Lag.ToString("F1")),
                            ("worstLagMinutes", Math.Max(0, (worst.CreatedAt - worst.EventTime).TotalMinutes).ToString("F1")),
                            ("isNew", isNew ? "true" : "false"))));
                }
            }
        }

        if (totalViolations > 0)
        {
            bool isWarning = newViolations == 0 && historicalViolations > 0;
            return InvariantResult.Failure(
                $"INV-P20 {(isWarning ? "WARN" : "FAIL")}: 检测到 {totalViolations} 处无声明空档或上传滞后超标 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                earliest,
                latest,
                note,
                fallback,
                isWarning: isWarning,
                violations: violations);
        }

        return InvariantResult.Success("INV-P20 PASS: 设备无声明空档与上传延迟均在指标内", note, fallback);
    }


    /// <summary>
    /// S7 (INV-P21): 断档必须在时间轴上被标记
    /// 判据: 相邻事件之间 &gt; 15 分钟的空洞，必须被"缺数据"类事件（gap 或等价标记）完整覆盖。
    /// 阈值: 未标记空洞 = 0，断档判定阈值 15.0 分钟。
    /// 为什么是这个阈值: 超过 15m 的无数据空洞若在 UI 上直接拼接或无解释空白，用户无法分辨是设备没用还是系统漏记；必须显示 gap 标记。
    ///
    /// 实现口径（#254 S7，本轮修正）：空洞的认定**必须看它是否被 gap 事件覆盖**。
    /// 旧实现只检查"相邻两条区间是否相接"，`TimelineInterval.IsGap` 是取数层查出来却从未被读的
    /// 死字段 —— 一个被 gap 事件完整覆盖的断档也会被判"未标记"。现在先把 gap 区间合并，
    /// 再判别每个空洞是否被合并后的 gap 区间完整覆盖；未覆盖才算违规，并按业务时间做 T4 分档。
    /// </summary>
    public static InvariantResult CheckS7_TimelineGapMarked(
        IEnumerable<TimelineInterval> intervals,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);
        var thresholdMinutes = opt.TimelineGapThresholdMinutes;

        var list = intervals?.OrderBy(i => i.StartTime).ThenBy(i => i.EndTime).ToList() ?? new List<TimelineInterval>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-P21 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);

        int totalViolations = 0;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();
        DateTime? earliest = null;
        DateTime? latest = null;

        // 按设备分别判定：gap 事件只能标记同一台设备的空洞。
        foreach (var deviceGroup in list.GroupBy(i => i.DeviceId, StringComparer.Ordinal))
        {
            var deviceIntervals = deviceGroup
                .OrderBy(i => i.StartTime)
                .ThenBy(i => i.EndTime)
                .ToList();

            // 先把"缺数据"类区间合并成互不重叠的覆盖段（多个 30 分钟 gap 分片拼接成一段完整断档）。
            var coverage = MergeIntervals(
                deviceIntervals.Where(i => i.IsGap).Select(i => (i.StartTime, i.EndTime)));

            var cursor = deviceIntervals[0].EndTime;
            for (int i = 1; i < deviceIntervals.Count; i++)
            {
                var interval = deviceIntervals[i];
                var holeStart = cursor;
                var holeEnd = interval.StartTime;
                var holeMinutes = (holeEnd - holeStart).TotalMinutes;

                if (holeMinutes > thresholdMinutes && !IsFullyCovered(coverage, holeStart, holeEnd))
                {
                    totalViolations++;
                    bool isNew = holeEnd >= cutoff;
                    if (isNew) newViolations++; else historicalViolations++;

                    earliest = earliest == null || holeStart < earliest ? holeStart : earliest;
                    latest = latest == null || holeEnd > latest ? holeEnd : latest;

                    if (samples.Count < opt.MaxSampleCount)
                    {
                        samples.Add($"Device={interval.DeviceId}: [{holeStart:yyyy-MM-dd HH:mm:ss} ~ {holeEnd:yyyy-MM-dd HH:mm:ss}] 存在 {holeMinutes:F1}m 未标记空洞 (> {thresholdMinutes:F1}m)");
                        violations.Add(new InvariantViolation(
                            Id: $"{interval.DeviceId}:unmarked-hole:{i}",
                            DeviceId: interval.DeviceId,
                            OccurredAtUtc: ToUtc(holeStart),
                            Fields: Fields(
                                ("holeStartUtc", ToUtc(holeStart).ToString("O")),
                                ("holeEndUtc", ToUtc(holeEnd).ToString("O")),
                                ("holeMinutes", holeMinutes.ToString("F1")),
                                ("isNew", isNew ? "true" : "false"))));
                    }
                }

                if (interval.EndTime > cursor)
                {
                    cursor = interval.EndTime;
                }
            }
        }

        if (totalViolations > 0)
        {
            bool isWarning = newViolations == 0 && historicalViolations > 0;
            return InvariantResult.Failure(
                $"INV-P21 {(isWarning ? "WARN" : "FAIL")}: 时间轴上存在 {totalViolations} 处未标记的断档空洞 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                earliest,
                latest,
                note,
                fallback,
                isWarning: isWarning,
                violations: violations);
        }

        return InvariantResult.Success("INV-P21 PASS: 所有 >15m 空洞均已妥善标记为 gap 事件", note, fallback);
    }

    /// <summary>把可能重叠/相接的区间合并成互不重叠的升序区间列表。</summary>
    private static List<(DateTime Start, DateTime End)> MergeIntervals(
        IEnumerable<(DateTime Start, DateTime End)> source)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var interval in source.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End)
            {
                merged.Add(interval);
                continue;
            }

            if (interval.End > merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, interval.End);
            }
        }

        return merged;
    }

    /// <summary>
    /// 空洞是否被"缺数据"覆盖段**完整**覆盖（判据原文要求"完整覆盖"，留白即未标记）。
    /// 允许 1 秒的边界容差：gap 分片与相邻事件的边界在毫秒/秒级上可能有取整差。
    /// </summary>
    private static bool IsFullyCovered(
        IReadOnlyList<(DateTime Start, DateTime End)> coverage,
        DateTime holeStart,
        DateTime holeEnd)
    {
        var tolerance = TimeSpan.FromSeconds(1);
        foreach (var (start, end) in coverage)
        {
            if (start <= holeStart.Add(tolerance) && end >= holeEnd.Subtract(tolerance))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// S8 (INV-C19): 日界一致（三层口径统一）
    /// 判据: 同一时刻在三处必须归属同一天：
    ///   1. 数据字段的日期桶 (date)
    ///   2. 按日接口的查询窗口 (date=YYYY-MM-DD)
    ///   3. 页面展示的业务日
    ///   统一标准：Asia/Shanghai 业务日，04:00 起算（[D 04:00, D+1 04:00) 为日 D）。
    /// 阈值: 不一致行数 = 0。
    /// 为什么是这个阈值: 跨夜作息（如凌晨 2 点工作）属于前一天的夜间延伸，统一以 04:00 作为界线；三层不一致会导致列表查出但聚合统计丢失。
    /// </summary>
    public static InvariantResult CheckS8_DayBoundaryConsistent(
        IEnumerable<DayBoundarySample> samples,
        InvariantOptions? options = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);

        var list = samples?.ToList() ?? new List<DayBoundarySample>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-C19 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        int totalViolations = 0;
        var violationSamples = new List<string>();
        var violations = new List<InvariantViolation>();
        bool hasQueryWindow = false;
        bool hasPageDisplay = false;

        foreach (var s in list)
        {
            var expectedBusinessDay = ComputeBusinessDayString(s.EventTimeUtc);
            bool b1 = s.DataFieldDateBucket == expectedBusinessDay;
            bool b2 = string.IsNullOrEmpty(s.QueryWindowDate) || s.QueryWindowDate == expectedBusinessDay;
            bool b3 = string.IsNullOrEmpty(s.PageDisplayDate) || s.PageDisplayDate == expectedBusinessDay;

            if (!string.IsNullOrEmpty(s.QueryWindowDate)) hasQueryWindow = true;
            if (!string.IsNullOrEmpty(s.PageDisplayDate)) hasPageDisplay = true;

            if (!b1 || !b2 || !b3)
            {
                totalViolations++;
                if (violationSamples.Count < opt.MaxSampleCount)
                {
                    violationSamples.Add($"EventUtc={s.EventTimeUtc:yyyy-MM-dd HH:mm:ss}, Expected={expectedBusinessDay} | Field={s.DataFieldDateBucket}({b1}), Query={s.QueryWindowDate ?? "N/A"}({b2}), Page={s.PageDisplayDate ?? "N/A"}({b3})");
                    violations.Add(new InvariantViolation(
                        Id: string.IsNullOrEmpty(s.EventId)
                            ? $"{s.TableName ?? "unknown"}:{ToUtc(s.EventTimeUtc):O}"
                            : s.EventId,
                        DeviceId: s.TableName ?? string.Empty,
                        OccurredAtUtc: ToUtc(s.EventTimeUtc),
                        Fields: Fields(
                            ("table", s.TableName),
                            ("expectedDate", expectedBusinessDay),
                            ("fieldDate", s.DataFieldDateBucket),
                            ("queryWindowDate", s.QueryWindowDate),
                            ("pageDisplayDate", s.PageDisplayDate))));
                }
            }
        }

        bool isAllThreeLayers = hasQueryWindow && hasPageDisplay;
        string coveredLayers = isAllThreeLayers
            ? "DataField,QueryWindow,PageDisplay"
            : (hasQueryWindow ? "DataField,QueryWindow" : "DataField");

        string layerScopeText = isAllThreeLayers
            ? "覆盖层级: 数据字段层 ✅, 接口窗口层 ✅, 展示层 ✅"
            : "覆盖层级: 数据字段层 ✅; 接口窗口层、展示层: 本判据未覆盖 (需接口契约测试)";

        if (totalViolations > 0)
        {
            string failMsg = isAllThreeLayers
                ? $"INV-C19 FAIL: 检测到 {totalViolations} 处日界归属三层不一致 (标准: Asia/Shanghai 04:00 起算) [{layerScopeText}]"
                : $"INV-C19 FAIL: 检测到 {totalViolations} 处日界归属不一致 (标准: Asia/Shanghai 04:00 起算) [{layerScopeText}]";

            return InvariantResult.Failure(
                failMsg,
                totalViolations,
                totalViolations,
                0,
                violationSamples,
                null,
                null,
                note,
                fallback,
                isWarning: false,
                coveredLayers: coveredLayers,
                violations: violations);
        }

        string passMsg = isAllThreeLayers
            ? $"INV-C19 PASS: 数据桶、接口窗口与页面展示三层日界严格一致 [{layerScopeText}]"
            : $"INV-C19 PASS: 数据字段层日界严格一致 (0 行偏离) [{layerScopeText}]";

        return InvariantResult.Success(passMsg, note, fallback, coveredLayers: coveredLayers);
    }

    /// <summary>
    /// 计算时刻对应的 Asia/Shanghai 04:00 起算的业务日字符串 (YYYY-MM-DD)。
    /// </summary>
    public static string ComputeBusinessDayString(DateTime utcTime)
    {
        var shanghaiTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc), ShanghaiTimeZone);
        // 若当前时间小于 04:00，属于前一天
        var businessDate = shanghaiTime.TimeOfDay < TimeSpan.FromHours(4)
            ? shanghaiTime.Date.AddDays(-1)
            : shanghaiTime.Date;

        return businessDate.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// 业务日字符串（YYYY-MM-DD）对应的起点（北京时间当天 04:00）的 UTC 时刻。
    /// 解析失败时返回 <see cref="DateTime.MinValue"/>，调用方不得据此做时间比较。
    /// </summary>
    public static DateTime ResolveBusinessDayStartUtc(string businessDate)
    {
        if (!DateTime.TryParseExact(
                businessDate,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return DateTime.MinValue;
        }

        var shanghaiLocal = DateTime.SpecifyKind(parsed.Date.AddHours(4), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(shanghaiLocal, ShanghaiTimeZone);
    }

    /// <summary>
    /// S9 (INV-C20): 有缺口必有信号
    /// 判据: 覆盖率低于红线时必须报"警告 / 红"，绝不得报"正常"。
    /// 阈值: 覆盖率 = 有效数据时长 ÷ 设备在线时长；&lt; 95% 红、&lt; 99% 黄 (T6)。
    /// 为什么是这个阈值: 95% 覆盖率是个人生活记录可信度的基准底线；低覆盖率若显示"正常"属于静默掩盖故障。
    /// </summary>
    public static InvariantResult CheckS9_GapHasSignal(
        CoverageSignalReport report,
        InvariantOptions? options = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);

        if (report == null || report.OnlineDurationSeconds <= 0)
        {
            return InvariantResult.Unknown("INV-C20 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        if (report.IsDataInsufficientForDenominator)
        {
            double rawRatio = report.OnlineDurationSeconds > 0 ? report.ValidDataDurationSeconds / report.OnlineDurationSeconds : 0.0;
            var gapDetails = report.GapBreakdown != null && report.GapBreakdown.Count > 0
                ? string.Join("; ", report.GapBreakdown)
                : "无细化时段";

            string msg = $"INV-C20 UNKNOWN: 口径近似 / 数据源不足: {report.DenominatorBasisNote ?? "缺失历史心跳序列与离线声明日志，分母无法准确界定设备在线区间"} (若按基准窗口推算覆盖率约为 {(rawRatio * 100.0):F1}%: 分子 {report.ValidDataDurationSeconds:F0}s, 分母 {report.OnlineDurationSeconds:F0}s; 缺口时段: {gapDetails})";
            return InvariantResult.Unknown(msg, note, fallback);
        }

        double ratio = report.ValidDataDurationSeconds / report.OnlineDurationSeconds;

        bool isNormal = report.ReportedStatus.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
                        report.ReportedStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ||
                        report.ReportedStatus.Equals("OK", StringComparison.OrdinalIgnoreCase);

        // 覆盖率 < 95% 必须报红
        if (ratio < opt.CoverageRedRatio)
        {
            if (isNormal)
            {
                return InvariantResult.Failure(
                    $"INV-C20 FAIL: 设备 {report.DeviceId} 覆盖率仅为 {(ratio * 100.0):F1}% (< 红线 {(opt.CoverageRedRatio * 100.0):F0}%)，但报告状态却为 '{report.ReportedStatus}' (必须为红/错误)",
                    1,
                    1,
                    0,
                    new[] { $"Device={report.DeviceId}: Coverage={(ratio * 100.0):F1}%, ReportedStatus={report.ReportedStatus}" },
                    null,
                    null,
                    note,
                    fallback,
                    isWarning: false,
                    violations: new[] { CoverageViolation(report, ratio) });
            }
        }
        else if (ratio < opt.CoverageYellowRatio)
        {
            // 覆盖率在 95%~99% 之间，必须至少报黄/警告，不得报完全正常
            if (isNormal)
            {
                return InvariantResult.Failure(
                    $"INV-C20 WARN: 设备 {report.DeviceId} 覆盖率为 {(ratio * 100.0):F1}% (< 黄线 {(opt.CoverageYellowRatio * 100.0):F0}%)，但报告状态为 '{report.ReportedStatus}' (必须报警告/黄线)",
                    1,
                    0,
                    1,
                    new[] { $"Device={report.DeviceId}: Coverage={(ratio * 100.0):F1}%, ReportedStatus={report.ReportedStatus}" },
                    null,
                    null,
                    note,
                    fallback,
                    isWarning: true,
                    violations: new[] { CoverageViolation(report, ratio) });
            }
        }

        return InvariantResult.Success($"INV-C20 PASS: 覆盖率 {(ratio * 100.0):F1}% 与健康信号 '{report.ReportedStatus}' 自洽", note, fallback);
    }

    /// <summary>S9 结构化违规引用：覆盖率与"被报成正常"的状态（聚合粒度，时间取体检窗口的近似起点）。</summary>
    private static InvariantViolation CoverageViolation(CoverageSignalReport report, double ratio) =>
        new(
            Id: report.DeviceId,
            DeviceId: report.DeviceId,
            OccurredAtUtc: DateTime.MinValue,
            Fields: Fields(
                ("coverage", (ratio * 100.0).ToString("F1")),
                ("reportedStatus", report.ReportedStatus),
                ("validDataSeconds", report.ValidDataDurationSeconds.ToString("F0")),
                ("onlineSeconds", report.OnlineDurationSeconds.ToString("F0"))));

    #endregion

    #region S10–S13: 尺子组三 · 链路健康

    /// <summary>
    /// S10 (INV-C21): 后台任务必须有产出（静默失败专治）
    /// 判据: 分类快照补齐、汇总入库、派生表构建等后台任务，在最近一个执行窗口内产出必须 &gt; 0；产出 0 且存在可处理数据 = 红。
    /// 阈值: 产出 0 且可处理数据 &gt; 0 = 红 (产出 0 违规)。
    /// 为什么是这个阈值: 后台任务常因未捕获异常退出、无限等待或查询条件脱节导致空转，空转产出 0 却报成功属于致命静默失败。
    /// </summary>
    public static InvariantResult CheckS10_TaskHasOutput(
        IEnumerable<BackgroundTaskRun> runs,
        InvariantOptions? options = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);

        var list = runs?.ToList() ?? new List<BackgroundTaskRun>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-C21 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        int totalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();

        foreach (var r in list)
        {
            if (r.AvailableDataCount > 0 && r.OutputCount == 0)
            {
                totalViolations++;
                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"Task={r.TaskName}, ExecutedAt={r.ExecutedAt:yyyy-MM-dd HH:mm:ss}: 可处理数据 {r.AvailableDataCount} 条但产出 0 行 (静默空转)");
                    violations.Add(new InvariantViolation(
                        Id: r.TaskName,
                        DeviceId: string.Empty,
                        OccurredAtUtc: ToUtc(r.ExecutedAt),
                        Fields: Fields(
                            ("availableDataCount", r.AvailableDataCount.ToString()),
                            ("outputCount", r.OutputCount.ToString()))));
                }
            }
        }

        if (totalViolations > 0)
        {
            return InvariantResult.Failure(
                $"INV-C21 FAIL: 检测到 {totalViolations} 个后台任务存在可处理数据但产出为 0 行",
                totalViolations,
                totalViolations,
                0,
                samples,
                null,
                null,
                note,
                fallback,
                violations: violations);
        }

        return InvariantResult.Success("INV-C21 PASS: 后台任务均有真实产出或无待处理数据", note, fallback);
    }

    /// <summary>
    /// S11 (INV-M21): 状态语义自洽
    /// 判据: 批次状态与其内部失败/拒绝计数必须逻辑自洽：
    ///   1. failed_count = 0 的批次不得处于 failed / completed-with-errors 状态
    ///   2. failed_count &gt; 0 的批次不得处于 completed 状态
    ///   3. 处理计数（accepted/failed/rejected/skipped）全为 0 的批次不得处于 completed（虚假完成/空转批次）
    /// 阈值: 违规批次数 = 0。
    /// 新增/存量（T4）: 按窗口起点 window_start_utc 分档——落在最近 RecentWindowHours（默认 24h）内为新增，
    /// 其余为存量；仅有存量违规时降级为黄线警告（存量只计数不报警）。缺省窗口起点一律视为存量。
    /// 为什么是这个阈值: 客户端条目级校验拒绝（如零时长过滤）被误当成整批失败，会导致质量面板误报同步失败并引导用户无意义重试。
    /// </summary>
    public static InvariantResult CheckS11_StatusSemantics(
        IEnumerable<BatchSyncStatusRecord> batches,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);

        var list = batches?.ToList() ?? new List<BatchSyncStatusRecord>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-M21 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);

        int totalViolations = 0;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();

        foreach (var b in list)
        {
            bool isFailedStatus = b.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                                 b.Status.Equals("completed-with-errors", StringComparison.OrdinalIgnoreCase);

            // 1. 无真正失败却标为失败
            if (b.FailedCount == 0 && isFailedStatus)
            {
                totalViolations++;
                if (ToUtc(b.WindowStartUtc) >= cutoff) newViolations++; else historicalViolations++;
                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"Batch={b.BatchId}: FailedCount=0 但状态被标为 '{b.Status}' (应为 completed 或 rejected 语义)");
                    violations.Add(BatchViolation(b, "failed-without-failure"));
                }
            }
            // 2. 有失败却标为已完成
            else if (b.FailedCount > 0 && b.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                totalViolations++;
                if (ToUtc(b.WindowStartUtc) >= cutoff) newViolations++; else historicalViolations++;
                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"Batch={b.BatchId}: FailedCount={b.FailedCount} > 0 但状态被标为 'completed'");
                    violations.Add(BatchViolation(b, "completed-with-failures"));
                }
            }
            // 3. 处理计数全为 0 却标为已完成 (虚假完成 / 空转批次)
            //    注意 skipped 也是"处理过"的条目：只含重复条目的批次是合法完成（#243）。
            else if ((b.TotalCount == 0 || (b.AcceptedCount == 0 && b.FailedCount == 0 && b.RejectedCount == 0 && b.SkippedCount == 0)) && b.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                totalViolations++;
                if (ToUtc(b.WindowStartUtc) >= cutoff) newViolations++; else historicalViolations++;
                if (samples.Count < opt.MaxSampleCount)
                {
                    samples.Add($"Batch={b.BatchId}: 处理计数为 0 (accepted=0, failed=0) 却被标为 'completed' (虚假完成/空转批次)");
                    violations.Add(BatchViolation(b, "empty-run-completed"));
                }
            }
        }

        if (totalViolations > 0)
        {
            bool isWarning = newViolations == 0 && historicalViolations > 0;
            return InvariantResult.Failure(
                $"INV-M21 {(isWarning ? "WARN" : "FAIL")}: 检测到 {totalViolations} 个批次状态语义与计数指标不自洽 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                null,
                null,
                note,
                fallback,
                isWarning: isWarning,
                violations: violations);
        }

        return InvariantResult.Success("INV-M21 PASS: 所有批次状态与其失败/拒绝计数语义一致", note, fallback);
    }

    /// <summary>
    /// S12 (INV-M22): 派生表在正常使用
    /// 判据: 派生表（时间线块、使用聚合）在最近数据上必须非空；若设计为在线计算，则不得保留空表（二选一，不允许含糊）。
    /// 阈值: 最近 24h 有源数据时，派生表行数 &gt; 0（或显式声明在线计算）。
    /// 为什么是这个阈值: 存在死表或未初始化的空派生表会导致查询落入空表返回空白，或开发者以为有预聚合而引发性能雪崩。
    /// </summary>
    public static InvariantResult CheckS12_DerivedTableActive(
        IEnumerable<DerivedTableStatus> tables,
        InvariantOptions? options = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);

        var list = tables?.ToList() ?? new List<DerivedTableStatus>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-M22 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        int totalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();

        foreach (var t in list)
        {
            if (t.SourceDataCountLast24H > 0)
            {
                if (t.DerivedRowCount == 0 && !t.IsExplicitOnlineCalculation)
                {
                    totalViolations++;
                    if (samples.Count < opt.MaxSampleCount)
                    {
                        samples.Add($"Table={t.TableName}: 最近24h存在源数据 {t.SourceDataCountLast24H} 条，派生表行数却为 0 且未声明在线计算");
                        violations.Add(new InvariantViolation(
                            Id: t.TableName,
                            DeviceId: string.Empty,
                            OccurredAtUtc: DateTime.MinValue,
                            Fields: Fields(
                                ("sourceDataCountLast24H", t.SourceDataCountLast24H.ToString()),
                                ("derivedRowCount", t.DerivedRowCount.ToString()),
                                ("isExplicitOnlineCalculation", "false"))));
                    }
                }
            }
        }

        if (totalViolations > 0)
        {
            return InvariantResult.Failure(
                $"INV-M22 FAIL: 检测到 {totalViolations} 个派生表存在源数据但为空且未声明在线计算",
                totalViolations,
                totalViolations,
                0,
                samples,
                null,
                null,
                note,
                fallback,
                violations: violations);
        }

        return InvariantResult.Success("INV-M22 PASS: 派生表正常更新或已明确声明在线计算", note, fallback);
    }

    /// <summary>
    /// S13 (INV-P22): 实例唯一
    /// 判据: 同一 device_id 在任一时刻只应有一条独立采集流（用轮询相位 / 会话序号连续性判定）。
    /// 阈值: 同一小时内出现 &gt;= 2 条**互斥**采集流 = 红。
    /// 为什么是这个阈值: 多实例同时采集同一设备会产生竞态覆盖、双倍计数和会话断裂，破坏时序完整性。
    ///
    /// 实现口径（#254 S13）：
    ///   「互斥」= 两条采集流在时间上真实**重叠并发**。同一小时内先后出现两个 instance_id
    ///   并不构成违规 —— 客户端升级/重启时旧进程退出、新进程立刻接管，正是正常交接
    ///   （实测 09-18 19:17 交接误差仅 0.001 秒、重叠为 0，旧实现把它误报成多实例）。
    ///
    ///   重叠检测**按设备整体进行、不按小时切分**：实例 A 的区间可以跨越整点
    ///   （如 A=[00:59:50, 01:00:10]、B=[01:00:00, 01:00:20]），若先按"事件自身时间戳所在小时"
    ///   分组，A 与 B 会落进两个不同的组而互相看不见，真实并发会被漏掉。
    ///   检测到重叠后，再按重叠发生的时刻归属到对应小时。
    ///
    ///   数据不足时必须如实报告"未知"：若一个设备在窗口内出现多个实例，但**所有**区间时长都为 0
    ///   （数据源没有提供时长），则无从判断它们是否真的同时在采集，此时输出 UNKNOWN —— 绝不亮假绿灯。
    ///
    /// 相位判定作为补充：若心跳未携带 instance_id，则按"互斥轮询相位在同一小时交错"判定。
    /// </summary>
    public static InvariantResult CheckS13_SingleInstance(
        IEnumerable<CollectionHeartbeat> heartbeats,
        InvariantOptions? options = null,
        DateTime? referenceTimeUtc = null)
    {
        var (opt, fallback, note) = InvariantOptions.Resolve(options);

        var list = heartbeats?.ToList() ?? new List<CollectionHeartbeat>();
        if (list.Count == 0)
        {
            return InvariantResult.Unknown("INV-P22 UNKNOWN: 数据源为空或未接线", note, fallback);
        }

        var now = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoff = now.AddHours(-opt.RecentWindowHours);

        // 先按设备分组做全局（跨小时）重叠检测，再按重叠发生的小时归属违规。
        var violationsByHour = new Dictionary<(string DeviceId, DateTime Hour), (string InstanceA, string InstanceB, double OverlapSeconds, DateTime OccurredAt)>();
        var inconclusiveDevices = new List<string>();

        foreach (var deviceGroup in list.GroupBy(h => h.DeviceId, StringComparer.Ordinal))
        {
            var deviceHeartbeats = deviceGroup.ToList();
            var conflicted = FindConcurrentInstanceOverlaps(deviceHeartbeats, opt.InstanceOverlapToleranceSeconds);

            if (conflicted.Count == 0 && HasInstancesWithoutDuration(deviceHeartbeats))
            {
                // 多实例但完全没有时长信息：无法证明"并发"也无法证伪，如实报未知。
                inconclusiveDevices.Add(deviceGroup.Key);
                continue;
            }

            foreach (var conflict in conflicted)
            {
                var hour = FloorToHour(conflict.OccurredAt);
                var key = (deviceGroup.Key, hour);
                if (!violationsByHour.ContainsKey(key))
                {
                    violationsByHour[key] = conflict;
                }
            }

            // 无 instance_id 时的补充判定：互斥轮询相位在同一小时交错。
            foreach (var hourGroup in deviceHeartbeats
                .Where(h => string.IsNullOrEmpty(h.InstanceId))
                .GroupBy(h => FloorToHour(h.Timestamp)))
            {
                var group = hourGroup.ToList();
                if (group.Count < 6)
                {
                    continue;
                }

                var phases = group.Select(h => Math.Round(h.PhaseOffsetSeconds, 1)).Distinct().ToList();
                if (phases.Count >= 2)
                {
                    var key = (deviceGroup.Key, hourGroup.Key);
                    violationsByHour[key] = (
                        string.Join(", ", phases) + "s 相位交错",
                        "phase-conflict",
                        group.Count,
                        hourGroup.Key);
                }
            }
        }

        int totalViolations = violationsByHour.Count;
        int newViolations = 0;
        int historicalViolations = 0;
        var samples = new List<string>();
        var violations = new List<InvariantViolation>();
        DateTime? earliest = null;
        DateTime? latest = null;

        foreach (var pair in violationsByHour.OrderBy(kv => kv.Key.Hour))
        {
            var deviceId = pair.Key.DeviceId;
            var hour = pair.Key.Hour;
            var info = pair.Value;

            bool isNew = info.OccurredAt >= cutoff;
            if (isNew) newViolations++; else historicalViolations++;

            earliest = earliest == null || info.OccurredAt < earliest ? info.OccurredAt : earliest;
            latest = latest == null || info.OccurredAt > latest ? info.OccurredAt : latest;

            if (samples.Count < opt.MaxSampleCount)
            {
                samples.Add(info.InstanceB == "phase-conflict"
                    ? $"Device={deviceId}, Hour={hour:yyyy-MM-dd HH:00}: 检测到 {info.InstanceA}"
                    : $"Device={deviceId}, Hour={hour:yyyy-MM-dd HH:00}: 实例 {info.InstanceA} 与 {info.InstanceB} 并发重叠 {info.OverlapSeconds:F3}s");

                violations.Add(new InvariantViolation(
                    Id: $"{deviceId}:{hour:yyyy-MM-ddTHH}:00Z",
                    DeviceId: deviceId,
                    OccurredAtUtc: info.OccurredAt,
                    Fields: info.InstanceB == "phase-conflict"
                        ? Fields(
                            ("kind", "phase-conflict"),
                            ("hourUtc", hour.ToString("O")),
                            ("phases", info.InstanceA),
                            ("isNew", isNew ? "true" : "false"))
                        : Fields(
                            ("kind", "instance-concurrency"),
                            ("hourUtc", hour.ToString("O")),
                            ("instanceA", info.InstanceA),
                            ("instanceB", info.InstanceB),
                            ("overlapSeconds", info.OverlapSeconds.ToString("F3")),
                            ("isNew", isNew ? "true" : "false"))));
            }
        }

        if (totalViolations > 0)
        {
            bool isWarning = newViolations == 0 && historicalViolations > 0;
            return InvariantResult.Failure(
                $"INV-P22 {(isWarning ? "WARN" : "FAIL")}: 检测到 {totalViolations} 处同一设备多实例并发采集冲突 (新增 {newViolations}, 存量 {historicalViolations})",
                totalViolations,
                newViolations,
                historicalViolations,
                samples,
                earliest,
                latest,
                note,
                fallback,
                isWarning: isWarning,
                violations: violations);
        }

        if (inconclusiveDevices.Count > 0)
        {
            return InvariantResult.Unknown(
                $"INV-P22 UNKNOWN: 设备 {string.Join(", ", inconclusiveDevices)} 出现多个采集实例但区间时长缺失，无法判定是否真的并发采集",
                note,
                fallback);
        }

        return InvariantResult.Success("INV-P22 PASS: 每台设备均保持唯一样本采集实例流", note, fallback);
    }

    private static DateTime FloorToHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);

    /// <summary>该设备是否"出现多个实例、但所有区间时长都为 0"（无从判断并发与否）。</summary>
    private static bool HasInstancesWithoutDuration(IEnumerable<CollectionHeartbeat> heartbeats)
    {
        var withInstance = heartbeats.Where(h => !string.IsNullOrEmpty(h.InstanceId)).ToList();
        if (withInstance.Select(h => h.InstanceId).Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return false;
        }

        return withInstance.All(h => h.DurationSeconds <= 0);
    }

    /// <summary>
    /// 找出该设备**所有**真实并发重叠（跨小时，不按小时切分）。两条不同 instance_id 的采集区间
    /// 重叠超过容差才算并发；先后交接（旧实例结束、新实例开始）不算。
    ///
    /// 区间由 <see cref="CollectionHeartbeat.Timestamp"/> + <see cref="CollectionHeartbeat.DurationSeconds"/>
    /// 给出。按起点排序后只需知道"其它实例在当前区间起点之前的最大结束时刻"，
    /// 维护每个实例的最大结束时刻（实例数极少），因此整体为 O(n log n + n × 实例数)。
    /// </summary>
    private static List<(string InstanceA, string InstanceB, double OverlapSeconds, DateTime OccurredAt)> FindConcurrentInstanceOverlaps(
        IEnumerable<CollectionHeartbeat> heartbeats,
        double toleranceSeconds)
    {
        var results = new List<(string, string, double, DateTime)>();
        var toleranceTicks = toleranceSeconds * TimeSpan.TicksPerSecond;

        var intervals = heartbeats
            .Where(h => !string.IsNullOrEmpty(h.InstanceId))
            .Select(h => (
                InstanceId: h.InstanceId!,
                Start: h.Timestamp,
                End: h.Timestamp.AddSeconds(Math.Max(0, h.DurationSeconds))))
            .OrderBy(i => i.Start)
            .ThenBy(i => i.End)
            .ToList();

        if (intervals.Count == 0)
        {
            return results;
        }

        var maxEndByInstance = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var current in intervals)
        {
            foreach (var (instanceId, otherEnd) in maxEndByInstance)
            {
                if (string.Equals(instanceId, current.InstanceId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (otherEnd.Ticks - current.Start.Ticks > toleranceTicks)
                {
                    double overlap = (otherEnd - current.Start).TotalSeconds;
                    results.Add((instanceId, current.InstanceId, overlap, current.Start));
                }
            }

            if (!maxEndByInstance.TryGetValue(current.InstanceId, out var knownEnd) || current.End > knownEnd)
            {
                maxEndByInstance[current.InstanceId] = current.End;
            }
        }

        return results;
    }

    #endregion
}
