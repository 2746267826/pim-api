using System.Text.Json;
using Pim.Core.Common;
using Pim.Module.Mobile.DTOs;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileWebContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SummaryResponse_SerializesDashboardFieldsExpectedByWeb()
    {
        var response = ApiResponse<MobileUsageSummaryResponse>.Ok(new MobileUsageSummaryResponse(
            "2026-07-06",
            "android-main",
            DateTimeOffset.Parse("2026-07-06T12:00:00Z"),
            1200,
            300,
            2,
            1,
            0.75,
            DateTimeOffset.Parse("2026-07-06T12:01:00Z"),
            [
                new MobileAppUsageSummaryDto(
                    "com.example.app",
                    "Example",
                    "tools",
                    1200,
                    1,
                    2,
                    DateTimeOffset.Parse("2026-07-06T11:00:00Z"),
                    "events",
                    1)
            ],
            [
                new MobileSyncBatchSummaryDto(
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    "android-main",
                    "batch-1",
                    DateTimeOffset.Parse("2026-07-06T10:00:00Z"),
                    DateTimeOffset.Parse("2026-07-06T11:00:00Z"),
                    DateTimeOffset.Parse("2026-07-06T11:01:00Z"),
                    "completed",
                    2,
                    0,
                    0,
                    0,
                    null)
            ],
            0));

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("\"appRanking\"", json);
        Assert.Contains("\"syncBatches\"", json);
        Assert.Contains("\"totalForegroundSeconds\":1200", json);
        Assert.Contains("\"clientBatchId\":\"batch-1\"", json);
    }

    [Fact]
    public void LocationHistoryResponse_SerializesPointsWrapperExpectedByWeb()
    {
        var response = ApiResponse<MobileLocationHistoryResponse>.Ok(new MobileLocationHistoryResponse(
            DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            "android-main",
            50,
            [
                new MobileLocationPointDto(
                    Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    "android-main",
                    DateTimeOffset.Parse("2026-07-06T10:00:00Z"),
                    DateTimeOffset.Parse("2026-07-06T10:00:10Z"),
                    31.230416,
                    121.473701,
                    9.4,
                    "gps",
                    "manual",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    "usable",
                    "{}")
            ]));

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("\"points\"", json);
        Assert.Contains("\"maxAccuracyMeters\":50", json);
        Assert.Contains("\"horizontalAccuracyMeters\":9.4", json);
        Assert.Contains("\"sourceKind\":\"manual\"", json);
    }
}
