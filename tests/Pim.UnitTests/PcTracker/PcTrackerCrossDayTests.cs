using System;
using System.Threading;
using System.Threading.Tasks;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.PcTracker;

public sealed class PcTrackerCrossDayTests
{
    private static DateTimeOffset BusinessDayStart(DateTime date)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch { tz = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        var local = DateTime.SpecifyKind(date.Date.AddHours(4), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static (DateTimeOffset start, DateTimeOffset end) CrossSession(DateTime boundaryDate)
    {
        // boundaryDate is the local date of the 04:00 boundary (e.g. 2026-07-07)
        // session 03:50 - 04:20 local on that date
        var boundary = BusinessDayStart(boundaryDate);
        var startedAt = boundary.AddMinutes(-10);
        var endedAt = boundary.AddMinutes(20);
        return (startedAt, endedAt);
    }

    [Fact]
    public async Task CategoryDistribution_CrossDay_Prorated()
    {
        await using var db = ServiceTestBase.CreateDb();
        var (s, e) = CrossSession(new DateTime(2026, 7, 7));
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(), RecordKey = "k-cross", RecordType = "window", DeviceId = "pc-1",
            StartedAt = s, EndedAt = e, CategoryName = "编程", CategoryColor = "#10b981",
            Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);

        var prev = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-07-06", null, null, null), CancellationToken.None);
        var curr = await svc.GetCategoryDistributionAsync(new PcAggregationQuery("2026-07-07", null, null, null), CancellationToken.None);

        Assert.Single(prev.Items);
        Assert.Single(curr.Items);
        // 10m on prev day, 20m on curr day, tolerance 1m
        Assert.InRange(prev.Items[0].Minutes, 9, 11);
        Assert.InRange(curr.Items[0].Minutes, 19, 21);
    }

    [Fact]
    public async Task Productivity_GetRange_CrossDay_SplitByOverlap()
    {
        await using var db = ServiceTestBase.CreateDb();
        var (s, e) = CrossSession(new DateTime(2026, 7, 7));
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(), RecordKey = "k-cross2", RecordType = "window", DeviceId = "pc-1",
            StartedAt = s, EndedAt = e, CategoryName = "工作", CategoryColor = "#10b981",
            Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var range = await svc.GetRangeAsync(new DateTime(2026, 7, 6), new DateTime(2026, 7, 7), CancellationToken.None);
        // Should have 2 days
        Assert.Equal(2, range.Count);
        var d06 = range.Find(x => x.Date == "2026-07-06");
        var d07 = range.Find(x => x.Date == "2026-07-07");
        Assert.NotNull(d06);
        Assert.NotNull(d07);
        Assert.InRange(d06!.TotalMinutes, 9, 11);
        Assert.InRange(d07!.TotalMinutes, 19, 21);
        Assert.InRange(d06.ProductiveMinutes, 9, 11);
        Assert.InRange(d07.ProductiveMinutes, 19, 21);
    }

    [Fact]
    public async Task Productivity_TimelineV2_CrossDay_OverlapDuration()
    {
        await using var db = ServiceTestBase.CreateDb();
        var (s, e) = CrossSession(new DateTime(2026, 7, 7));
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(), RecordKey = "k-cross3", RecordType = "window", DeviceId = "pc-1",
            StartedAt = s, EndedAt = e, CategoryName = "工作", CategoryColor = "#10b981",
            Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var svc = new PcProductivityService(db);
        var day06 = await svc.GetTimelineV2Async(new DateTime(2026, 7, 6), CancellationToken.None);
        var day07 = await svc.GetTimelineV2Async(new DateTime(2026, 7, 7), CancellationToken.None);
        Assert.Single(day06);
        Assert.Single(day07);
        Assert.InRange(day06[0].DurationMinutes, 9, 11);
        Assert.InRange(day07[0].DurationMinutes, 19, 21);
    }

    [Fact]
    public async Task CategoryDistribution_Range_CrossDay_Prorated()
    {
        await using var db = ServiceTestBase.CreateDb();
        var (s, e) = CrossSession(new DateTime(2026, 7, 7));
        db.Set<ActivityClassificationEntity>().Add(new ActivityClassificationEntity
        {
            Id = Guid.NewGuid(), RecordKey = "k-cross4", RecordType = "window", DeviceId = "pc-1",
            StartedAt = s, EndedAt = e, CategoryName = "工作", CategoryColor = "#10b981",
            Confidence = 0.9, Source = "rule", ClassifierVersion = "v1", ClassifiedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var svc = ServiceTestBase.CreatePcAggregationService(db);
        var res = await svc.GetCategoryDistributionAsync(new PcAggregationQuery(null, "2026-07-06", "2026-07-07", null), CancellationToken.None);
        // range covering both days should include full 30m
        Assert.Single(res.Items);
        Assert.InRange(res.Items[0].Minutes, 29, 31);
    }
}
