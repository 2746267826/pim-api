using Pim.Client.Core;
using Pim.Client.Core.Services;
using Pim.Core.Operations;
using System.Text.Json;
using Xunit;

using ApiDaemonHeartbeatRequest = Pim.Core.Operations.DaemonHeartbeatRequest;

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

    [Fact]
    public void BuildHeartbeat_UsesDefaultServerUrlWhenBlank()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            "device-1",
            "1.0.0",
            " ",
            null,
            null,
            null);

        Assert.Equal("http://127.0.0.1:5858", heartbeat.ServerUrl);
    }

    [Fact]
    public void BuildHeartbeat_StatusJsonIncludesMachineAndProcess()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            "device-1",
            "1.0.0",
            ClientDefaults.DefaultServerUrl,
            null,
            null,
            null);

        using var status = JsonDocument.Parse(heartbeat.StatusJson);

        Assert.Equal(Environment.MachineName, status.RootElement.GetProperty("machine").GetString());
        Assert.Equal("pim-windows-daemon", status.RootElement.GetProperty("process").GetString());
    }

    [Fact]
    public void ClientHeartbeatJson_DeserializesIntoApiRequestContract()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            "device-1",
            "1.0.0",
            ClientDefaults.DefaultServerUrl,
            DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-24T00:01:00Z"),
            null);
        var json = JsonSerializer.Serialize(heartbeat, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var request = JsonSerializer.Deserialize<ApiDaemonHeartbeatRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal(DaemonSourceState.Unknown, request.ActivityWatchState);
        Assert.Equal(DaemonSourceState.Unknown, request.KeyStatsState);
    }
}
