using Pim.Client.Core;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using System.Reflection;
using System.Text.Json.Serialization;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ApiClientDefaultsTests
{
    [Fact]
    public void ApiClient_UsesIpv4LoopbackDefaultDaemonServerUrl()
    {
        var client = new ApiClient();

        Assert.Equal("http://127.0.0.1:5858", ClientDefaults.DefaultServerUrl);
        Assert.Equal($"{ClientDefaults.DefaultServerUrl}/api/v1", client.CurrentBaseUrl);
    }

    [Fact]
    public void ApiClient_NormalizesLocalhostServerUrlToIpv4Loopback()
    {
        var client = new ApiClient();

        client.SetBaseUrl("http://localhost:5858");

        Assert.Equal("http://127.0.0.1:5858/api/v1", client.CurrentBaseUrl);
    }

    [Fact]
    public void TrackerSessionManager_AppSwitchedCreatesNewSession()
    {
        var cfg = new TrackerConfig();
        var mgr = new TrackerSessionManager(cfg);
        var now = DateTimeOffset.UtcNow;
        var w1 = new TrackerWindowInfo { AppName = "chrome", ExePath = "C:\\chrome.exe", WindowTitle = "A", CapturedAt = now };
        var w2 = new TrackerWindowInfo { AppName = "code", ExePath = "C:\\code.exe", WindowTitle = "B", CapturedAt = now.AddSeconds(10) };

        mgr.HandleWindowChange(w1, now);
        Assert.Equal("chrome", mgr.Current?.AppName);
        mgr.HandleWindowChange(w2, now.AddSeconds(10));
        Assert.Equal("code", mgr.Current?.AppName);
        Assert.Equal(2, mgr.SessionsCreated);
    }

    [Fact]
    public void TrackerSessionManager_PageVisitDoesNotSplitSession()
    {
        var cfg = new TrackerConfig();
        var mgr = new TrackerSessionManager(cfg);
        var now = DateTimeOffset.UtcNow;
        var w1 = new TrackerWindowInfo { AppName = "chrome", ExePath = "C:\\chrome.exe", WindowTitle = "Tab A", CapturedAt = now };
        var w2 = new TrackerWindowInfo { AppName = "chrome", ExePath = "C:\\chrome.exe", WindowTitle = "Tab B", CapturedAt = now.AddSeconds(5) };

        mgr.HandleWindowChange(w1, now);
        var s1 = mgr.Current;
        mgr.HandleWindowChange(w2, now.AddSeconds(5));
        Assert.Same(s1, mgr.Current);
        Assert.Single(mgr.Current!.PageVisits);
    }

    [Fact]
    public void TrackerSessionManager_IdleWithGraceDeducts()
    {
        var cfg = new TrackerConfig { IdleThresholdSeconds = 300 };
        var mgr = new TrackerSessionManager(cfg);
        var now = DateTimeOffset.UtcNow;
        var w1 = new TrackerWindowInfo { AppName = "chrome", ExePath = "C:\\chrome.exe", WindowTitle = "A", CapturedAt = now };
        mgr.HandleWindowChange(w1, now);
        mgr.HandleIdleStarted(now.AddSeconds(400), TimeSpan.FromSeconds(400));
        Assert.True(mgr.IsIdle);
        Assert.Equal("__IDLE__", mgr.Current?.AppName);
    }

    [Fact]
    public void TrackerSessionManager_GapClosesSession()
    {
        var cfg = new TrackerConfig { GapThresholdSeconds = 60 };
        var mgr = new TrackerSessionManager(cfg);
        var now = DateTimeOffset.UtcNow;
        var w1 = new TrackerWindowInfo { AppName = "chrome", ExePath = "C:\\chrome.exe", WindowTitle = "A", CapturedAt = now };
        mgr.HandleWindowChange(w1, now);
        var closed = mgr.HandleGap(now, now.AddSeconds(120));
        Assert.NotNull(closed);
        Assert.Null(mgr.Current);
    }

    [Fact]
    public void KeyStatsSnapshot_MapsFormattedDistanceFieldsFromApi()
    {
        var type = typeof(KeyStatsCollectorService).GetNestedType("KeyStatsSnapshot", BindingFlags.NonPublic);

        AssertJsonProperty(type, "FormattedMouseDistance", "formattedMouseDistance");
        AssertJsonProperty(type, "FormattedScrollDistance", "formattedScrollDistance");
    }

    [Theory]
    [InlineData(true, false, "Sample ok; legacy upload failed")]
    [InlineData(false, true, "Sample upload failed; legacy ok")]
    [InlineData(true, true, null)]
    [InlineData(false, false, "Both sample and legacy uploads returned null response")]
    public void KeyStatsCollector_BuildsPartialUploadHealthMessage(bool sampleOk, bool legacyOk, string? expected)
    {
        var method = typeof(KeyStatsCollectorService).GetMethod("BuildUploadHealthMessage", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var actual = method.Invoke(null, [sampleOk, legacyOk]);

        Assert.Equal(expected, actual);
    }

    private static void AssertJsonProperty(Type? type, string propertyName, string expectedJsonName)
    {
        Assert.NotNull(type);
        var property = type.GetProperty(propertyName);
        Assert.NotNull(property);
        var attr = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(expectedJsonName, attr.Name);
    }
}
