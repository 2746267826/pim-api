using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class KeyStatsCollectorUploadGateTests
{
    [Fact]
    public void ShouldUpload_IsFalse_ForStaleZero()
    {
        var health = KeyStatsHealthProbe.Evaluate(
            new[] { new KeyStatsProcessInfo(1, 1, true) },
            1,
            new KeyStatsCounterSnapshot(0, 0, 0, 0, 0, 0, 0, 0),
            new KeyStatsCounterSnapshot(0, 0, 0, 0, 0, 0, 0, 0),
            null);

        Assert.False(KeyStatsCollectorService.ShouldUpload(health));
    }

    [Fact]
    public void ShouldUpload_IsTrue_ForAvailable()
    {
        var health = KeyStatsHealthProbe.Evaluate(
            new[] { new KeyStatsProcessInfo(1, 1, true) },
            1,
            new KeyStatsCounterSnapshot(9, 1, 0, 0, 0, 0, 1, 0),
            null,
            null);

        Assert.True(KeyStatsCollectorService.ShouldUpload(health));
    }
}
