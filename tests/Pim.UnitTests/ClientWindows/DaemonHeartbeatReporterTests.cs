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
    public void BuildHeartbeat_UsesProvidedSourceStatesAndStatusDetails()
    {
        var heartbeat = DaemonHeartbeatReporter.BuildHeartbeat(
            deviceId: "device-1",
            version: "1.0.0",
            serverUrl: ClientDefaults.DefaultServerUrl,
            lastSuccessfulUploadAt: DateTimeOffset.Parse("2026-05-24T00:00:00Z"),
            lastAttemptedUploadAt: DateTimeOffset.Parse("2026-05-24T00:01:00Z"),
            lastError: null,
            uploadQueueCount: 3,
            activityWatchState: "Available",
            keyStatsState: "Unavailable",
            statusDetails: new
            {
                keyStatsDetailState = "ApiOkButStaleZero",
                keyStatsProcessCount = 2,
                keyStatsSkipReason = "stale-zero",
                awQueueCount = 3
            });

        Assert.Equal("Available", heartbeat.ActivityWatchState);
        Assert.Equal("Unavailable", heartbeat.KeyStatsState);
        Assert.Equal(3, heartbeat.UploadQueueCount);

        using var status = JsonDocument.Parse(heartbeat.StatusJson);
        Assert.Equal("ApiOkButStaleZero", status.RootElement.GetProperty("keyStatsDetailState").GetString());
        Assert.Equal(2, status.RootElement.GetProperty("keyStatsProcessCount").GetInt32());
        Assert.Equal("stale-zero", status.RootElement.GetProperty("keyStatsSkipReason").GetString());
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
            null,
            uploadQueueCount: 1,
            activityWatchState: "Available",
            keyStatsState: "Unavailable");
        var json = JsonSerializer.Serialize(heartbeat, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var request = JsonSerializer.Deserialize<ApiDaemonHeartbeatRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal(DaemonSourceState.Available, request.ActivityWatchState);
        Assert.Equal(DaemonSourceState.Unavailable, request.KeyStatsState);
        Assert.Equal(1, request.UploadQueueCount);
    }
}
