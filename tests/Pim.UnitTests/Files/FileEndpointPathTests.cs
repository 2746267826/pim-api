using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
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
    public void MapEndpoints_RegistersAuthorizedRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        new FilesModule().MapEndpoints(app);

        var routeEndpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        var foundRoutes = routeEndpoints
            .SelectMany(endpoint => endpoint.Metadata
                .GetMetadata<IHttpMethodMetadata>()?
                .HttpMethods
                .Select(method => (Method: method, Route: NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty)))
                ?? Array.Empty<(string Method, string Route)>())
            .OrderBy(route => route.Method)
            .ThenBy(route => route.Route)
            .ToList();

        var expectedRoutes = new (string Method, string Route)[]
        {
            ("GET", "/api/v1/files/providers"),
            ("POST", "/api/v1/files/providers/nextcloud"),
            ("POST", "/api/v1/files/providers/{id:guid}/test"),
            ("POST", "/api/v1/files/providers/{id:guid}/sync"),
            ("GET", "/api/v1/files/items"),
            ("GET", "/api/v1/files/items/{id:guid}"),
            ("POST", "/api/v1/files/items/upload"),
            ("GET", "/api/v1/files/items/{id:guid}/download"),
            ("POST", "/api/v1/files/items/{id:guid}/move"),
            ("POST", "/api/v1/files/items/{id:guid}/rename"),
            ("DELETE", "/api/v1/files/items/{id:guid}"),
            ("GET", "/api/v1/files/trash"),
            ("POST", "/api/v1/files/trash/{id:guid}/restore"),
            ("GET", "/api/v1/files/items/{id:guid}/versions"),
            ("GET", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/download"),
            ("POST", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore-preview"),
            ("POST", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore"),
            ("POST", "/api/v1/files/items/{id:guid}/index"),
            ("GET", "/api/v1/files/search"),
            ("GET", "/api/v1/files/suggestions"),
            ("POST", "/api/v1/files/suggestions/{id:guid}/dismiss"),
            ("POST", "/api/v1/files/suggestions/{id:guid}/accept"),
            ("GET", "/api/v1/files/items/{id:guid}/open-link")
        };

        foreach (var expectedRoute in expectedRoutes)
        {
            var endpoints = routeEndpoints
                .Where(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty) == expectedRoute.Route)
                .Where(endpoint => endpoint.Metadata
                    .GetMetadata<IHttpMethodMetadata>()?
                    .HttpMethods
                    .Contains(expectedRoute.Method) is true)
                .ToList();

            Assert.True(
                endpoints.Count > 0,
                $"Missing route: {expectedRoute.Method} {expectedRoute.Route}. Found: {string.Join(", ", foundRoutes.Select(route => $"{route.Method} {route.Route}"))}");
            Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        }
    }

    private static string NormalizeRoute(string route)
        => route.Length > 1 ? route.TrimEnd('/') : route;
}
