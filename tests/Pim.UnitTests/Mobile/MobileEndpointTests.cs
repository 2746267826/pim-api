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
        Assert.All(endpoints, endpoint => Assert.Contains(
            endpoint.Metadata,
            metadata => metadata is IAuthorizeData));
    }
}
