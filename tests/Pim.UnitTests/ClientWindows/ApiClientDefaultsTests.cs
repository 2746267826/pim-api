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

        state.RecordFetched(windowLastId: 10, afkLastId: 20);

        Assert.Equal(0, state.LastWindowId);
        Assert.Equal(0, state.LastAfkId);

        state.CommitFetched();

        Assert.Equal(10, state.LastWindowId);
        Assert.Equal(20, state.LastAfkId);
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
    [InlineData(3, 0, 2, 2, "Partial AW upload failure: window pending 3, afk pending 0")]
    [InlineData(3, 3, 2, 0, "Partial AW upload failure: window pending 0, afk pending 2")]
    [InlineData(3, 0, 2, 0, "Partial AW upload failure: window pending 3, afk pending 2")]
    public void AwCollector_BuildsPartialUploadHealthMessage(
        int windowFetched,
        int windowUploaded,
        int afkFetched,
        int afkUploaded,
        string? expected)
    {
        var method = typeof(AwCollectorService).GetMethod("BuildUploadHealthMessage", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var actual = method.Invoke(null, [windowFetched, windowUploaded, afkFetched, afkUploaded]);

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
