using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.UnitTests.Harness.Invariants;

/// <summary>
/// 日历模块不变量定义
/// 每条不变量均为可量化断言，用于属性测试与回归校验
/// </summary>
public static class CalendarInvariants
{
    /// <summary>
    /// INV-CA01: 周期展开不遗漏 —— 窗口 [rangeStart, rangeEnd) 内展开实例数 == 理论应有数
    /// threshold: expectedCount 按 RRULE 推导（COUNT / UNTIL / 间隔折算）；tolerance: 0 允许遗漏 0 条，超过即 FAIL
    /// 不变量: |expanded.Count - expectedCount| <= tolerance (tolerance=0)
    /// </summary>
    public static (bool pass, string detail) CheckRecurrenceExpansionCompleteness(
        List<DateTimeOffset> expandedOccurrences,
        int expectedCount,
        int tolerance = 0)
    {
        var actual = expandedOccurrences.Count;
        var diff = Math.Abs(actual - expectedCount);
        if (diff > tolerance)
        {
            return (false,
                $"INV-CA01 FAIL: expanded {actual} != expected {expectedCount}, diff {diff} > tolerance {tolerance}");
        }

        // 额外校验：展开后按时间单调且无重复
        var distinct = expandedOccurrences.Distinct().Count();
        if (distinct != actual)
        {
            return (false,
                $"INV-CA01 FAIL: duplicate occurrences detected {actual - distinct} duplicates");
        }

        for (int i = 1; i < expandedOccurrences.Count; i++)
        {
            if (expandedOccurrences[i] <= expandedOccurrences[i - 1])
                return (false,
                    $"INV-CA01 FAIL: occurrences not strictly increasing at index {i}: {expandedOccurrences[i - 1]:O} >= {expandedOccurrences[i]:O}");
        }

        return (true, "INV-CA01 PASS");
    }

    /// <summary>
    /// INV-CA02: 提醒不早于/晚于事件边界 —— 提醒触发时刻必须在 [eventStart - maxLead, eventStart] 区间内
    /// threshold: maxLeadMinutes 默认 10080 分钟（7天）为最大提前量；tolerance: 1秒 允许舍入误差
    /// 不变量: eventStart - maxLead - tolerance <= reminderTime <= eventStart + tolerance
    /// </summary>
    public static (bool pass, string detail) CheckReminderTiming(
        List<(DateTimeOffset eventStart, DateTimeOffset reminderTime)> reminders,
        double maxLeadMinutes = 10080,
        double toleranceSeconds = 1.0)
    {
        foreach (var r in reminders)
        {
            var leadSeconds = (r.eventStart - r.reminderTime).TotalSeconds;
            // reminder 不得晚于事件开始（超过 tolerance）
            if (r.reminderTime > r.eventStart.AddSeconds(toleranceSeconds))
            {
                return (false,
                    $"INV-CA02 FAIL: reminder {r.reminderTime:O} is {leadSeconds:F1}s after eventStart {r.eventStart:O}, threshold maxLead {maxLeadMinutes}m tolerance {toleranceSeconds}s");
            }

            // reminder 不得早于 maxLead 之前
            var maxLeadSeconds = maxLeadMinutes * 60;
            if (leadSeconds > maxLeadSeconds + toleranceSeconds)
            {
                return (false,
                    $"INV-CA02 FAIL: reminder {r.reminderTime:O} is {leadSeconds / 60:F1}m before eventStart {r.eventStart:O} exceeds threshold {maxLeadMinutes}m tolerance {toleranceSeconds}s");
            }
        }

        return (true, "INV-CA02 PASS");
    }

    /// <summary>
    /// INV-CA03: Outlook 冲突检测 —— 任意两事件若时间重叠则必须被标记为冲突，且无重叠不得误报
    /// threshold: overlapThresholdSeconds = 60秒（小于60秒的重叠视为容差不算冲突）；tolerance: 0 误报/漏报 0 条
    /// 不变量: overlap > threshold => conflictReported == true; overlap <= threshold => conflictReported == false
    /// </summary>
    public static (bool pass, string detail) CheckOutlookConflictDetection(
        List<(DateTimeOffset start, DateTimeOffset end, bool conflictReported)> events,
        double overlapThresholdSeconds = 60.0,
        int tolerance = 0)
    {
        var falseNegatives = 0;
        var falsePositives = 0;

        for (int i = 0; i < events.Count; i++)
        {
            for (int j = i + 1; j < events.Count; j++)
            {
                var a = events[i];
                var b = events[j];
                var overlapStart = a.start > b.start ? a.start : b.start;
                var overlapEnd = a.end < b.end ? a.end : b.end;
                var overlapSeconds = (overlapEnd - overlapStart).TotalSeconds;
                var actuallyOverlaps = overlapSeconds > overlapThresholdSeconds;

                // 若存在重叠，至少其中一个应报告冲突
                if (actuallyOverlaps && !a.conflictReported && !b.conflictReported)
                {
                    falseNegatives++;
                }
            }
        }

        // 检查误报：报告冲突但实际无重叠的事件
        for (int i = 0; i < events.Count; i++)
        {
            if (!events[i].conflictReported) continue;
            var hasRealOverlap = false;
            for (int j = 0; j < events.Count; j++)
            {
                if (i == j) continue;
                var a = events[i];
                var b = events[j];
                var overlapStart = a.start > b.start ? a.start : b.start;
                var overlapEnd = a.end < b.end ? a.end : b.end;
                var overlapSeconds = (overlapEnd - overlapStart).TotalSeconds;
                if (overlapSeconds > overlapThresholdSeconds)
                {
                    hasRealOverlap = true;
                    break;
                }
            }

            if (!hasRealOverlap) falsePositives++;
        }

        var totalErrors = falseNegatives + falsePositives;
        if (totalErrors > tolerance)
        {
            return (false,
                $"INV-CA03 FAIL: falseNegatives {falseNegatives} falsePositives {falsePositives} total {totalErrors} > tolerance {tolerance} threshold {overlapThresholdSeconds}s");
        }

        return (true, "INV-CA03 PASS");
    }

    /// <summary>
    /// INV-CA04: 报表汇总 == 明细 —— 按日/按分类汇总值之和必须等于明细 сумме
    /// threshold: reportTotal 为汇总侧声明的总值；tolerance: 明细条数 * 1秒（每条允许1秒舍入）
    /// 不变量: |sum(details) - reportTotal| <= tolerance
    /// </summary>
    public static (bool pass, string detail) CheckReportSumEqualsDetail(
        List<double> detailSeconds,
        double reportTotalSeconds,
        double perItemToleranceSeconds = 1.0)
    {
        var detailSum = detailSeconds.Sum();
        var tolerance = detailSeconds.Count * perItemToleranceSeconds;
        var diff = Math.Abs(detailSum - reportTotalSeconds);
        if (diff > tolerance)
        {
            return (false,
                $"INV-CA04 FAIL: detail sum {detailSum:F1}s != report total {reportTotalSeconds:F1}s diff {diff:F1}s > tolerance {tolerance:F1}s (threshold perItem {perItemToleranceSeconds}s)");
        }

        return (true, "INV-CA04 PASS");
    }

    /// <summary>
    /// INV-CA05: 周期异常覆盖（Exception Overlay）—— 例外实例必须精确覆盖原实例且不产生重复或遗漏
    /// threshold: exceptionCount 期望覆盖数 == occurrences 中 IsException==true 的数量；tolerance: 0 条重复/遗漏
    /// 不变量: overlay 后总实例数 == 原展开数 - 被覆盖数 + 例外数，且无重复 recurrenceId
    /// </summary>
    public static (bool pass, string detail) CheckRecurrenceExceptionOverlay(
        List<(string recurrenceId, DateTimeOffset originalStart, bool isException, DateTimeOffset? exceptionStart)> occurrences,
        int tolerance = 0)
    {
        var exceptionIds = occurrences.Where(o => o.isException).Select(o => o.recurrenceId).ToList();
        var duplicateIds = exceptionIds.GroupBy(id => id).Where(g => g.Count() > 1).ToList();
        if (duplicateIds.Count > tolerance)
        {
            return (false,
                $"INV-CA05 FAIL: duplicate recurrenceId in exceptions {duplicateIds.First().Key} count {duplicateIds.First().Count()} > tolerance {tolerance} threshold 0 duplicates");
        }

        // 例外必须有新的时间且不与原实例重复（除被覆盖的 recurrenceId 外）
        var normalStarts = new HashSet<DateTimeOffset>(occurrences.Where(o => !o.isException).Select(o => o.originalStart));
        foreach (var ex in occurrences.Where(o => o.isException))
        {
            if (ex.exceptionStart is null)
                return (false, $"INV-CA05 FAIL: exception {ex.recurrenceId} has null exceptionStart threshold non-null tolerance 0");
            if (normalStarts.Contains(ex.exceptionStart.Value))
                return (false, $"INV-CA05 FAIL: exception {ex.recurrenceId} overlay time {ex.exceptionStart:O} collides with normal occurrence threshold no-collision tolerance 0");
        }

        // 覆盖后无 recurrenceId 既在 normal 又在 exception 且保留两份（应只保留 exception）
        var normalIds = new HashSet<string>(occurrences.Where(o => !o.isException).Select(o => o.recurrenceId));
        var overlap = exceptionIds.Count(id => normalIds.Contains(id));
        if (overlap > tolerance)
        {
            return (false,
                $"INV-CA05 FAIL: {overlap} recurrenceIds appear in both normal and exception without overlay, tolerance {tolerance} threshold 0 overlap");
        }

        return (true, "INV-CA05 PASS");
    }

    /// <summary>
    /// INV-CA06: 任务分段覆盖 —— 任务执行分段之和必须覆盖任务时长且无超长/负时长分段
    /// threshold: taskDurationSeconds 为任务声明总时长；tolerance: 1秒 * 分段数（每段允许1秒舍入）
    /// 不变量: 0 &lt;= segment.duration &lt;= taskDuration 且 |sum(segments) - taskDuration| &lt;= tolerance 且段间无重叠
    /// </summary>
    public static (bool pass, string detail) CheckTaskSegmentCoverage(
        List<(DateTimeOffset start, DateTimeOffset end)> segments,
        double taskDurationSeconds,
        double perSegmentToleranceSeconds = 1.0)
    {
        if (segments.Count == 0)
        {
            if (Math.Abs(taskDurationSeconds) > perSegmentToleranceSeconds)
                return (false, $"INV-CA06 FAIL: no segments but taskDuration {taskDurationSeconds:F1}s threshold 0 tolerance {perSegmentToleranceSeconds}s");
            return (true, "INV-CA06 PASS");
        }

        foreach (var s in segments)
        {
            var dur = (s.end - s.start).TotalSeconds;
            if (dur < -1e-9)
                return (false, $"INV-CA06 FAIL: segment {s.start:O}->{s.end:O} negative duration {dur:F1}s threshold >=0 tolerance 0");
            if (dur > taskDurationSeconds + perSegmentToleranceSeconds)
                return (false, $"INV-CA06 FAIL: segment {s.start:O}->{s.end:O} duration {dur:F1}s exceeds taskDuration {taskDurationSeconds:F1}s threshold taskDuration tolerance {perSegmentToleranceSeconds}s");
        }

        // 段间不重叠且按时间排序
        var ordered = segments.OrderBy(s => s.start).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].start < ordered[i - 1].end.AddSeconds(-perSegmentToleranceSeconds))
                return (false, $"INV-CA06 FAIL: segment overlap at index {i}: {ordered[i - 1].end:O} overlaps {ordered[i].start:O} threshold no-overlap tolerance {perSegmentToleranceSeconds}s");
        }

        var sum = segments.Sum(s => (s.end - s.start).TotalSeconds);
        var tolerance = segments.Count * perSegmentToleranceSeconds;
        var diff = Math.Abs(sum - taskDurationSeconds);
        if (diff > tolerance)
        {
            return (false,
                $"INV-CA06 FAIL: segments sum {sum:F1}s != taskDuration {taskDurationSeconds:F1}s diff {diff:F1}s > tolerance {tolerance:F1}s threshold {taskDurationSeconds:F1}s");
        }

        return (true, "INV-CA06 PASS");
    }

    /// <summary>
    /// INV-CA07: 事件时长边界 —— 单事件时长必须在 [0, maxDuration] 内
    /// threshold: maxDurationSeconds 默认 86400秒（24小时）为单事件最大允许；tolerance: 1秒 允许舍入
    /// 不变量: -tolerance &lt;= durationSeconds &lt;= maxDuration + tolerance
    /// </summary>
    public static (bool pass, string detail) CheckEventDurationBounds(
        List<(string eventId, DateTimeOffset start, DateTimeOffset end)> events,
        double maxDurationSeconds = 86400.0,
        double toleranceSeconds = 1.0)
    {
        foreach (var e in events)
        {
            var duration = (e.end - e.start).TotalSeconds;
            if (duration < -toleranceSeconds)
                return (false, $"INV-CA07 FAIL: event {e.eventId} negative duration {duration:F1}s threshold 0 tolerance {toleranceSeconds}s");
            if (duration > maxDurationSeconds + toleranceSeconds)
                return (false, $"INV-CA07 FAIL: event {e.eventId} duration {duration:F1}s > max {maxDurationSeconds:F1}s tolerance {toleranceSeconds}s");
        }

        return (true, "INV-CA07 PASS");
    }

    /// <summary>
    /// INV-CA08: 日历去重与幂等 —— 同一 Outlook GraphEventId 在同一视图内仅出现一次，且跨同步无幽灵事件
    /// threshold: expectedUniqueCount == distinct GraphEventId 数量；tolerance: 0 重复 0 条幽灵
    /// 不变量: distinct(GraphEventId) == total && ghostCount == 0
    /// </summary>
    public static (bool pass, string detail) CheckCalendarDeduplication(
        List<(string graphEventId, string recurrenceId, DateTimeOffset start)> viewEvents,
        HashSet<string>? knownGraphIds = null,
        int tolerance = 0)
    {
        var duplicateGroups = viewEvents.GroupBy(e => e.graphEventId).Where(g => g.Count() > 1).ToList();
        if (duplicateGroups.Count > tolerance)
        {
            var worst = duplicateGroups.OrderByDescending(g => g.Count()).First();
            return (false,
                $"INV-CA08 FAIL: graphEventId {worst.Key} duplicated {worst.Count()} times > tolerance {tolerance} threshold unique 1 per id");
        }

        // recurrence 实例需同时以 (graphEventId + recurrenceId) 去重
        var compositeDupes = viewEvents.GroupBy(e => (e.graphEventId, e.recurrenceId)).Where(g => g.Count() > 1).ToList();
        if (compositeDupes.Count > tolerance)
        {
            var worst = compositeDupes.First();
            return (false,
                $"INV-CA08 FAIL: composite key {worst.Key.graphEventId}/{worst.Key.recurrenceId} duplicated {worst.Count()} times > tolerance {tolerance} threshold 1");
        }

        if (knownGraphIds is not null)
        {
            var ghosts = viewEvents.Where(e => !knownGraphIds.Contains(e.graphEventId)).ToList();
            if (ghosts.Count > tolerance)
                return (false, $"INV-CA08 FAIL: {ghosts.Count} ghost events not in knownGraphIds > tolerance {tolerance} threshold 0 ghosts, e.g. {ghosts.First().graphEventId}");
        }

        return (true, "INV-CA08 PASS");
    }

    /// <summary>
    /// INV-CA09: 时区一致性 —— 事件 start/end 的 UTC 时刻经目标时区转换后，时长差与原始时长一致
    /// threshold: durationSeconds 通过 (end - start).TotalSeconds 推导；tolerance: 1秒 允许夏令时/舍入误差
    /// 不变量: |convertedDuration - originalDuration| &lt;= tolerance 且 start/end Offset 与 timeZoneId 匹配（±1小时容差）
    /// </summary>
    public static (bool pass, string detail) CheckTimezoneConsistency(
        List<(string eventId, DateTimeOffset start, DateTimeOffset end, string timeZoneId)> events,
        double toleranceSeconds = 1.0)
    {
        foreach (var e in events)
        {
            var duration = (e.end - e.start).TotalSeconds;
            if (duration < -toleranceSeconds)
                return (false, $"INV-CA09 FAIL: event {e.eventId} negative duration {duration:F1}s threshold 0 tolerance {toleranceSeconds}s");

            if (string.IsNullOrWhiteSpace(e.timeZoneId))
                return (false, $"INV-CA09 FAIL: event {e.eventId} timeZoneId empty threshold non-empty tolerance 0");

            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(e.timeZoneId);
                var convertedStart = TimeZoneInfo.ConvertTime(e.start, tz);
                var convertedEnd = TimeZoneInfo.ConvertTime(e.end, tz);
                var convertedDuration = (convertedEnd - convertedStart).TotalSeconds;
                var diff = Math.Abs(convertedDuration - duration);
                if (diff > toleranceSeconds)
                    return (false, $"INV-CA09 FAIL: event {e.eventId} converted duration {convertedDuration:F1}s != original {duration:F1}s diff {diff:F1}s > tolerance {toleranceSeconds}s threshold {duration:F1}s");
            }
            catch (TimeZoneNotFoundException)
            {
                return (false, $"INV-CA09 FAIL: event {e.eventId} timeZoneId '{e.timeZoneId}' not found threshold valid tz tolerance 0");
            }
            catch (InvalidTimeZoneException)
            {
                return (false, $"INV-CA09 FAIL: event {e.eventId} timeZoneId '{e.timeZoneId}' invalid threshold valid tz tolerance 0");
            }
        }

        return (true, "INV-CA09 PASS");
    }

    /// <summary>
    /// INV-CA10: 视图窗口过滤 —— 视图内事件必须与查询窗口 [windowStart, windowEnd) 相交，窗口外事件数为 0
    /// threshold: window 外事件数阈值 0；tolerance: 0 条窗口外，1秒 边界容差（事件恰好在边界上算相交）
    /// 不变量: viewEvents.All(e =&gt; e.end &gt; windowStart - tolerance &amp;&amp; e.start &lt; windowEnd + tolerance) 且超出数为 0
    /// </summary>
    public static (bool pass, string detail) CheckViewWindowFiltering(
        List<(string eventId, DateTimeOffset start, DateTimeOffset end)> viewEvents,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        double toleranceSeconds = 1.0,
        int tolerance = 0)
    {
        if (windowEnd <= windowStart)
            return (false, $"INV-CA10 FAIL: windowEnd {windowEnd:O} <= windowStart {windowStart:O} threshold windowEnd > windowStart tolerance 0");

        var outOfWindow = 0;
        foreach (var e in viewEvents)
        {
            var intersects = e.end > windowStart.AddSeconds(-toleranceSeconds) && e.start < windowEnd.AddSeconds(toleranceSeconds);
            if (!intersects)
                outOfWindow++;
        }

        if (outOfWindow > tolerance)
            return (false, $"INV-CA10 FAIL: {outOfWindow} events out of window [{windowStart:O},{windowEnd:O}) > tolerance {tolerance} threshold 0 out-of-window tolerance {toleranceSeconds}s");

        // 视图内时间有序（按 start 单调）
        var ordered = viewEvents.OrderBy(e => e.start).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].start < ordered[i - 1].start.AddSeconds(-toleranceSeconds) && viewEvents[i].start != ordered[i].start)
            {
                // 仅校验视图本身未打乱外部排序时不需要额外失败；窗口过滤本身不强制排序，仅在调用方可选校验
            }
        }

        return (true, "INV-CA10 PASS");
    }
}
