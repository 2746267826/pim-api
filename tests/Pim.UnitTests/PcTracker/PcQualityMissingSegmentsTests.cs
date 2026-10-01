using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20261001 · REQ-4（#373）与 REQ-5（#377）：缺数文案必须与字段语义一致，
/// 且时段要有机器可读的结构化字段。
/// <para>
/// 缺陷：<c>details.disconnectedFromUtc</c> 取的是**最早一段**断档的起点，文案却说「最近一次中断自 …」；
/// 起止时刻只存在于 issue 的本地化 <c>message</c> 里，<c>details</c> 只有小时字符串数组，
/// 消费方只能正则解析文案。
/// </para>
/// </summary>
public sealed class PcQualityMissingSegmentsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime QueryDate = new(2026, 9, 27);

    /// <summary>
    /// 构造两段断档：本地 07:00–13:00 的内部空洞 + 17:00 到业务日结束的尾部断档。
    /// </summary>
    private static async Task<(PimDbContext Db, PcQualityResponse Result, DateTimeOffset DayStart)> ArrangeTwoGapsAsync()
    {
        var db = CreateDb();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(QueryDate);
        AddTrackerEvent(db, dayStart.AddHours(2), 3600);                     // 本地 06:00–07:00
        for (var hour = 9; hour <= 12; hour++)                               // 本地 13:00–17:00
            AddTrackerEvent(db, dayStart.AddHours(hour), 3600);
        AddHeartbeat(db, Now.AddMinutes(-1));
        await db.SaveChangesAsync();

        var result = await Service(db).GetQualityAsync(QueryDate, null, null, CancellationToken.None);
        return (db, result, dayStart);
    }

    /// <summary>AC-5.1 / AC-5.4：<c>details.missingSegments</c> 给出与实际断档一一对应的起止时刻。</summary>
    [Fact]
    public async Task MissingSegments_ListEveryGapWithUtcBounds()
    {
        var (db, result, dayStart) = await ArrangeTwoGapsAsync();
        await using var _ = db;

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        var segments = Assert.IsType<List<PcQualityMissingSegmentDto>>(tracker.Details["missingSegments"]);

        // 内部空洞：本地 07:00–13:00 → UTC 前一日 23:00Z 起 6 小时；尾部：本地 17:00 → 次日 04:00。
        Assert.Equal(2, segments.Count);
        Assert.Equal(dayStart.AddHours(3).ToString("O"), segments[0].StartUtc);
        Assert.Equal(dayStart.AddHours(9).ToString("O"), segments[0].EndUtc);
        Assert.Equal(dayStart.AddHours(13).ToString("O"), segments[1].StartUtc);
        Assert.Equal(dayStart.AddDays(1).ToString("O"), segments[1].EndUtc);
        // 时刻必须是 UTC（ISO 8601 +00:00），消费方无需再猜时区。
        Assert.All(segments, segment =>
        {
            Assert.EndsWith("+00:00", segment.StartUtc);
            Assert.EndsWith("+00:00", segment.EndUtc);
        });
    }

    /// <summary>AC-5.3：既有键名与语义不变（本单只做加法）。</summary>
    [Fact]
    public async Task MissingSegments_KeepTheExistingHourFields()
    {
        var (db, result, _) = await ArrangeTwoGapsAsync();
        await using var __ = db;

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        // 缺数**小时**数（6 小时内部空洞 + 11 小时尾部空白）与**段**数（2）是两个不同的量，
        // 既有字段语义不变：missingHourCount 仍按小时计。
        Assert.Equal("17", Assert.IsType<string>(tracker.Details["missingHourCount"]));
        var missingHours = Assert.IsType<string>(tracker.Details["missingHours"]);
        Assert.Contains("2026-09-27 07:00", missingHours);
        Assert.Equal(
            PcTrackerService.GetBusinessDayStartForQuery(QueryDate).AddHours(3).ToString("O"),
            Assert.IsType<string>(tracker.Details["disconnectedFromUtc"]));
    }

    /// <summary>
    /// AC-4.1 / AC-4.2：文案必须区分「最早一段」与「最近一段」——<c>disconnectedFromUtc</c> 是最早一段的起点，
    /// 文案不得再说「最近一次中断自 …」；多段时必须给出「共 N 段」与最早一段的起止。
    /// </summary>
    [Fact]
    public async Task IssueMessage_DescribesTheEarliestGapAndTheGapCount()
    {
        var (db, result, dayStart) = await ArrangeTwoGapsAsync();
        await using var _ = db;

        var issue = Assert.Single(result.Issues, i => i.Code == "tracker-events-missing-hours");

        Assert.Contains("2 段", issue.Message);
        Assert.Contains("最早一段", issue.Message);
        Assert.DoesNotContain("最近一次中断", issue.Message);
        // 最早一段（本地 07:00–13:00）的起止都要出现在文案里。
        Assert.Contains("2026-09-27 07:00", issue.Message);
        Assert.Contains("2026-09-27 13:00", issue.Message);
        // 文案必须把「最早一段」与字段语义绑在一起，否则消费方仍要猜文案说的是哪一段。
        Assert.Contains("disconnectedFromUtc 即该段起点", issue.Message);
    }

    /// <summary>
    /// AC-4.3 / AC-5.2：文案中的时段与 <c>missingSegments</c> 一一对应，不存在「文案里有、字段里没有」的时段。
    /// </summary>
    [Fact]
    public async Task IssueMessageAndMissingSegments_DescribeTheSameGaps()
    {
        var (db, result, _) = await ArrangeTwoGapsAsync();
        await using var __ = db;

        var tracker = Assert.Single(result.Components, c => c.Key == "tracker-events");
        var segments = Assert.IsType<List<PcQualityMissingSegmentDto>>(tracker.Details["missingSegments"]);
        var issue = Assert.Single(result.Issues, i => i.Code == "tracker-events-missing-hours");

        Assert.Equal(segments.Count, CountOccurrences(issue.Message, "–"));
        foreach (var segment in segments)
        {
            Assert.Contains(FormatLocal(segment.StartUtc), issue.Message);
            Assert.Contains(FormatLocal(segment.EndUtc), issue.Message);
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string FormatLocal(string utc)
        => DateTimeOffset.Parse(utc)
            .ToOffset(TimeSpan.FromHours(8))
            .ToString("yyyy-MM-dd HH:mm");

    private static void AddTrackerEvent(PimDbContext db, DateTimeOffset timestamp, double durationSeconds)
        => db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "device-1",
            Timestamp = timestamp,
            Duration = durationSeconds,
            EventType = "window",
            AppName = "Code.exe",
            RawJson = "{}",
            Date = timestamp.UtcDateTime.Date
        });

    private static void AddHeartbeat(PimDbContext db, DateTimeOffset receivedAt)
        => db.DaemonHeartbeats.Add(new DaemonHeartbeatEntity
        {
            DeviceId = "DESKTOP",
            DaemonKind = "windows",
            Version = "1.0.0",
            ServerUrl = "http://127.0.0.1:5858",
            LastSuccessfulUploadAt = receivedAt,
            LastAttemptedUploadAt = receivedAt,
            UploadQueueCount = 0,
            ActivityWatchState = DaemonSourceState.Available.ToString(),
            KeyStatsState = DaemonSourceState.Available.ToString(),
            StatusJson = "{}",
            ReceivedAt = receivedAt
        });

    private static PcTrackerQualityService Service(PimDbContext db)
        => new(db, new FixedTimeProvider(Now));

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
