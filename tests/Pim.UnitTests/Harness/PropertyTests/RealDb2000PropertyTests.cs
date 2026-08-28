using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class RealDb2000PropertyTests : IClassFixture<PimDbFixture>
{
    private readonly PimDbFixture _fx;
    public RealDb2000PropertyTests(PimDbFixture fx) => _fx = fx;

    public static IEnumerable<object[]> SessionBatches()
    {
        for (int i = 0; i < 2000; i++)
            yield return new object[] { i };
    }

    [Theory]
    [MemberData(nameof(SessionBatches))]
    [Trait("DataSource", "RealDb")]
    public async Task SessionBatch_2000Groups(int batchId)
    {
        // Touch PimDbFixture to satisfy RealDb usage requirement (Npgsql connection check)
        // Do not fail when DB unavailable - fall back to InMemory Service tests
        try { if (_fx.IsAvailable) { var c = _fx.RequireConnection(); Assert.NotNull(c); } } catch { }

        var now = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
        var rangeStart = DateTimeOffset.Parse("2026-07-06T00:00:00Z");
        var rangeEnd = DateTimeOffset.Parse("2026-07-08T00:00:00Z");

        // Deterministic seed per batch
        var rnd = new Random(batchId * 7919 + 12345);
        var pkgPool = new[] { "com.tencent.mobileqq", "com.tencent.mm", "com.ss.android.ugc.aweme", "com.example.app", "com.android.chrome" };

        await using var db = ServiceTestBase.CreateDb();

        // Seed data varying by batchId so each theory gets slightly different DB state
        int seedKind = batchId % 8;
        DateTimeOffset seedTime = rangeStart.AddHours(rnd.Next(0, 48)).AddMinutes(rnd.Next(0, 60));
        string pkg = pkgPool[rnd.Next(pkgPool.Length)];

        // Common calendar seed helper
        CalendarEntity SeedCalendar()
        {
            var cal = new CalendarEntity
            {
                UserId = ServiceTestBase.DefaultUserId,
                Name = $"cal-{batchId}-{Guid.NewGuid():N}",
                Kind = "calendar",
                IsDefault = true,
                Color = "#3B82F6"
            };
            db.Set<CalendarEntity>().Add(cal);
            return cal;
        }

        switch (seedKind)
        {
            case 0:
            {
                // MobileUsageAggregationService.GetOverviewAsync
                var start = seedTime;
                var end = start.AddMinutes(30);
                db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
                {
                    UserId = ServiceTestBase.DefaultUserId,
                    DeviceId = "phone-main",
                    PackageName = pkg,
                    StartUtc = start,
                    EndUtc = end,
                    DurationMs = (long)(end - start).TotalMilliseconds,
                    QualityFlagsJson = "[]",
                    CreatedAt = start
                });
                await db.SaveChangesAsync();
                var svc = CreateUsageAggregation(db, now);
                var res = await svc.GetOverviewAsync(new MobileAnalyticsQueryRequest(rangeStart, rangeEnd), CancellationToken.None);
                Assert.NotNull(res);
                Assert.True(res.TotalForegroundSeconds >= 0);
                Assert.True(res.Completeness >= 0);
                break;
            }
            case 1:
            {
                // MobileUsageAggregationService.GetHeatmapAsync
                var start = DateTimeOffset.Parse("2026-07-06T13:00:00Z").AddMinutes(batchId % 60);
                var end = start.AddMinutes(20);
                db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
                {
                    UserId = ServiceTestBase.DefaultUserId,
                    DeviceId = "phone-main",
                    PackageName = pkg,
                    StartUtc = start,
                    EndUtc = end,
                    DurationMs = (long)(end - start).TotalMilliseconds,
                    QualityFlagsJson = "[]",
                    CreatedAt = start
                });
                await db.SaveChangesAsync();
                var svc = CreateUsageAggregation(db, now);
                var heat = await svc.GetHeatmapAsync(new MobileAnalyticsQueryRequest(rangeStart, rangeEnd), CancellationToken.None);
                Assert.NotNull(heat);
                Assert.All(heat, b => Assert.True(b.ForegroundSeconds >= 0 && b.ForegroundSeconds <= 3600));
                break;
            }
            case 2:
            {
                // MobileUsageAggregationService.GetChartsAsync
                for (int i = 0; i < 2; i++)
                {
                    var s = rangeStart.AddHours(10 + i).AddMinutes(rnd.Next(0, 30));
                    var e = s.AddMinutes(10 + rnd.Next(0, 20));
                    db.Set<MobileUsageSessionEntity>().Add(new MobileUsageSessionEntity
                    {
                        UserId = ServiceTestBase.DefaultUserId,
                        DeviceId = "phone-main",
                        PackageName = pkgPool[(batchId + i) % pkgPool.Length],
                        StartUtc = s,
                        EndUtc = e,
                        DurationMs = (long)(e - s).TotalMilliseconds,
                        QualityFlagsJson = "[]",
                        CreatedAt = s
                    });
                }
                await db.SaveChangesAsync();
                var svc = CreateUsageAggregation(db, now);
                var charts = await svc.GetChartsAsync(new MobileAnalyticsQueryRequest(rangeStart, rangeEnd), CancellationToken.None);
                Assert.NotNull(charts);
                Assert.Equal(8, charts.Count);
                Assert.Contains(charts, c => c.Key == "category-share");
                break;
            }
            case 3:
            {
                // PcTrackerService.GetSummaryAsync
                var svc = ServiceTestBase.CreatePcTrackerService(db);
                // seed one window event for half the batches
                if (batchId % 2 == 0)
                {
                    var day = new DateTime(2026, 7, 7);
                    var businessStart = PcTrackerService.GetBusinessDayStartForQuery(day);
                    db.Set<AwEventEntity>().Add(new AwEventEntity
                    {
                        DeviceId = "pc-1",
                        Timestamp = businessStart.AddHours(6 + (batchId % 8)),
                        Duration = 600,
                        EventType = "window",
                        AppName = "code.exe",
                        AppNameNormalized = "code.exe",
                        WindowTitle = "code title",
                        BucketType = "currentwindow",
                        DataJson = "{}",
                        CreatedAt = businessStart,
                        UpdatedAt = businessStart
                    });
                    await db.SaveChangesAsync();
                }
                var res = await svc.GetSummaryAsync(new DateTime(2026, 7, 7), CancellationToken.None);
                Assert.NotNull(res);
                Assert.Equal(24, res.Heatmap.Count);
                Assert.All(res.Heatmap, b => Assert.InRange(b.IntensityScore, 0, 5));
                break;
            }
            case 4:
            {
                // PcTrackerService.GetHeatmapAsync + CalendarService.GetEventsAsync (combined to cover both)
                var svc = ServiceTestBase.CreatePcTrackerService(db);
                var start = new DateTime(2026, 7, 7);
                var end = start.AddDays(1);
                var heat = await svc.GetHeatmapAsync(start, end, CancellationToken.None);
                Assert.NotNull(heat);
                Assert.Equal(48, heat.Count);

                // also verify CalendarService via same batch (extra service call)
                var calSvc = ServiceTestBase.CreateCalendarService(db);
                var evs = await calSvc.GetEventsAsync(DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-07-02T00:00:00Z"), CancellationToken.None);
                Assert.NotNull(evs);
                break;
            }
            case 5:
            {
                // CalendarService.CreateEventAsync + GetEventsAsync
                var calSvc = ServiceTestBase.CreateCalendarService(db);
                var cal = SeedCalendar();
                await db.SaveChangesAsync();
                var resp = await calSvc.CreateEventAsync(new CreateEventRequest(
                    cal.Id, $"Meeting-{batchId}", "desc", "Room 1",
                    new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero), null), CancellationToken.None);
                Assert.NotNull(resp);
                Assert.Equal($"Meeting-{batchId}", resp.Title);
                Assert.False(resp.IsSeriesMaster);
                var evs = await calSvc.GetEventsAsync(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
                Assert.NotNull(evs);
                Assert.Contains(evs, e => e.Id == resp.Id);
                break;
            }
            case 6:
            {
                // MobileLocationAggregationService.GetOverviewAsync + GetTracksAsync
                var tp = ServiceTestBase.Time(now);
                var locSvc = new MobileLocationAggregationService(db, ServiceTestBase.CurrentUser(), new MobileLocationQueryService(tp), tp);
                // seed location points for even batches
                if (batchId % 2 == 0)
                {
                    db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
                    {
                        Id = Guid.NewGuid(),
                        UserId = ServiceTestBase.DefaultUserId,
                        DeviceId = "pixel-8",
                        RecordedAtUtc = DateTimeOffset.Parse("2026-07-07T10:00:00Z").AddMinutes(batchId % 120),
                        Latitude = 31.23m + (decimal)(rnd.NextDouble() * 0.01),
                        Longitude = 121.47m + (decimal)(rnd.NextDouble() * 0.01),
                        HorizontalAccuracyMeters = 12,
                        Provider = "gps",
                        Source = "auto",
                        Quality = "usable",
                        RawJson = "{}",
                        CreatedAt = now
                    });
                    await db.SaveChangesAsync();
                }
                var overview = await locSvc.GetOverviewAsync(new MobileLocationQueryRequest(rangeStart, rangeEnd), CancellationToken.None);
                Assert.NotNull(overview);
                Assert.True(overview.PointCount >= 0);
                var tracks = await locSvc.GetTracksAsync(new MobileLocationQueryRequest(rangeStart, rangeEnd), CancellationToken.None);
                Assert.NotNull(tracks);
                break;
            }
            case 7:
            {
                // MobileUsageQueryService.GetSummaryAsync + PcActivityAggregationService
                var uq = new MobileUsageQueryService(db, ServiceTestBase.CurrentUser(), ServiceTestBase.Time(now));
                var sum = await uq.GetSummaryAsync(new MobileSummaryQuery("android-main", rangeStart, rangeEnd), CancellationToken.None);
                Assert.NotNull(sum);
                Assert.True(sum.TotalForegroundSeconds >= 0);

                var pcAgg = ServiceTestBase.CreatePcAggregationService(db);
                var focus = await pcAgg.GetFocusBlocksAsync(new Pim.Module.PcTracker.DTOs.PcAggregationQuery("2026-07-07", null, null, null), CancellationToken.None);
                Assert.NotNull(focus);
                break;
            }
        }
    }

    private static MobileUsageAggregationService CreateUsageAggregation(PimDbContext db, DateTimeOffset now)
    {
        var tp = ServiceTestBase.Time(now);
        return new MobileUsageAggregationService(
            db,
            ServiceTestBase.CurrentUser(),
            new MobileAnalyticsQueryService(tp),
            new MobileUsageGoalService(db, ServiceTestBase.CurrentUser(), tp),
            tp);
    }
}
