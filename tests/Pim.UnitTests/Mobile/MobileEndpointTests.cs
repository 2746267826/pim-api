using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Pim.Module.Mobile;
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
}
