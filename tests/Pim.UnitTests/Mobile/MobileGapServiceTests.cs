using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileGapServiceTests
{
    [Fact]
    public async Task GetGapsAsync_ClampsRequestedRangeToMostRecentFourteenDays()
    {
        var now = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(now));

        var response = await service.GetGapsAsync(new MobileGapRequest(
            "android-main",
            now.AddDays(-45),
            now,
            "{\"usageEvents\":true}"), CancellationToken.None);

        var expectedStart = now.AddDays(-14);
        Assert.Equal(expectedStart, response.MaxBackfillStartUtc);
        Assert.NotEmpty(response.Windows);
        Assert.All(response.Windows, window => Assert.True(window.WindowStartUtc >= expectedStart));
        Assert.Equal(expectedStart, response.Windows.Min(window => window.WindowStartUtc));
    }
}
