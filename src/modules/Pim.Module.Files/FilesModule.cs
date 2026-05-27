using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;

namespace Pim.Module.Files;

public sealed class FilesModule : IModule
{
    public string Name => "files";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.AddScoped<FileProviderBindingService>();
        services.AddScoped<FileOperationService>();
        services.AddHttpClient<NextcloudFileProviderAdapter>();
        services.AddScoped<IFileProviderAdapter>(sp => sp.GetRequiredService<NextcloudFileProviderAdapter>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(FileEndpointPaths.Root).RequireAuthorization();

        group.MapGet("/providers", async (
            [FromServices] FileProviderBindingService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<FileProviderDto>>.Ok(await service.ListProvidersAsync(ct))));

        group.MapPost("/providers/nextcloud", async (
            [FromBody] BindNextcloudProviderRequest request,
            [FromServices] FileProviderBindingService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<FileProviderDto>.Ok(await service.BindNextcloudAsync(request, ct))));

        group.MapPost("/providers/{id:guid}/test", async (
            Guid id,
            [FromServices] FileProviderBindingService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<FileProviderTestDto>.Ok(await service.TestProviderAsync(id, ct))));
        group.MapPost("/providers/{id:guid}/sync", (Guid id) => NotImplemented());
        group.MapGet("/items", ([FromQuery] string? path) => NotImplemented());
        group.MapGet("/items/{id:guid}", (Guid id) => NotImplemented());
        group.MapPost("/items/upload", (HttpRequest request) => NotImplemented());
        group.MapGet("/items/{id:guid}/download", (Guid id) => NotImplemented());
        group.MapPost("/items/{id:guid}/move", (Guid id, [FromBody] MoveFileRequest request) => NotImplemented());
        group.MapPost("/items/{id:guid}/rename", (Guid id, [FromBody] RenameFileRequest request) => NotImplemented());
        group.MapDelete("/items/{id:guid}", (Guid id) => NotImplemented());
        group.MapGet("/trash", () => NotImplemented());
        group.MapPost("/trash/{id:guid}/restore", (Guid id) => NotImplemented());
        group.MapGet("/items/{id:guid}/versions", (Guid id) => NotImplemented());
        group.MapGet("/items/{id:guid}/versions/{versionId:guid}/download", (Guid id, Guid versionId) => NotImplemented());
        group.MapPost("/items/{id:guid}/versions/{versionId:guid}/restore-preview", (Guid id, Guid versionId) => NotImplemented());
        group.MapPost("/items/{id:guid}/versions/{versionId:guid}/restore", (Guid id, Guid versionId) => NotImplemented());
        group.MapPost("/items/{id:guid}/index", (Guid id) => NotImplemented());
        group.MapGet("/search", ([FromQuery] string? q, [FromQuery] string? mode) => NotImplemented());
        group.MapGet("/suggestions", () => NotImplemented());
        group.MapPost("/suggestions/{id:guid}/dismiss", (Guid id) => NotImplemented());
        group.MapPost("/suggestions/{id:guid}/accept", (Guid id) => NotImplemented());
        group.MapGet("/items/{id:guid}/open-link", (Guid id, [FromQuery] string? mode) => NotImplemented());
    }

    public Task InitializeAsync(IServiceProvider serviceProvider) => Task.CompletedTask;

    private static IResult NotImplemented()
        => Results.Json(ApiResponse<string>.Error(501, "Files module endpoint is not implemented yet"), statusCode: 501);
}

public static class FileEndpointPaths
{
    public const string Root = "/api/v1/files";
    public const string Providers = $"{Root}/providers";
    public const string NextcloudProviders = $"{Providers}/nextcloud";

    public static string ProviderTest(string id) => $"{Providers}/{id}/test";
    public static string ProviderSync(string id) => $"{Providers}/{id}/sync";
    public static string Item(string id) => $"{Root}/items/{id}";
    public static string ItemDownload(string id) => $"{Item(id)}/download";
    public static string VersionRestore(string id, string versionId) => $"{Item(id)}/versions/{versionId}/restore";
}
