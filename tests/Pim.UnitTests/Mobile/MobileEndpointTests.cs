using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Pim.Module.Mobile;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileEndpointTests
{
    [Fact]
    public void MobileEndpoints_AreMappedUnderApiV1AndRequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        new MobileModule().MapEndpoints(app);

        var endpoints = app.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/mobile", StringComparison.Ordinal) == true)
            .ToList();

        var paths = endpoints.Select(endpoint => endpoint.RoutePattern.RawText).ToHashSet();
        Assert.Contains("/api/v1/mobile/devices", paths);
        Assert.Contains("/api/v1/mobile/devices/register", paths);
        Assert.Contains("/api/v1/mobile/sync/gaps", paths);
        Assert.Contains("/api/v1/mobile/usage/events", paths);
        Assert.Contains("/api/v1/mobile/location/points", paths);
        Assert.Contains("/api/v1/mobile/summary", paths);
        Assert.Contains("/api/v1/mobile/timeline", paths);
        Assert.Contains("/api/v1/mobile/location/history", paths);
        Assert.Contains("/api/v1/mobile/location/analytics/overview", paths);
        Assert.Contains("/api/v1/mobile/location/analytics/tracks", paths);
        Assert.Contains("/api/v1/mobile/location/analytics/segments/{segmentId}", paths);
        Assert.Contains("/api/v1/mobile/location/analytics/segments/{segmentId}/points", paths);
        Assert.Contains("/api/v1/mobile/quality", paths);
        Assert.Contains("/api/v1/mobile/analytics/overview", paths);
        Assert.Contains("/api/v1/mobile/analytics/heatmap", paths);
        Assert.Contains("/api/v1/mobile/analytics/charts", paths);
        Assert.Contains("/api/v1/mobile/analytics/timeline-blocks", paths);
        Assert.Contains("/api/v1/mobile/analytics/timeline-blocks/{blockId}/sessions", paths);
        Assert.Contains("/api/v1/mobile/analytics/sessions/{sessionId}/events", paths);
        Assert.Contains("/api/v1/mobile/analytics/goals", paths);
        Assert.Contains("/api/v1/mobile/apps/catalog-overrides", paths);
        Assert.Contains("/api/v1/mobile/apps/catalog-overrides/{packageName}", paths);
        Assert.Contains("/api/v1/mobile/apps/category-rules", paths);
        Assert.Contains("/api/v1/mobile/apps/category-rules/{ruleId}", paths);
        Assert.All(endpoints, endpoint => Assert.Contains(
            endpoint.Metadata,
            metadata => metadata is IAuthorizeData));
    }

    /// <summary>
    /// #239 / EPIC #254 D-1：按日接口的 date 必须解析为业务日窗口
    /// [D 04:00, D+1 04:00)（Asia/Shanghai），而不是 UTC 自然日。
    /// </summary>
    [Fact]
    public void BuildSummaryQuery_ParsesDateAsShanghaiBusinessDay()
    {
        var query = MobileModule.BuildSummaryQuery("android-main", "2026-09-13", null, null);

        Assert.Equal("android-main", query.DeviceId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T20:00:00Z"), query.RangeStartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T20:00:00Z"), query.RangeEndUtc);
    }

    [Fact]
    public void BuildSummaryQuery_FallsBackToExplicitRangeWhenDateIsMissingOrInvalid()
    {
        var start = DateTimeOffset.Parse("2026-09-13T01:00:00Z");
        var end = DateTimeOffset.Parse("2026-09-13T05:00:00Z");

        var missingDate = MobileModule.BuildSummaryQuery("android-main", null, start, end);
        Assert.Equal(start, missingDate.RangeStartUtc);
        Assert.Equal(end, missingDate.RangeEndUtc);

        var invalidDate = MobileModule.BuildSummaryQuery("android-main", "13/09/2026", start, end);
        Assert.Equal(start, invalidDate.RangeStartUtc);
        Assert.Equal(end, invalidDate.RangeEndUtc);
    }

    [Fact]
    public void MobileServices_RegisterLocationAnalyticsServices()
    {
        var services = new ServiceCollection();

        new MobileModule().RegisterServices(services, new ConfigurationBuilder().Build());

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(MobileLocationQueryService)
            && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(MobileLocationAggregationService)
            && descriptor.Lifetime == ServiceLifetime.Scoped);
    }
}
