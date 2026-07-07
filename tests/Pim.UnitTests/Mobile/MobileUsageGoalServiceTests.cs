using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileUsageGoalServiceTests
{
    [Fact]
    public async Task SaveAsync_StoresUserGlobalDailyGoalAndListAsyncReturnsIt()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var saved = await service.SaveAsync(new MobileUsageGoalUpsertRequest(
            "total-daily",
            null,
            null,
            "每日手机总时长",
            14400,
            true), CancellationToken.None);
        var goals = await service.ListAsync(CancellationToken.None);

        var goal = Assert.Single(goals);
        Assert.Equal(saved.Id, goal.Id);
        Assert.Equal("total-daily", goal.Scope);
        Assert.Equal("每日手机总时长", goal.Label);
        Assert.Equal(14400, goal.LimitSeconds);
        Assert.True(goal.IsEnabled);
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingScopePackageAndCategory()
    {
        var now = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageGoalService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var first = await service.SaveAsync(new MobileUsageGoalUpsertRequest(
            "category-daily",
            null,
            MobileLifeCategories.ShortVideoEntertainment,
            "少刷短视频",
            1800,
            true), CancellationToken.None);
        var updated = await service.SaveAsync(new MobileUsageGoalUpsertRequest(
            "category-daily",
            null,
            MobileLifeCategories.ShortVideoEntertainment,
            "短视频控制",
            1200,
            false), CancellationToken.None);

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("短视频控制", updated.Label);
        Assert.Equal(1200, updated.LimitSeconds);
        Assert.False(updated.IsEnabled);
        Assert.Single(await service.ListAsync(CancellationToken.None));
    }
}
