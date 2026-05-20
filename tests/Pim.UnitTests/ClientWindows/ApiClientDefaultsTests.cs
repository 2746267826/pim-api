using Pim.Client.Core;
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
    public void AwCollectorCursorState_DoesNotAdvanceUntilUploadSucceeds()
    {
        var state = new AwCollectorCursorState();

        state.RecordFetched("aw-watcher-window_DESKTOP", 10);
        state.RecordFetched("aw-watcher-afk_DESKTOP", 20);

        Assert.Equal(0, state.LastForBucket("aw-watcher-window_DESKTOP"));
        Assert.Equal(0, state.LastForBucket("aw-watcher-afk_DESKTOP"));

        state.CommitFetched();

        Assert.Equal(10, state.LastForBucket("aw-watcher-window_DESKTOP"));
        Assert.Equal(20, state.LastForBucket("aw-watcher-afk_DESKTOP"));
    }

    [Fact]
    public void KeyStatsSnapshot_MapsFormattedDistanceFieldsFromApi()
    {
        var type = typeof(KeyStatsCollectorService).GetNestedType("KeyStatsSnapshot", BindingFlags.NonPublic);

        AssertJsonProperty(type, "FormattedMouseDistance", "formattedMouseDistance");
        AssertJsonProperty(type, "FormattedScrollDistance", "formattedScrollDistance");
    }

    [Fact]
    public void AwPayloads_MapSnakeCaseActivityWatchFields()
    {
        var infoType = typeof(AwCollectorService).GetNestedType("AwInfoPayload", BindingFlags.NonPublic);
        var bucketType = typeof(AwCollectorService).GetNestedType("AwBucketPayload", BindingFlags.NonPublic);

        AssertJsonProperty(infoType, "DeviceId", "device_id");
        AssertJsonProperty(bucketType, "LastUpdated", "last_updated");
    }

    [Fact]
    public void AwCollector_LiveBacklogHelpersUseUnboundedFetchAndServerCappedBatches()
    {
        var unboundedLimit = typeof(AwCollectorService).GetField("ActivityWatchUnboundedLimit", BindingFlags.NonPublic | BindingFlags.Static);
        var uploadBatchSize = typeof(AwCollectorService).GetField("CompleteAwUploadBatchSize", BindingFlags.NonPublic | BindingFlags.Static);
        var urlMethod = typeof(AwCollectorService).GetMethod("BuildEventsUrl", BindingFlags.NonPublic | BindingFlags.Static);
        var chunkMethod = typeof(AwCollectorService).GetMethod("ChunkCompleteAwUploadEvents", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(unboundedLimit);
        Assert.NotNull(uploadBatchSize);
        Assert.Equal(-1, (int)unboundedLimit.GetRawConstantValue()!);
        Assert.Equal(500, (int)uploadBatchSize.GetRawConstantValue()!);

        Assert.NotNull(urlMethod);
        var url = Assert.IsType<string>(urlMethod.Invoke(null, ["aw-watcher-window_DESKTOP"]));
        Assert.Equal("/api/0/buckets/aw-watcher-window_DESKTOP/events?limit=-1", url);

        Assert.NotNull(chunkMethod);
        var chunked = chunkMethod
            .MakeGenericMethod(typeof(int))
            .Invoke(null, [Enumerable.Range(1, 501).ToList()]);
        var chunks = Assert.IsAssignableFrom<IEnumerable<IReadOnlyList<int>>>(chunked).ToList();
        Assert.Collection(
            chunks,
            first => Assert.Equal(500, first.Count),
            second => Assert.Single(second));
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

    [Theory]
    [InlineData(3, 3, 2, 2, null)]
    [InlineData(3, 0, 2, 2, "Partial AW upload failure: pending 3 events")]
    [InlineData(3, 3, 2, 0, "Partial AW upload failure: pending 2 events")]
    [InlineData(3, 0, 2, 0, "Partial AW upload failure: pending 5 events")]
    public void AwCollector_BuildsPartialUploadHealthMessage(
        int windowFetched,
        int windowUploaded,
        int afkFetched,
        int afkUploaded,
        string? expected)
    {
        var method = typeof(AwCollectorService).GetMethod("BuildUploadHealthMessage", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var outcomeType = typeof(AwCollectorService).GetNestedType("AwBucketUploadOutcome", BindingFlags.NonPublic);
        Assert.NotNull(outcomeType);
        var outcomes = Array.CreateInstance(outcomeType, 2);
        outcomes.SetValue(Activator.CreateInstance(outcomeType, [windowFetched, windowUploaded, null]), 0);
        outcomes.SetValue(Activator.CreateInstance(outcomeType, [afkFetched, afkUploaded, null]), 1);

        var actual = method.Invoke(null, [outcomes]);

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
