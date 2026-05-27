using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Exceptions;
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
        services.AddScoped<FileIndexingService>();
        services.AddSingleton<IFileEmbeddingService, HashingFileEmbeddingService>();
        services.AddScoped<IFileTextExtractionService, TikaFileTextExtractionService>();
        services.AddHttpClient<NextcloudFileProviderAdapter>();
        services.AddHttpClient<QdrantFileVectorStore>();
        services.AddScoped<IFileVectorStore>(sp => sp.GetRequiredService<QdrantFileVectorStore>());
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
        group.MapPost("/providers/{id:guid}/sync", SyncProviderAsync);
        group.MapGet("/items", ListItemsAsync);
        group.MapGet("/items/{id:guid}", GetItemAsync);
        group.MapPost("/items/upload", UploadItemAsync);
        group.MapGet("/items/{id:guid}/download", DownloadItemAsync);
        group.MapPost("/items/{id:guid}/move", MoveItemAsync);
        group.MapPost("/items/{id:guid}/rename", RenameItemAsync);
        group.MapDelete("/items/{id:guid}", DeleteItemAsync);
        group.MapGet("/trash", ListTrashAsync);
        group.MapPost("/trash/{id:guid}/restore", RestoreTrashAsync);
        group.MapGet("/items/{id:guid}/versions", ListVersionsAsync);
        group.MapGet("/items/{id:guid}/versions/{versionId:guid}/download", DownloadVersionAsync);
        group.MapPost("/items/{id:guid}/versions/{versionId:guid}/restore-preview", PreviewVersionRestoreAsync);
        group.MapPost("/items/{id:guid}/versions/{versionId:guid}/restore", RestoreVersionAsync);
        group.MapPost("/items/{id:guid}/index", IndexItemAsync);
        group.MapGet("/search", SearchAsync);
        group.MapGet("/suggestions", ListSuggestionsAsync);
        group.MapPost("/suggestions/{id:guid}/dismiss", DismissSuggestionAsync);
        group.MapPost("/suggestions/{id:guid}/accept", AcceptSuggestionAsync);
        group.MapGet("/items/{id:guid}/open-link", BuildOpenLinkAsync);
    }

    public Task InitializeAsync(IServiceProvider serviceProvider) => Task.CompletedTask;

    private static async Task<IResult> SyncProviderAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<FileItemDto>>.Ok(await service.SyncProviderAsync(id, ct)));

    private static async Task<IResult> ListItemsAsync(
        [FromQuery] string? path,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileListResponse>.Ok(new FileListResponse(
            await service.ListItemsAsync(new FileListQuery(path), ct))));

    private static async Task<IResult> GetItemAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileItemDto>.Ok(await service.GetItemAsync(id, ct)));

    private static async Task<IResult> UploadItemAsync(
        HttpRequest request,
        [FromServices] FileOperationService service,
        CancellationToken ct)
    {
        if (!request.HasFormContentType)
            throw new DomainException(5306, "Multipart form data is required");

        var form = await request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["providerId"].FirstOrDefault(), out var providerId))
            throw new DomainException(5307, "Provider id is required");

        var path = form["path"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
            throw new DomainException(5308, "Upload path is required");

        var file = form.Files.GetFile("file")
            ?? throw new DomainException(5309, "Upload file is required");
        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;

        using var content = file.OpenReadStream();
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await service.UploadAsync(providerId, path, content, contentType, ct)));
    }

    private static async Task<IResult> DownloadItemAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
    {
        var download = await service.DownloadAsync(id, ct);
        return Results.File(download.Content, download.ContentType, download.FileName);
    }

    private static async Task<IResult> MoveItemAsync(
        Guid id,
        [FromBody] MoveFileRequest request,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileItemDto>.Ok(await service.MoveAsync(id, request, ct)));

    private static async Task<IResult> RenameItemAsync(
        Guid id,
        [FromBody] RenameFileRequest request,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileItemDto>.Ok(await service.RenameAsync(id, request, ct)));

    private static async Task<IResult> DeleteItemAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);
        return Results.Ok(ApiResponse<string>.Ok("deleted"));
    }

    private static async Task<IResult> ListTrashAsync(
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<ProviderTrashItem>>.Ok(await service.ListTrashAsync(ct)));

    private static async Task<IResult> RestoreTrashAsync(
        Guid id,
        [FromQuery] string? trashId,
        [FromServices] FileOperationService service,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trashId))
            throw new DomainException(5310, "Trash id is required");

        await service.RestoreTrashAsync(id, trashId, ct);
        return Results.Ok(ApiResponse<string>.Ok("restored"));
    }

    private static async Task<IResult> ListVersionsAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<FileVersionDto>>.Ok(await service.ListVersionsAsync(id, ct)));

    private static async Task<IResult> DownloadVersionAsync(
        Guid id,
        Guid versionId,
        [FromServices] FileOperationService service,
        CancellationToken ct)
    {
        var download = await service.DownloadVersionAsync(id, versionId, ct);
        return Results.File(download.Content, download.ContentType, download.FileName);
    }

    private static async Task<IResult> PreviewVersionRestoreAsync(
        Guid id,
        Guid versionId,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<VersionRestorePreviewDto>.Ok(
            await service.RestoreVersionPreviewAsync(id, versionId, ct)));

    private static async Task<IResult> RestoreVersionAsync(
        Guid id,
        Guid versionId,
        [FromServices] FileOperationService service,
        CancellationToken ct)
    {
        await service.RestoreVersionAsync(id, versionId, ct);
        return Results.Ok(ApiResponse<string>.Ok("restored"));
    }

    private static async Task<IResult> IndexItemAsync(
        Guid id,
        [FromServices] FileIndexingService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileIndexJobDto>.Ok(await service.IndexCurrentVersionAsync(id, ct)));

    private static async Task<IResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? mode,
        [FromServices] FileIndexingService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileSearchResultDto>.Ok(await service.SearchAsync(new FileSearchQuery(q, mode), ct)));

    private static async Task<IResult> ListSuggestionsAsync(
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<FileSuggestionDto>>.Ok(await service.ListSuggestionsAsync(ct)));

    private static async Task<IResult> DismissSuggestionAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileSuggestionDto>.Ok(await service.DismissSuggestionAsync(id, ct)));

    private static async Task<IResult> AcceptSuggestionAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileSuggestionDto>.Ok(await service.AcceptSuggestionAsync(id, ct)));

    private static async Task<IResult> BuildOpenLinkAsync(
        Guid id,
        [FromQuery] string? mode,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileOpenLinkDto>.Ok(await service.BuildOpenLinkAsync(id, mode, ct)));

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
