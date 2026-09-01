using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class BrowserBridgeServiceTests
{
    private static BrowserHeartbeat Heartbeat(string instanceId, string browser = "chrome", string url = "https://example.com")
        => new()
        {
            Url = url,
            Title = "Title",
            Audible = false,
            Incognito = false,
            TabCount = 3,
            Timestamp = System.DateTimeOffset.UtcNow.ToString("O"),
            Browser = browser,
            InstanceId = instanceId,
        };

    [Fact]
    public void OnHeartbeat_DifferentInstanceIds_CreateSeparateConnections()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnHeartbeat(Heartbeat("ext_a"));
        bridge.OnHeartbeat(Heartbeat("ext_b"));

        Assert.Equal(2, bridge.Connections.Count);
        Assert.True(bridge.Connections["ext_a"].IsConnected);
        Assert.True(bridge.Connections["ext_b"].IsConnected);
        Assert.Equal(1, bridge.Connections["ext_a"].HeartbeatCount);
        Assert.Equal(1, bridge.Connections["ext_b"].HeartbeatCount);
    }

    [Fact]
    public void OnHeartbeat_SameInstanceId_UpdatesSameConnection()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnHeartbeat(Heartbeat("ext_a", url: "https://first.example"));
        bridge.OnHeartbeat(Heartbeat("ext_a", url: "https://second.example"));

        Assert.Single(bridge.Connections);
        var conn = bridge.Connections["ext_a"];
        Assert.Equal(2, conn.HeartbeatCount);
        Assert.Equal("https://second.example", conn.LastUrl);
    }

    [Fact]
    public void OnHeartbeat_NormalizesBrowserType()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnHeartbeat(Heartbeat("ext_a", browser: "  EDGE  "));
        bridge.OnHeartbeat(Heartbeat("ext_b", browser: "vivaldi"));

        Assert.Equal("edge", bridge.Connections["ext_a"].BrowserType);
        Assert.Equal("other", bridge.Connections["ext_b"].BrowserType);
    }

    [Fact]
    public void OnHeartbeat_EmptyInstanceId_MapsToUnknown()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnHeartbeat(Heartbeat(""));

        Assert.True(bridge.Connections.ContainsKey("unknown"));
    }

    [Fact]
    public void OnHeartbeat_NormalizesHeartbeatFields()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnHeartbeat(Heartbeat("ext_a", browser: "Chrome"));

        var last = bridge.LastHeartbeat;
        Assert.NotNull(last);
        Assert.Equal("chrome", last!.Browser);
        Assert.Equal("ext_a", last.InstanceId);
    }

    [Fact]
    public void CheckConnections_MarksSilentConnectionDisconnected()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnHeartbeat(Heartbeat("ext_a"));
        var conn = bridge.Connections["ext_a"];
        // Simulate a heartbeat older than the 120s timeout.
        conn.LastHeartbeat = System.DateTimeOffset.UtcNow.AddSeconds(-121);
        conn.IsConnected = true;

        bridge.CheckConnections();

        Assert.False(conn.IsConnected);
        Assert.False(bridge.IsConnected);
    }

    [Fact]
    public void CheckConnections_KeepsRecentConnectionConnected()
    {
        using var bridge = new BrowserBridgeService();

        bridge.OnHeartbeat(Heartbeat("ext_a"));
        bridge.CheckConnections();

        Assert.True(bridge.Connections["ext_a"].IsConnected);
    }
}