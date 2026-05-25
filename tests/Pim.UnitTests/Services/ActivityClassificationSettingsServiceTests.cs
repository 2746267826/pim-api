using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassificationSettingsServiceTests
{
    [Fact]
    public async Task GetSettingsAsync_ReturnsDefaultFiveMinuteSettingWithoutPersisting()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSettingsService(db);

        var settings = await service.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(5, settings.RecommendedMinimumClassificationDurationMinutes);
        Assert.Equal([1, 3, 5, 10, 15], settings.SupportedRecommendedMinimumDurations);
        Assert.Equal(0, await db.Set<ActivityClassificationSettingsEntity>().CountAsync());
    }

    [Fact]
    public async Task SaveSettingsAsync_ClampsToSupportedPreset()
    {
        using var db = CreateDb();
        var service = new ActivityClassificationSettingsService(db);

        var settings = await service.SaveSettingsAsync(7, CancellationToken.None);

        Assert.Equal(5, settings.RecommendedMinimumClassificationDurationMinutes);
        Assert.Equal([1, 3, 5, 10, 15], settings.SupportedRecommendedMinimumDurations);

        var persisted = await db.Set<ActivityClassificationSettingsEntity>().SingleAsync();
        Assert.Equal("default", persisted.SettingsKey);
        Assert.Equal(5, persisted.RecommendedMinimumClassificationDurationMinutes);
    }

    [Fact]
    public async Task SaveSettingsAsync_UpdatesExistingDefaultRow()
    {
        using var db = CreateDb();
        var existing = new ActivityClassificationSettingsEntity
        {
            Id = Guid.NewGuid(),
            SettingsKey = "default",
            RecommendedMinimumClassificationDurationMinutes = 3,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.Set<ActivityClassificationSettingsEntity>().Add(existing);
        await db.SaveChangesAsync();
        var service = new ActivityClassificationSettingsService(db);

        var settings = await service.SaveSettingsAsync(10, CancellationToken.None);

        Assert.Equal(10, settings.RecommendedMinimumClassificationDurationMinutes);
        var persisted = await db.Set<ActivityClassificationSettingsEntity>().SingleAsync();
        Assert.Equal(existing.Id, persisted.Id);
        Assert.Equal(10, persisted.RecommendedMinimumClassificationDurationMinutes);
        Assert.True(persisted.UpdatedAt > existing.CreatedAt);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationSettingsEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new PimDbContext(options);
    }
}
