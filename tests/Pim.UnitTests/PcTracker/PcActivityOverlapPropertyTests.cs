using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-PC-BACKEND-20260930 · AC-2.4 属性测试：重叠 / 跨日 / 空档混合的生成流下，
/// 「逐块并集 ≤ 块时长」恒成立，且 apps / categories 合计不超过块时长。
/// 复用仓库既有的确定性种子风格（每个 seed 一个可复现场景）。
/// </summary>
public sealed class PcActivityOverlapPropertyTests
{
    private static readonly DateTime Day = new(2026, 7, 5);
    private static readonly string[] ActivityTypes = ["window", "web-page", "input-minute"];
    private static readonly string[] InactiveTypes = ["gap", "idle", "afk"];
    private static readonly string[] Apps = ["Code.exe", "chrome.exe", "explorer.exe", "teams.exe"];
    private static readonly string[] Categories = ["开发", "浏览", "沟通", "其他"];

    [Fact]
    public async Task GeneratedStreams_NeverExceedBlockOrDayLength()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            await using var db = CreateDb();
            var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
            SeedRandomStream(db, dayStart, seed);
            await db.SaveChangesAsync();

            foreach (var blockMinutes in new[] { 15, 60, 240 })
            {
                var analysis = await new PcActivityAnalysisService(Tracker(db))
                    .GetDailyAnalysisAsync(Day, blockMinutes, CancellationToken.None);

                var blockSeconds = blockMinutes * 60.0;
                foreach (var block in analysis.Blocks)
                {
                    Assert.True(
                        block.ActiveDurationSeconds <= blockSeconds + 0.001,
                        $"seed={seed} blockMinutes={blockMinutes} 块 {block.Start} 活跃 {block.ActiveDurationSeconds}s 超过块时长 {blockSeconds}s");
                    Assert.All(block.Apps, app => Assert.True(
                        app.DurationSeconds <= blockSeconds + 0.001,
                        $"seed={seed} 应用 {app.AppName} 时长超过块时长"));
                    Assert.All(block.Categories, category => Assert.True(
                        category.DurationSeconds <= blockSeconds + 0.001,
                        $"seed={seed} 分类 {category.CategoryName} 时长超过块时长"));
                    Assert.True(block.Apps.Sum(a => a.DurationSeconds) <= blockSeconds + 0.001);
                    Assert.True(block.Categories.Sum(c => c.DurationSeconds) <= blockSeconds + 0.001);
                }

                Assert.True(
                    analysis.Blocks.Sum(b => b.ActiveDurationSeconds) <= 86400 + 0.001,
                    $"seed={seed} blockMinutes={blockMinutes} 单日合计超过 24 小时");
            }
        }
    }

    [Fact]
    public void ResolverSegments_AreAlwaysInsideTheWindowAndDisjoint()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var random = new Random(seed);
            var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
            var records = BuildRecords(random, dayStart, count: 200);

            for (var hour = 0; hour < 24; hour++)
            {
                var windowStart = dayStart.AddHours(hour);
                var windowEnd = windowStart.AddHours(1);
                var segments = PcActivityActiveSegments.Resolve(records, windowStart, windowEnd);
                var total = PcActivityActiveSegments.SumSeconds(segments);

                Assert.True(total <= 3600 + 0.001, $"seed={seed} hour={hour} 并集 {total}s 超过 1 小时");
                for (var i = 0; i < segments.Count; i++)
                {
                    Assert.True(segments[i].Start >= windowStart);
                    Assert.True(segments[i].End <= windowEnd);
                    Assert.True(segments[i].End > segments[i].Start);
                    if (i > 0)
                        Assert.True(segments[i].Start >= segments[i - 1].End, "消解后的段必须互不重叠");
                }
            }
        }
    }

    private static void SeedRandomStream(PimDbContext db, DateTimeOffset dayStart, int seed)
    {
        var random = new Random(seed);
        foreach (var record in BuildRecords(random, dayStart, count: 120))
        {
            if (record.RecordType == "input-minute")
                continue; // input-minute 由 KeyStats 采样派生，这里只种原始事件。

            var timestamp = DateTimeOffset.Parse(record.Start, System.Globalization.CultureInfo.InvariantCulture);
            if (record.RecordType == "web-page")
            {
                db.Set<AwEventEntity>().Add(new AwEventEntity
                {
                    Id = Random.Shared.NextInt64(1, long.MaxValue),
                    DeviceId = "device-1",
                    Timestamp = timestamp,
                    Duration = record.DurationSeconds ?? 0,
                    EventType = "web",
                    AppName = "chrome.exe",
                    AppNameNormalized = "chrome",
                    WindowTitle = record.Domain,
                    DataJson = $"{{\"url\":\"https://{record.Domain}/\",\"title\":\"{record.Domain}\"}}"
                });
                continue;
            }

            if (record.RecordType is "gap" or "idle" or "afk")
            {
                db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
                {
                    DeviceId = "device-1",
                    Timestamp = timestamp,
                    Duration = record.DurationSeconds ?? 0,
                    EventType = record.RecordType,
                    AppName = null,
                    Date = Day,
                    RawJson = "{}"
                });
                continue;
            }

            db.Set<AwEventEntity>().Add(new AwEventEntity
            {
                Id = Random.Shared.NextInt64(1, long.MaxValue),
                DeviceId = "device-1",
                Timestamp = timestamp,
                Duration = record.DurationSeconds ?? 0,
                EventType = "window",
                AppName = record.AppName,
                AppNameNormalized = AppNameNormalizer.Normalize(record.AppName),
                WindowTitle = "W",
                DataJson = "{}"
            });
        }
    }

    /// <summary>生成一段“有重叠、有跨小时/跨业务日边界、有 gap/idle/afk”的记录流。</summary>
    private static List<Pim.Module.PcTracker.DTOs.PcDetailRecord> BuildRecords(
        Random random,
        DateTimeOffset dayStart,
        int count)
    {
        var records = new List<Pim.Module.PcTracker.DTOs.PcDetailRecord>(count);
        for (var i = 0; i < count; i++)
        {
            // 允许起点落在业务日前后各 2 小时，制造跨边界记录。
            var offsetMinutes = random.Next(-120, (int)TimeSpan.FromDays(1).TotalMinutes + 120);
            var start = dayStart.AddMinutes(offsetMinutes);
            var duration = random.Next(1, 5400);
            var isInactive = random.Next(100) < 25;
            var recordType = isInactive
                ? InactiveTypes[random.Next(InactiveTypes.Length)]
                : ActivityTypes[random.Next(ActivityTypes.Length)];
            var app = Apps[random.Next(Apps.Length)];

            records.Add(new Pim.Module.PcTracker.DTOs.PcDetailRecord(
                RecordType: recordType,
                Start: start.ToUniversalTime().ToString("O"),
                End: start.AddSeconds(duration).ToUniversalTime().ToString("O"),
                DurationSeconds: duration,
                DeviceId: "device-1",
                AppName: recordType == "web-page" ? "chrome.exe" : app,
                DisplayName: app,
                CategoryName: Categories[random.Next(Categories.Length)],
                Title: "T",
                KeyPresses: null,
                TotalClicks: null,
                MouseDistance: null,
                ScrollDistance: null,
                KeyCounts: null,
                Raw: null,
                Domain: recordType == "web-page" ? $"site{random.Next(5)}.example.com" : null,
                BrowserAppName: recordType == "web-page" ? "chrome.exe" : null,
                ClassificationConfidence: random.NextDouble(),
                ClassificationSource: "fallback",
                ClassificationExplanation: "property"));
        }

        return records;
    }

    private static PcTrackerService Tracker(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
