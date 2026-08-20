using Pim.Client.Core;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using System.Text.Json;
using Xunit;

using ServerPlannedOfflineRequest = Pim.Core.Operations.PlannedOfflineRequest;

namespace Pim.UnitTests.ClientWindows;

public class PlannedOfflineReporterTests
{
    [Fact]
    public void BuildRequest_FillsDeviceKindAndReason()
    {
        var at = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var req = PlannedOfflineReporter.BuildRequest(Environment.MachineName, "shutdown", at);
        Assert.Equal(Environment.MachineName, req.DeviceId);
        Assert.Equal("windows", req.DaemonKind);
        Assert.Equal("shutdown", req.Reason);
        Assert.Equal(at, req.OccurredAt);
    }

    [Fact]
    public void BuildRequest_RoundTripsToServerDto()
    {
        var at = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var client = PlannedOfflineReporter.BuildRequest("PC-1", "suspend", at);
        var json = JsonSerializer.Serialize(client, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var server = JsonSerializer.Deserialize<ServerPlannedOfflineRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(server);
        Assert.Equal("PC-1", server.DeviceId);
        Assert.Equal("windows", server.DaemonKind);
        Assert.Equal("suspend", server.Reason);
        Assert.Equal(at, server.OccurredAt);
    }
}