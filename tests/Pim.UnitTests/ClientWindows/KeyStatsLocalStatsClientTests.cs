using System.Net;
using System.Text;
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
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KEYSTATS_BASE_URL")))
        {
            return;
        }

        Assert.Equal("http://127.0.0.1:18080", KeyStatsLocalStatsClient.ResolveBaseUrl());
    }

    [Fact]
    public async Task GetSnapshotAsync_MapsJsonFields()
    {
        const string json = """
            {
              "keyPresses": 11,
              "leftClicks": 2,
              "rightClicks": 3,
              "middleClicks": 4,
              "sideBackClicks": 5,
              "sideForwardClicks": 6,
              "mouseDistance": 7.5,
              "scrollDistance": 8.25
            }
            """;
        using var client = CreateClient(HttpStatusCode.OK, json);

        var (snapshot, error) = await client.GetSnapshotAsync();

        Assert.Null(error);
        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.KeyPresses);
        Assert.Equal(2, snapshot.LeftClicks);
        Assert.Equal(3, snapshot.RightClicks);
        Assert.Equal(4, snapshot.MiddleClicks);
        Assert.Equal(5, snapshot.SideBackClicks);
        Assert.Equal(6, snapshot.SideForwardClicks);
        Assert.Equal(7.5, snapshot.MouseDistance);
        Assert.Equal(8.25, snapshot.ScrollDistance);
    }

    [Fact]
    public async Task GetSnapshotAsync_HttpError_ReturnsError()
    {
        using var client = CreateClient(HttpStatusCode.InternalServerError, "boom");

        var (snapshot, error) = await client.GetSnapshotAsync();

        Assert.Null(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task GetSnapshotAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080") };
        using var client = new KeyStatsLocalStatsClient(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSnapshotAsync(cts.Token));
    }

    private static KeyStatsLocalStatsClient CreateClient(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var handler = new StubHandler(response);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18080") };
        return new KeyStatsLocalStatsClient(http);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response);
        }
    }
}
