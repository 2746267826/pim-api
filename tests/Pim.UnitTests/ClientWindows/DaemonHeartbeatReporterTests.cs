using Pim.Client.Core;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class DaemonHeartbeatReporterTests
{
    [Fact]
    public void BuildHeartbeat_UsesIpv4LoopbackDefaultServerUrl()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            "device-1",
            "1.0.0",
            ClientDefaults.DefaultServerUrl,
            DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
            null,
            null);

        Assert.Equal("http://127.0.0.1:5858", heartbeat.ServerUrl);
        Assert.Equal("windows", heartbeat.DaemonKind);
        Assert.Equal("device-1", heartbeat.DeviceId);
    }
}
