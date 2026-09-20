using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Pim.Module.Files;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// 文件模块端点路径契约（文件模块 v2 / P4 退役后）。
///
/// v2 只有 OneDrive 一种来源：Nextcloud 绑定/测试、WebDAV 回收站与版本端点随 P4 退役，
/// 因此本用例显式锁定「已退役的路由不再存在」，避免它们被无意加回来。
/// </summary>
public class FileEndpointPathTests
{
    [Fact]
    public void FileEndpointPaths_AreStable()
    {
        Assert.Equal("/api/v1/files", FileEndpointPaths.Root);
        Assert.Equal("/api/v1/files/providers", FileEndpointPaths.Providers);
    }

    private static List<RouteEndpoint> MapRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        new FilesModule().MapEndpoints(app);

        return ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static (string Method, string Route)? Normalize(RouteEndpoint endpoint)
    {
        var route = endpoint.RoutePattern.RawText ?? string.Empty;
        if (route.Length > 1)
        {
            route = route.TrimEnd('/');
        }

        var method = endpoint.Metadata
            .GetMetadata<IHttpMethodMetadata>()?
            .HttpMethods
            .FirstOrDefault();
        return method is null ? null : (method, route);
    }

    [Fact]
    public void MapEndpoints_RegistersTheV2Surface_WithAuthorization()
    {
        var routes = MapRoutes();
        var normalized = routes.Select(Normalize).Where(r => r is not null).Select(r => r!.Value).ToHashSet();

        var expected = new (string Method, string Route)[]
        {
            ("GET", "/api/v1/files/providers"),
            ("POST", "/api/v1/files/providers/onedrive"),
            ("GET", "/api/v1/files/providers/{id:guid}/binding-status"),
            ("DELETE", "/api/v1/files/providers/{id:guid}"),
            ("POST", "/api/v1/files/providers/{id:guid}/sync"),
            ("GET", "/api/v1/files/items"),
            ("GET", "/api/v1/files/items/{id:guid}"),
            ("POST", "/api/v1/files/items/upload"),
            ("GET", "/api/v1/files/items/{id:guid}/download"),
            ("POST", "/api/v1/files/items/{id:guid}/move"),
            ("POST", "/api/v1/files/items/{id:guid}/rename"),
            ("DELETE", "/api/v1/files/items/{id:guid}"),
            ("GET", "/api/v1/files/items/{id:guid}/content"),
            ("GET", "/api/v1/files/items/{id:guid}/thumbnail"),
            ("GET", "/api/v1/files/items/{id:guid}/preview-url"),
            ("GET", "/api/v1/files/items/{id:guid}/text"),
            ("PUT", "/api/v1/files/items/{id:guid}/text"),
            ("GET", "/api/v1/files/items/{id:guid}/snapshots"),
            ("POST", "/api/v1/files/items/{id:guid}/snapshots/{snapshotId:guid}/restore"),
            ("GET", "/api/v1/files/items/{id:guid}/extracted-text"),
            ("POST", "/api/v1/files/items/{id:guid}/restore"),
            ("GET", "/api/v1/files/search"),
            ("GET", "/api/v1/files/suggestions"),
            ("POST", "/api/v1/files/suggestions/{id:guid}/dismiss"),
            ("POST", "/api/v1/files/suggestions/{id:guid}/accept"),
            ("GET", "/api/v1/files/items/{id:guid}/open-link"),
        };

        foreach (var route in expected)
        {
            Assert.True(
                normalized.Contains(route),
                $"Missing route: {route.Method} {route.Route}. Found: {string.Join(", ", normalized.OrderBy(r => r.Route))}");
        }

        Assert.All(routes, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
    }

    /// <summary>
    /// P4 退役的端点必须真的消失：Nextcloud 绑定/连接测试、WebDAV 回收站、版本 API。
    /// 这些路由对 OneDrive 本就不可用（曾恒抛 5334），留着只会误导调用方。
    /// </summary>
    [Fact]
    public void RetiredRoutes_AreGoneAfterP4()
    {
        var normalized = MapRoutes()
            .Select(Normalize)
            .Where(r => r is not null)
            .Select(r => r!.Value)
            .ToHashSet();

        var retired = new (string Method, string Route)[]
        {
            ("POST", "/api/v1/files/providers/nextcloud"),
            ("POST", "/api/v1/files/providers/{id:guid}/test"),
            ("GET", "/api/v1/files/trash"),
            ("POST", "/api/v1/files/trash/{id:guid}/restore"),
            ("GET", "/api/v1/files/items/{id:guid}/versions"),
            ("GET", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/download"),
            ("POST", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore-preview"),
            ("POST", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore"),
            ("POST", "/api/v1/files/items/{id:guid}/index"),
        };

        foreach (var route in retired)
        {
            Assert.False(
                normalized.Contains(route),
                $"路由应随 P4 退役但仍存在：{route.Method} {route.Route}");
        }
    }

    [Fact]
    public void MapEndpoints_OperationRoutesUseNamedHandlers()
    {
        var routes = MapRoutes();

        var expected = new (string Method, string Route, string Handler)[]
        {
            ("POST", "/api/v1/files/providers/{id:guid}/sync", "SyncProviderAsync"),
            ("GET", "/api/v1/files/items", "ListItemsAsync"),
            ("GET", "/api/v1/files/items/{id:guid}", "GetItemAsync"),
            ("POST", "/api/v1/files/items/upload", "UploadItemAsync"),
            ("GET", "/api/v1/files/items/{id:guid}/download", "DownloadItemAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/move", "MoveItemAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/rename", "RenameItemAsync"),
            ("DELETE", "/api/v1/files/items/{id:guid}", "DeleteItemAsync"),
            ("GET", "/api/v1/files/items/{id:guid}/extracted-text", "OneDriveReadTextAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/restore", "OneDriveRestoreItemAsync"),
            ("GET", "/api/v1/files/search", "SearchAsync"),
            ("GET", "/api/v1/files/items/{id:guid}/open-link", "BuildOpenLinkAsync"),
        };

        foreach (var (method, route, handlerName) in expected)
        {
            var endpoint = routes.Single(candidate =>
                (candidate.RoutePattern.RawText ?? string.Empty).TrimEnd('/') == route
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) is true);
            var handler = endpoint.Metadata.OfType<System.Reflection.MethodInfo>().Single();

            Assert.Equal(typeof(FilesModule), handler.DeclaringType);
            Assert.Equal(handlerName, handler.Name);
        }
    }
}
