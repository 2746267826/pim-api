using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class KeyStatsLocalStatsClientTests
{
    [Fact]
    public void CountersIndicateRecovery_True_WhenGrew()
    {
        var before = new KeyStatsCounterSnapshot(0, 0, 0, 0, 0, 0, 0, 0);
        var after = before with { KeyPresses = 3 };
        Assert.True(KeyStatsLocalStatsClient.CountersIndicateRecovery(before, after));
    }

    [Fact]
    public void CountersIndicateRecovery_False_WhenStillZero()
    {
        var before = new KeyStatsCounterSnapshot(0, 0, 0, 0, 0, 0, 0, 0);
        var after = new KeyStatsCounterSnapshot(0, 0, 0, 0, 0, 0, 0, 0);
        Assert.False(KeyStatsLocalStatsClient.CountersIndicateRecovery(before, after));
    }

    [Fact]
    public void CountersIndicateRecovery_True_WhenHasAnyActivityEvenWithoutPrevious()
    {
        var after = new KeyStatsCounterSnapshot(5, 0, 0, 0, 0, 0, 0, 0);
        Assert.True(KeyStatsLocalStatsClient.CountersIndicateRecovery(null, after));
    }

    [Fact]
    public void ResolveBaseUrl_DefaultsToLocalhost18080()
    {
        var url = KeyStatsLocalStatsClient.ResolveBaseUrl();
        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.StartsWith("http", url);
    }
}
