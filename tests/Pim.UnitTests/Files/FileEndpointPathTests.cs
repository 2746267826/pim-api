using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Module.Files;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileEndpointPathTests
{
    [Fact]
    public void FileEndpointPaths_AreStable()
    {
        Assert.Equal("/api/v1/files", FileEndpointPaths.Root);
        Assert.Equal("/api/v1/files/providers", FileEndpointPaths.Providers);
        Assert.Equal("/api/v1/files/providers/nextcloud", FileEndpointPaths.NextcloudProviders);
        Assert.Equal("/api/v1/files/providers/11111111-1111-1111-1111-111111111111/test", FileEndpointPaths.ProviderTest("11111111-1111-1111-1111-111111111111"));
        Assert.Equal("/api/v1/files/items/22222222-2222-2222-2222-222222222222/download", FileEndpointPaths.ItemDownload("22222222-2222-2222-2222-222222222222"));
        Assert.Equal("/api/v1/files/items/22222222-2222-2222-2222-222222222222/versions/33333333-3333-3333-3333-333333333333/restore", FileEndpointPaths.VersionRestore("22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public async Task MapEndpoints_RegistersAuthorizedRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        new FilesModule().MapEndpoints(app);
        await app.StartAsync();

        var routeEndpoints = app.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToLookup(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty));

        var expectedRoutes = new[]
        {
            "/api/v1/files/providers",
            "/api/v1/files/providers/nextcloud",
            "/api/v1/files/providers/{id:guid}/test",
            "/api/v1/files/providers/{id:guid}/sync",
            "/api/v1/files/items",
            "/api/v1/files/items/{id:guid}",
            "/api/v1/files/items/upload",
            "/api/v1/files/items/{id:guid}/download",
            "/api/v1/files/items/{id:guid}/move",
            "/api/v1/files/items/{id:guid}/rename",
            "/api/v1/files/items/{id:guid}",
            "/api/v1/files/trash",
            "/api/v1/files/trash/{id:guid}/restore",
            "/api/v1/files/items/{id:guid}/versions",
            "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/download",
            "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore-preview",
            "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore",
            "/api/v1/files/items/{id:guid}/index",
            "/api/v1/files/search",
            "/api/v1/files/suggestions",
            "/api/v1/files/suggestions/{id:guid}/dismiss",
            "/api/v1/files/suggestions/{id:guid}/accept",
            "/api/v1/files/items/{id:guid}/open-link"
        };

        foreach (var expectedRoute in expectedRoutes)
        {
            var endpoints = routeEndpoints[expectedRoute].ToList();
            Assert.True(endpoints.Count > 0, $"Missing route: {expectedRoute}");
            Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        }
    }

    private static string NormalizeRoute(string route)
        => route.Length > 1 ? route.TrimEnd('/') : route;
}
