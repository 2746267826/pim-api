using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcActivityAnalysisServiceTests
{
    [Fact]
    public async Task GetDailyAnalysisAsync_GroupsRecordsAndFlagsPendingClassification()
    {
        await using var db = CreateDb();
        db.Set<AwEventEntity>().AddRange(
            WindowEvent("2026-07-05T08:00:00Z", 600, "Code.exe", "Program.cs"),
            WindowEvent("2026-07-05T08:20:00Z", 300, "Mystery.exe", "Unknown"));
        await db.SaveChangesAsync();
        var tracker = new PcTrackerService(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());
        var service = new PcActivityAnalysisService(tracker);

        var result = await service.GetDailyAnalysisAsync(new DateTime(2026, 7, 5), 60, CancellationToken.None);

        var block = Assert.Single(result.Blocks.Where(item => item.ActiveDurationSeconds > 0));
        Assert.Equal("2026-07-05", result.Date);
        Assert.Equal(60, result.BlockMinutes);
        Assert.Equal(900, block.ActiveDurationSeconds);
        Assert.True(block.IntensityScore > 0);
        Assert.True(block.PendingClassificationCount > 0);
        Assert.True(block.ContextSwitchCount > 0);
        Assert.True(block.CategoryChangeCount >= 0);
        Assert.True(block.Apps.Count >= 1);
        Assert.True(block.Categories.Count >= 1);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(241)]
    public async Task GetDailyAnalysisAsync_RejectsUnsupportedBlockSize(int blockMinutes)
    {
        await using var db = CreateDb();
        var tracker = new PcTrackerService(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());
        var service = new PcActivityAnalysisService(tracker);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetDailyAnalysisAsync(new DateTime(2026, 7, 5), blockMinutes, CancellationToken.None));
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static AwEventEntity WindowEvent(string timestamp, double duration, string appName, string title) =>
        new()
        {
            Id = Random.Shared.NextInt64(1, long.MaxValue),
            DeviceId = "device-1",
            Timestamp = DateTimeOffset.Parse(timestamp),
            Duration = duration,
            EventType = "window",
            AppName = appName,
            AppNameNormalized = AppNameNormalizer.Normalize(appName),
            WindowTitle = title,
            DataJson = "{}"
        };
}
