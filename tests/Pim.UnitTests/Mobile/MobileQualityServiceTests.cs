using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileQualityServiceTests
{
    [Fact]
    public async Task GetQualityAsync_ReturnsStableComponentKeys()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileQualityService(db, MobileTestHelpers.CurrentUser());

        var quality = await service.GetQualityAsync(null, null, CancellationToken.None);

        var keys = quality.Components.Select(component => component.Key).ToHashSet();
        Assert.Contains("android-heartbeat", keys);
        Assert.Contains("event-coverage", keys);
        Assert.Contains("fallback-only-days", keys);
        Assert.Contains("sync-batch-failures", keys);
        Assert.Contains("location-accuracy-rejections", keys);
        Assert.Contains("app-metadata-completeness", keys);
    }
}
