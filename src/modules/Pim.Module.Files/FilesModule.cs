using System.Reflection;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Common;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
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
        services.AddScoped<FileAiService>();
        services.AddHttpClient<OpenAiFileEmbeddingService>();
        services.AddSingleton<IFileEmbeddingService>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenAiFileEmbeddingService));
            var config = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetRequiredService<ILogger<OpenAiFileEmbeddingService>>();
            // prefer real embedding when key present, but service itself falls back to hashing
            return new OpenAiFileEmbeddingService(http, config, logger);
        });
        services.AddScoped<IFileTextExtractionService, TikaFileTextExtractionService>();
        services.AddHttpClient<NextcloudFileProviderAdapter>();
        services.AddHttpClient<QdrantFileVectorStore>();
        services.AddScoped<IFileVectorStore>(sp => sp.GetRequiredService<QdrantFileVectorStore>());
        services.AddScoped<IFileProviderAdapter>(sp => sp.GetRequiredService<NextcloudFileProviderAdapter>());

        // OneDrive（Graph 直链版，见 designs/onedrive-files-v2.md）
        services.AddSingleton<SensitivePathPolicy>();
        services.AddSingleton<OneDriveSyncGate>();
        services.AddSingleton<OneDriveTokenCache>();
        services.AddScoped<OneDriveContentService>();
        services.AddHttpClient<OneDriveGraphClient>(client =>
        {
            // Graph 挂起时不占满默认 100s 请求周期（复审 M-11）
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IOneDriveGraphClient>(sp => sp.GetRequiredService<OneDriveGraphClient>());
        services.AddScoped<OneDriveTokenService>();
        services.AddScoped<OneDriveBindingService>();
        services.AddScoped<OneDriveSyncService>();
        services.AddScoped<OneDriveSyncJob>();
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

        group.MapPost("/providers/onedrive", StartOneDriveBindingAsync);
        group.MapGet("/providers/{id:guid}/binding-status", GetOneDriveBindingStatusAsync);
        group.MapDelete("/providers/{id:guid}", DisconnectProviderAsync);

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
        group.MapGet("/items/{id:guid}/content", OneDriveContentAsync);
        group.MapGet("/items/{id:guid}/thumbnail", OneDriveThumbnailAsync);
        group.MapGet("/items/{id:guid}/preview-url", OneDrivePreviewUrlAsync);
        group.MapGet("/items/{id:guid}/text", OneDriveGetTextAsync);
        group.MapPut("/items/{id:guid}/text", OneDriveSaveTextAsync);
        group.MapGet("/items/{id:guid}/snapshots", OneDriveListSnapshotsAsync);
        group.MapPost("/items/{id:guid}/snapshots/{snapshotId:guid}/restore", OneDriveRestoreSnapshotAsync);
        group.MapPost("/items/{id:guid}/index", IndexItemAsync);
        group.MapGet("/search", SearchAsync);
        group.MapGet("/suggestions", ListSuggestionsAsync);
        group.MapPost("/suggestions/{id:guid}/dismiss", DismissSuggestionAsync);
        group.MapPost("/suggestions/{id:guid}/accept", AcceptSuggestionAsync);
        group.MapGet("/items/{id:guid}/open-link", BuildOpenLinkAsync);
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        var registry = serviceProvider.GetService<IAiSchemaRegistry>();
        if (registry is not null)
            FileAiService.RegisterSchemas(registry);

        // 进程崩溃/重启会留下永久 "syncing"：启动时统一复位为可感知的中断态（设计 §6）
        try
        {
            using var scope = serviceProvider.CreateScope();
            var startupDb = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var stuck = await startupDb.Set<FileProviderEntity>()
                .Where(provider => provider.SyncStatus == "syncing")
                .ToListAsync();
            foreach (var provider in stuck)
            {
                provider.SyncStatus = "error";
                provider.LastError = "进程重启中断了上次同步，将自动重试";
                provider.UpdatedAt = DateTimeOffset.UtcNow;
            }
            if (stuck.Count > 0)
                await startupDb.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            serviceProvider.GetService<ILogger<FilesModule>>()?.LogWarning(
                exception, "Failed to reset stale syncing providers at startup.");
        }

        var jobClient = serviceProvider.GetService<IBackgroundJobClient>();
        var recurringJobs = serviceProvider.GetService<IRecurringJobManager>();
        var logger = serviceProvider.GetService<ILogger<FilesModule>>();
        if (jobClient is null || recurringJobs is null)
        {
            logger?.LogWarning(
                "Background job infrastructure is not available; OneDrive scheduled sync is disabled.");
            return;
        }

        try
        {
            jobClient.Enqueue<OneDriveSyncJob>(job => job.RunAllAsync());
            recurringJobs.AddOrUpdate<OneDriveSyncJob>(
                "onedrive-files-sync",
                job => job.RunAllAsync(),
                "*/20 * * * *");
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Failed to schedule the recurring OneDrive sync job.");
        }
    }

    private static async Task<IResult> OneDriveContentAsync(
        Guid id,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
        => Results.Redirect(await service.GetContentLinkAsync(id, ct));

    private static async Task<IResult> OneDriveThumbnailAsync(
        Guid id,
        [FromQuery] string? size,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
        => Results.Redirect(await service.GetThumbnailLinkAsync(id, string.IsNullOrWhiteSpace(size) ? "medium" : size, ct));

    private static async Task<IResult> OneDrivePreviewUrlAsync(
        Guid id,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<OneDriveLinkDto>.Ok(new OneDriveLinkDto(await service.GetPreviewLinkAsync(id, ct))));

    private static async Task<IResult> OneDriveGetTextAsync(
        Guid id,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<OneDriveTextDto>.Ok(OneDriveTextDto.From(await service.GetTextAsync(id, ct))));

    private static async Task<IResult> OneDriveSaveTextAsync(
        Guid id,
        [FromBody] SaveOneDriveTextRequest request,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
    {
        await service.SaveTextAsync(id, request.Content, ct);
        return Results.Ok(ApiResponse<bool>.Ok(true));
    }

    private static async Task<IResult> OneDriveListSnapshotsAsync(
        Guid id,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<FileTextSnapshotDto>>.Ok(await service.ListSnapshotsAsync(id, ct)));

    private static async Task<IResult> OneDriveRestoreSnapshotAsync(
        Guid id,
        Guid snapshotId,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
    {
        await service.RestoreSnapshotAsync(id, snapshotId, ct);
        return Results.Ok(ApiResponse<bool>.Ok(true));
    }

    private static async Task<IResult> StartOneDriveBindingAsync(
        [FromBody] StartOneDriveBindingRequest request,
        [FromServices] OneDriveBindingService service,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken ct)
        => Results.Ok(ApiResponse<OneDriveBindingStartDto>.Ok(OneDriveBindingStartDto.From(
            await service.StartBindingAsync(RequireUserId(currentUser), request.ClientId, ct))));

    private static async Task<IResult> GetOneDriveBindingStatusAsync(
        Guid id,
        [FromServices] OneDriveBindingService service,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken ct)
        => Results.Ok(ApiResponse<OneDriveBindingStatusDto>.Ok(OneDriveBindingStatusDto.From(
            await service.GetBindingStatusAsync(RequireUserId(currentUser), id, ct))));

    private static async Task<IResult> DisconnectProviderAsync(
        Guid id,
        [FromServices] OneDriveBindingService service,
        [FromServices] OneDriveTokenService tokens,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken ct)
    {
        await service.DisconnectAsync(RequireUserId(currentUser), id, ct);
        tokens.InvalidateCached(id);
        return Results.Ok(ApiResponse<bool>.Ok(true));
    }

    private static Guid RequireUserId(ICurrentUserService currentUser)
        => currentUser.UserId ?? throw new DomainException(01002, "Login required");

    private static async Task<IResult> SyncProviderAsync(
        Guid id,
        [FromServices] FileOperationService service,
        [FromServices] OneDriveSyncService oneDriveService,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        // 全局用户过滤器保证只能看到自己的 provider
        var provider = await db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.Provider })
            .FirstOrDefaultAsync();
        if (provider is null)
            throw new DomainException(5104, "文件来源不存在");

        if (provider.Provider == "onedrive")
        {
            var result = await oneDriveService.SyncAsync(id, ct);
            return Results.Ok(ApiResponse<OneDriveSyncResultDto>.Ok(OneDriveSyncResultDto.From(result)));
        }

        return Results.Ok(ApiResponse<IReadOnlyList<FileItemDto>>.Ok(await service.SyncProviderAsync(id, ct)));
    }

    private static async Task<IResult> ListItemsAsync(
        [FromQuery] string? path,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileListResponse>.Ok(new FileListResponse(
            await service.ListItemsAsync(new FileListQuery(path), page ?? 1, pageSize ?? 50, ct))));

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
            throw new DomainException(5306, "需要 multipart 表单数据");

        var form = await request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["providerId"].FirstOrDefault(), out var providerId))
            throw new DomainException(5307, "需要文件来源 ID");

        var path = form["path"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
            throw new DomainException(5308, "需要上传路径");

        var file = form.Files.GetFile("file")
            ?? throw new DomainException(5309, "需要上传文件");
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
        [FromServices] OneDriveWriteService oneDriveWrite,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        if (await IsOneDriveItemAsync(db, id, ct))
        {
            var result = await oneDriveWrite.MoveAsync(id, request.DestinationPath, ct);
            return Results.Ok(ApiResponse<FileItemDto>.Ok(await service.GetItemAsync(id, ct)));
        }

        return Results.Ok(ApiResponse<FileItemDto>.Ok(await service.MoveAsync(id, request, ct)));
    }

    private static async Task<IResult> RenameItemAsync(
        Guid id,
        [FromBody] RenameFileRequest request,
        [FromServices] FileOperationService service,
        [FromServices] OneDriveWriteService oneDriveWrite,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        if (await IsOneDriveItemAsync(db, id, ct))
        {
            await oneDriveWrite.RenameAsync(id, request.Name, ct);
            return Results.Ok(ApiResponse<FileItemDto>.Ok(await service.GetItemAsync(id, ct)));
        }

        return Results.Ok(ApiResponse<FileItemDto>.Ok(await service.RenameAsync(id, request, ct)));
    }

    private static async Task<IResult> DeleteItemAsync(
        Guid id,
        [FromServices] FileOperationService service,
        [FromServices] OneDriveWriteService oneDriveWrite,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        if (await IsOneDriveItemAsync(db, id, ct))
        {
            await oneDriveWrite.DeleteToTrashAsync(id, ct);
            return Results.Ok(ApiResponse<string>.Ok("已删除（文件移入 OneDrive 回收站，PIM 内可尝试恢复）"));
        }

        await service.DeleteAsync(id, ct);
        return Results.Ok(ApiResponse<string>.Ok("已删除"));
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
            throw new DomainException(5310, "需要回收站 ID");

        await service.RestoreTrashAsync(id, trashId, ct);
        return Results.Ok(ApiResponse<string>.Ok("已恢复"));
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
        return Results.Ok(ApiResponse<string>.Ok("已恢复"));
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
        [FromServices] OneDriveWriteService oneDriveWrite,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        if (await IsOneDriveItemAsync(db, id, ct))
        {
            var webUrl = await oneDriveWrite.GetWebUrlAsync(id, ct);
            return Results.Ok(ApiResponse<FileOpenLinkDto>.Ok(new FileOpenLinkDto(webUrl, "onedrive-web")));
        }

        return Results.Ok(ApiResponse<FileOpenLinkDto>.Ok(await service.BuildOpenLinkAsync(id, mode, ct)));
    }

    private static async Task<bool> IsOneDriveItemAsync(PimDbContext db, Guid itemId, CancellationToken ct)
        => await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(item => item.Provider)
            .AnyAsync(item => item.Id == itemId && item.Provider != null && item.Provider.Provider == "onedrive", ct);

    private static async Task<IResult> OneDriveReadTextAsync(
        Guid id,
        [FromQuery] long? maxBytes,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
    {
        var text = await service.ReadTextAsync(id, maxBytes, ct);
        return Results.Ok(ApiResponse<OneDriveTextDto>.Ok(OneDriveTextDto.From(text)));
    }

    private static async Task<IResult> OneDriveRestoreItemAsync(
        Guid id,
        [FromServices] OneDriveWriteService service,
        CancellationToken ct)
    {
        var result = await service.RestoreAsync(id, ct);
        return Results.Ok(ApiResponse<OneDriveWriteResultDto>.Ok(
            new OneDriveWriteResultDto(result.ItemId, result.Path)));
    }

    private static IResult NotImplemented()
        => Results.Json(ApiResponse<string>.Error(501, "文件模块端点尚未实现"), statusCode: 501);
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
