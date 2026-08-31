using Pim.Client.Core.Models;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

// Legacy AwBucketSelection removed per Native Tracker design (pim-native-tracker).
// This file now validates TrackerConfig defaults and Native Tracker event types.
public class AwBucketSelectionTests
{
    [Fact]
    public void TrackerConfig_Defaults_MatchSpec()
    {
        var cfg = new TrackerConfig();
        Assert.True(cfg.Enabled);
        Assert.Equal(10, cfg.PollIntervalSeconds);
        Assert.Equal(300, cfg.IdleThresholdSeconds);
        Assert.Equal(60, cfg.GapThresholdSeconds);
        Assert.Equal(15601, cfg.BrowserBridgePort);
        Assert.Equal(500, cfg.UploadBatchSize);
        Assert.Equal(30, cfg.UploadIntervalSeconds);
        Assert.Equal(300, cfg.HealthReportIntervalSeconds);
        Assert.Equal(30, cfg.LogRetentionDays);
    }

    [Fact]
    public void TrackerConfig_AllowsCustomExcludedApps()
    {
        var cfg = new TrackerConfig { ExcludedApps = new List<string> { "game.exe" } };
        Assert.Contains("game.exe", cfg.ExcludedApps);
    }

    [Fact]
    public void BrowserHeartbeat_ParsesDomain()
    {
        var hb = new BrowserHeartbeat { Url = "https://github.com/ActivityWatch/aw-watcher-web", Title = "test" };
        Assert.Equal("github.com", hb.Domain);
    }
}
