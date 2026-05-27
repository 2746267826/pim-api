using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;
using System.Reflection;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileEndpointPathTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

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

    [Fact]
    public void MapEndpoints_FileOperationRoutesUseNamedHandlers()
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

        var expectedHandlers = new (string Method, string Route, string Handler)[]
        {
            ("POST", "/api/v1/files/providers/{id:guid}/sync", "SyncProviderAsync"),
            ("GET", "/api/v1/files/items", "ListItemsAsync"),
            ("GET", "/api/v1/files/items/{id:guid}", "GetItemAsync"),
            ("POST", "/api/v1/files/items/upload", "UploadItemAsync"),
            ("GET", "/api/v1/files/items/{id:guid}/download", "DownloadItemAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/move", "MoveItemAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/rename", "RenameItemAsync"),
            ("DELETE", "/api/v1/files/items/{id:guid}", "DeleteItemAsync"),
            ("GET", "/api/v1/files/trash", "ListTrashAsync"),
            ("POST", "/api/v1/files/trash/{id:guid}/restore", "RestoreTrashAsync"),
            ("GET", "/api/v1/files/items/{id:guid}/versions", "ListVersionsAsync"),
            ("GET", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/download", "DownloadVersionAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore-preview", "PreviewVersionRestoreAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/versions/{versionId:guid}/restore", "RestoreVersionAsync"),
            ("POST", "/api/v1/files/items/{id:guid}/index", "IndexItemAsync"),
            ("GET", "/api/v1/files/search", "SearchAsync"),
            ("GET", "/api/v1/files/suggestions", "ListSuggestionsAsync"),
            ("POST", "/api/v1/files/suggestions/{id:guid}/dismiss", "DismissSuggestionAsync"),
            ("POST", "/api/v1/files/suggestions/{id:guid}/accept", "AcceptSuggestionAsync"),
            ("GET", "/api/v1/files/items/{id:guid}/open-link", "BuildOpenLinkAsync")
        };

        foreach (var expected in expectedHandlers)
        {
            var endpoint = FindEndpoint(routeEndpoints, expected.Method, expected.Route);
            var handler = endpoint.Metadata.OfType<MethodInfo>().Single();

            Assert.Equal(typeof(FilesModule), handler.DeclaringType);
            Assert.Equal(expected.Handler, handler.Name);
        }
    }

    [Fact]
    public async Task MapEndpoints_ExecutesOperationHandlersWithBoundInputs()
    {
        var adapter = new EndpointFakeFileProviderAdapter();
        await using var app = BuildOperationApp(adapter);
        var (provider, item) = await SeedProviderAndItemAsync(app.Services);

        adapter.MoveResult = ProviderItem("file-1", "/Archive/report.txt", "report.txt");
        await InvokeEndpointAsync(
            app,
            "POST",
            "/api/v1/files/items/{id:guid}/move",
            context =>
            {
                context.Request.RouteValues["id"] = item.Id.ToString();
                SetJsonBody(context, """{"destinationPath":"/Archive/report.txt"}""");
            });

        Assert.Equal(1, adapter.MoveCallCount);
        Assert.Equal("/Reports/report.txt", adapter.LastMoveSourcePath);
        Assert.Equal("/Archive/report.txt", adapter.LastMoveDestinationPath);

        await InvokeEndpointAsync(
            app,
            "POST",
            "/api/v1/files/trash/{id:guid}/restore",
            context =>
            {
                context.Request.RouteValues["id"] = provider.Id.ToString();
                context.Request.QueryString = new QueryString("?trashId=trash-1");
            });

        Assert.Equal(1, adapter.RestoreTrashCallCount);
        Assert.Equal(provider.Id, adapter.LastRestoreTrashProviderId);
        Assert.Equal("trash-1", adapter.LastRestoreTrashId);

        adapter.UploadResult = ProviderItem("uploaded-file", "/Uploads/upload.txt", "upload.txt");
        await InvokeEndpointAsync(
            app,
            "POST",
            "/api/v1/files/items/upload",
            async context =>
            {
                await SetMultipartUploadBodyAsync(
                    context,
                    provider.Id,
                    "/Uploads/upload.txt",
                    "upload.txt",
                    "uploaded");
            });

        Assert.Equal(1, adapter.UploadCallCount);
        Assert.Equal("/Uploads/upload.txt", adapter.LastUploadDestinationPath);
        Assert.Equal("text/plain", adapter.LastUploadContentType);
        Assert.Equal("uploaded", adapter.LastUploadContent);
    }

    private static RouteEndpoint FindEndpoint(
        IReadOnlyList<RouteEndpoint> routeEndpoints,
        string method,
        string route)
        => routeEndpoints.Single(endpoint =>
            NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty) == route
            && endpoint.Metadata
                .GetMetadata<IHttpMethodMetadata>()?
                .HttpMethods
                .Contains(method) is true);

    private static string NormalizeRoute(string route)
        => route.Length > 1 ? route.TrimEnd('/') : route;

    private static WebApplication BuildOperationApp(EndpointFakeFileProviderAdapter adapter)
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var builder = WebApplication.CreateBuilder();
        var databaseName = $"file-endpoint-{Guid.NewGuid()}";
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<PimDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<ICurrentUserService>(_ => new FixedCurrentUserService(UserId));
        builder.Services.AddSingleton<ISecretProtector, FakeSecretProtector>();
        builder.Services.AddScoped<IAuditLogService, AuditLogService>();
        builder.Services.AddScoped<FileProviderBindingService>();
        builder.Services.AddScoped<FileOperationService>();
        builder.Services.AddSingleton<IFileProviderAdapter>(adapter);

        var app = builder.Build();
        new FilesModule().MapEndpoints(app);
        return app;
    }

    private static async Task<(FileProviderEntity Provider, FileItemEntity Item)> SeedProviderAndItemAsync(
        IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var now = DateTimeOffset.UtcNow;
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            BaseUrl = "https://cloud.example.test",
            InternalBaseUrl = "http://nextcloud",
            Username = "alice",
            AppPasswordSecret = "protected:app-password",
            Status = "connected",
            CreatedAt = now,
            UpdatedAt = now
        };
        var item = new FileItemEntity
        {
            Provider = provider,
            ExternalFileId = "file-1",
            Path = "/Reports/report.txt",
            Name = "report.txt",
            ItemType = "file",
            MimeType = "text/plain",
            Size = 100,
            Etag = "etag-1",
            Permissions = "RGDNVW",
            IsDeleted = false,
            LastSeenAt = now,
            CreatedAt = now,
            ModifiedAt = now,
            SyncedAt = now
        };

        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().Add(item);
        await db.SaveChangesAsync();

        return (provider, item);
    }

    private static async Task InvokeEndpointAsync(
        WebApplication app,
        string method,
        string route,
        Action<DefaultHttpContext> configure)
    {
        await InvokeEndpointAsync(
            app,
            method,
            route,
            context =>
            {
                configure(context);
                return Task.CompletedTask;
            });
    }

    private static async Task InvokeEndpointAsync(
        WebApplication app,
        string method,
        string route,
        Func<DefaultHttpContext, Task> configureAsync)
    {
        var routeEndpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
        var endpoint = FindEndpoint(routeEndpoints, method, route);
        Assert.NotNull(endpoint.RequestDelegate);

        using var requestScope = app.Services.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());

        await configureAsync(context);
        await endpoint.RequestDelegate(context);

        context.Response.Body.Position = 0;
        var responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.True(
            context.Response.StatusCode == StatusCodes.Status200OK,
            $"Expected 200 but was {context.Response.StatusCode}: {responseBody}");
    }

    private static void SetJsonBody(DefaultHttpContext context, string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
    }

    private static async Task SetMultipartUploadBodyAsync(
        DefaultHttpContext context,
        Guid providerId,
        string path,
        string fileName,
        string content)
    {
        using var multipart = new MultipartFormDataContent("file-endpoint-boundary");
        multipart.Add(new StringContent(providerId.ToString()), "providerId");
        multipart.Add(new StringContent(path), "path");
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(fileContent, "file", fileName);

        var bytes = await multipart.ReadAsByteArrayAsync();
        context.Request.ContentType = multipart.Headers.ContentType?.ToString();
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
    }

    private static ProviderFileItem ProviderItem(string externalFileId, string path, string name)
        => new(
            externalFileId,
            null,
            path,
            name,
            "file",
            "text/plain",
            100,
            "etag-1",
            "RGDNVW",
            DateTimeOffset.UtcNow);

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string value) => $"protected:{value}";

        public string Unprotect(string protectedValue)
            => protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue["protected:".Length..]
                : protectedValue;
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class EndpointFakeFileProviderAdapter : IFileProviderAdapter
    {
        public ProviderFileItem MoveResult { get; set; } = ProviderItem("file-1", "/Archive/report.txt", "report.txt");
        public ProviderFileItem UploadResult { get; set; } = ProviderItem("uploaded-file", "/Uploads/upload.txt", "upload.txt");
        public int MoveCallCount { get; private set; }
        public int UploadCallCount { get; private set; }
        public int RestoreTrashCallCount { get; private set; }
        public string? LastMoveSourcePath { get; private set; }
        public string? LastMoveDestinationPath { get; private set; }
        public string? LastUploadDestinationPath { get; private set; }
        public string? LastUploadContentType { get; private set; }
        public string? LastUploadContent { get; private set; }
        public Guid? LastRestoreTrashProviderId { get; private set; }
        public string? LastRestoreTrashId { get; private set; }

        public Task<FileProviderTestResult> TestConnectionAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
            => Task.FromResult(new FileProviderTestResult(true, "connected", null));

        public Task<IReadOnlyList<ProviderFileItem>> ListFolderAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderFileItem>>([]);

        public Task<ProviderFileItem> GetMetadataAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => Task.FromResult(MoveResult);

        public async Task<ProviderFileItem> UploadAsync(
            FileProviderConnection connection,
            string destinationPath,
            Stream content,
            string contentType,
            CancellationToken ct = default)
        {
            using var reader = new StreamReader(content, leaveOpen: true);
            LastUploadContent = await reader.ReadToEndAsync(ct);
            LastUploadDestinationPath = destinationPath;
            LastUploadContentType = contentType;
            UploadCallCount++;
            return UploadResult;
        }

        public Task<ProviderDownload> DownloadAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => Task.FromResult(new ProviderDownload(new MemoryStream(), "application/octet-stream", "download.bin"));

        public Task<ProviderFileItem> MoveAsync(
            FileProviderConnection connection,
            string sourcePath,
            string destinationPath,
            CancellationToken ct = default)
        {
            LastMoveSourcePath = sourcePath;
            LastMoveDestinationPath = destinationPath;
            MoveCallCount++;
            return Task.FromResult(MoveResult);
        }

        public Task<ProviderFileItem> RenameAsync(
            FileProviderConnection connection,
            string sourcePath,
            string name,
            CancellationToken ct = default)
            => Task.FromResult(MoveResult);

        public Task DeleteToTrashAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ProviderTrashItem>> ListTrashAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderTrashItem>>([]);

        public Task RestoreTrashAsync(
            FileProviderConnection connection,
            string trashId,
            CancellationToken ct = default)
        {
            LastRestoreTrashProviderId = connection.ProviderId;
            LastRestoreTrashId = trashId;
            RestoreTrashCallCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProviderFileVersion>> ListVersionsAsync(
            FileProviderConnection connection,
            string externalFileId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderFileVersion>>([]);

        public Task<ProviderDownload> DownloadVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            string fileName,
            CancellationToken ct = default)
            => Task.FromResult(new ProviderDownload(new MemoryStream(), "application/octet-stream", fileName));

        public Task RestoreVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public ProviderOpenLink BuildOpenLink(
            FileProviderConnection connection,
            string path,
            string mode,
            string? externalFileId = null)
            => new($"https://cloud.example.test/{path.TrimStart('/')}", mode);
    }
}
