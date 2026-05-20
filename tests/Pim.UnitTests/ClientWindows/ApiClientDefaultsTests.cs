using Pim.Client.Core;
using Pim.Client.Core.Services;
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
    public void AwCollectorCursorState_DoesNotAdvanceUntilUploadSucceeds()
    {
        var state = new AwCollectorCursorState();

        state.RecordFetched(windowLastId: 10, afkLastId: 20);

        Assert.Equal(0, state.LastWindowId);
        Assert.Equal(0, state.LastAfkId);

        state.CommitFetched();

        Assert.Equal(10, state.LastWindowId);
        Assert.Equal(20, state.LastAfkId);
    }
}
