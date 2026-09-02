using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class SiteEventNormalizerTests
{
    [Fact]
    public void FocusEvent_WithValidRange_IsNormalized()
    {
        var raw = new SiteEventDto { Kind = "focus", Host = " GitHub.COM ", StartMs = 1000, EndMs = 6000 };

        var result = SiteEventNormalizer.Normalize(raw);

        Assert.NotNull(result);
        Assert.Equal("github.com", result!.Host);
        Assert.Equal(1000, result.StartMs);
        Assert.Equal(6000, result.EndMs);
        Assert.Equal(5000, result.DurationMs);
    }

    [Fact]
    public void UnknownKind_IsDropped()
    {
        var result = SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "bogus", Host = "github.com" });
        Assert.Null(result);
    }

    [Fact]
    public void BlankHost_IsDropped()
    {
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "visit", Host = "  " }));
    }

    [Fact]
    public void OversizedHost_IsDropped()
    {
        var result = SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "visit", Host = new string('a', 300) });
        Assert.Null(result);
    }

    [Fact]
    public void FocusEvent_MissingTimestamps_IsDropped()
    {
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "focus", Host = "github.com" }));
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "focus", Host = "github.com", StartMs = 100 }));
    }

    [Fact]
    public void FocusEvent_EndBeforeStart_IsDropped()
    {
        var result = SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "focus", Host = "github.com", StartMs = 6000, EndMs = 1000 });
        Assert.Null(result);
    }

    [Fact]
    public void FocusEvent_LongerThanDay_IsDropped()
    {
        var result = SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "focus", Host = "github.com", StartMs = 0, EndMs = 25 * 60 * 60 * 1000 });
        Assert.Null(result);
    }

    [Fact]
    public void TickEvent_RequiresStartAndPositiveDuration()
    {
        Assert.NotNull(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "tick", Host = "github.com", StartMs = 1000, DurationMs = 500 }));
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "tick", Host = "github.com", StartMs = 1000 }));
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "tick", Host = "github.com", StartMs = 1000, DurationMs = 0 }));
    }

    [Fact]
    public void VisitEvent_OnlyNeedsHost()
    {
        var result = SiteEventNormalizer.Normalize(new SiteEventDto { Kind = "visit", Host = "GitHub.com" });
        Assert.NotNull(result);
        Assert.Equal("github.com", result!.Host);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("media")]
    public void RunAndMedia_RequireDateAndPositiveDuration(string kind)
    {
        Assert.NotNull(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = kind, Host = "github.com", Date = "2026-09-03", DurationMs = 100 }));
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = kind, Host = "github.com", DurationMs = 100 }));
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = kind, Host = "github.com", Date = "2026/09/03", DurationMs = 100 }));
        Assert.Null(SiteEventNormalizer.Normalize(new SiteEventDto { Kind = kind, Host = "github.com", Date = "2026-09-03", DurationMs = 0 }));
    }
}

public class BrowserBridgeSiteChannelTests
{
    [Fact]
    public void OnSiteEvents_ValidEventsFlowToReader()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnSiteEvents(new List<SiteEventDto>
        {
            new() { Kind = "focus", Host = "GitHub.com", StartMs = 1, EndMs = 2000 },
            new() { Kind = "visit", Host = "example.com" },
        });

        Assert.True(bridge.SiteReader.TryRead(out var first));
        Assert.Equal("focus", first.Kind);
        Assert.Equal("github.com", first.Host);
        Assert.True(bridge.SiteReader.TryRead(out var second));
        Assert.Equal("visit", second.Kind);

        Assert.Equal(2, bridge.SiteEventsReceived);
        Assert.Equal(0, bridge.SiteEventsDropped);
        Assert.NotNull(bridge.SiteLastBatchTime);
    }

    [Fact]
    public void OnSiteEvents_InvalidEventsAreCounted()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnSiteEvents(new List<SiteEventDto>
        {
            new() { Kind = "bogus", Host = "github.com" },
            new() { Kind = "focus", Host = "", StartMs = 1, EndMs = 2 },
        });

        Assert.False(bridge.SiteReader.TryRead(out _));
        Assert.Equal(2, bridge.SiteEventsDropped);
    }

    [Fact]
    public void IsSiteConnected_FlipsAfterSilence()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnSiteEvents(new List<SiteEventDto> { new() { Kind = "visit", Host = "example.com" } });
        Assert.True(bridge.IsSiteConnected);

        // Simulate silence beyond the 120s disconnect window.
        bridge.SiteLastBatchTime = DateTimeOffset.UtcNow.AddSeconds(-121);
        Assert.False(bridge.IsSiteConnected);
    }
}
