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
using Pim.Core.Storage;
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
        services.AddScoped<FileSearchService>();
        // 文本抽取后端（可选）：只服务 read_file_text 的 pdf 等分支。
        // Tika 服务本身随 P4 从 compose 移除；未配置时 read_file_text 明确返回 5336。
        services.AddScoped<IFileTextExtractionService, TikaFileTextExtractionService>();

        // OneDrive（Graph 直链版，见 designs/onedrive-files-v2.md）
        services.AddSingleton<SensitivePathPolicy>();
        services.AddSingleton<OneDriveSyncGate>();
        services.AddSingleton<OneDriveTokenCache>();
        services.AddSingleton<OneDriveTransientRateLimiter>();
        services.AddScoped<OneDriveTextExtractor>(sp => new OneDriveTextExtractor(
            // pdf 等非文本类型只能靠 Tika；不传的话 read_file_text 对 pdf 永远报「不支持」
            sp.GetService<IFileTextExtractionService>(),
            sp.GetService<ILogger<OneDriveTextExtractor>>()));
        services.AddScoped<OneDriveContentService>();
        services.AddScoped<OneDriveWriteService>();
        // QuickNotes 等模块经此把附件存入用户自己的 OneDrive（设计文档 §10）
        services.AddScoped<IOneDriveAttachmentStore, OneDriveAttachmentStore>();
        // OneDriveGraphClient 的构造函数收的是 IHttpClientFactory + IConfiguration（它自己
        // CreateClient("onedrive-graph")），不能注册成 AddHttpClient<T> 的类型化客户端：
        // ActivatorUtilities 要求类型化客户端的构造函数能接收 HttpClient，这里的构造函数没有
        // 这个参数位，解析时会抛「A suitable constructor ... could not be located」，
        // 进而让所有 OneDrive 服务（绑定/同步/内容/写）全部 500。
        // 正确做法：注册类型本身 + 用同名命名客户端承载超时策略（复审 C-1）。
        services.AddHttpClient(OneDriveGraphClient.HttpClientName, client =>
        {
            // Graph 挂起时不占满默认 100s 请求周期（复审 M-11）
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<OneDriveGraphClient>();
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

        group.MapPost("/providers/onedrive", StartOneDriveBindingAsync);
        group.MapGet("/providers/{id:guid}/binding-status", GetOneDriveBindingStatusAsync);
        group.MapDelete("/providers/{id:guid}", DisconnectProviderAsync);

        group.MapPost("/providers/{id:guid}/sync", SyncProviderAsync);
        group.MapGet("/items", ListItemsAsync);
        group.MapGet("/items/{id:guid}", GetItemAsync);
        group.MapPost("/items/upload", UploadItemAsync);
        group.MapGet("/items/{id:guid}/download", DownloadItemAsync);
        group.MapPost("/items/{id:guid}/move", MoveItemAsync);
        group.MapPost("/items/{id:guid}/rename", RenameItemAsync);
        group.MapDelete("/items/{id:guid}", DeleteItemAsync);
        group.MapGet("/items/{id:guid}/content", OneDriveContentAsync);
        group.MapGet("/items/{id:guid}/thumbnail", OneDriveThumbnailAsync);
        group.MapGet("/items/{id:guid}/preview-url", OneDrivePreviewUrlAsync);
        group.MapGet("/items/{id:guid}/text", OneDriveGetTextAsync);
        group.MapPut("/items/{id:guid}/text", OneDriveSaveTextAsync);
        group.MapGet("/items/{id:guid}/snapshots", OneDriveListSnapshotsAsync);
        group.MapPost("/items/{id:guid}/snapshots/{snapshotId:guid}/restore", OneDriveRestoreSnapshotAsync);
        // read_file_text（MCP）与 item 级恢复此前只有处理器、没有路由，工具调用恒 404（复审 C-3/C-4）
        group.MapGet("/items/{id:guid}/extracted-text", OneDriveReadTextAsync);
        group.MapPost("/items/{id:guid}/restore", OneDriveRestoreItemAsync);
        group.MapGet("/search", SearchAsync);
        group.MapGet("/suggestions", ListSuggestionsAsync);
        group.MapPost("/suggestions/{id:guid}/dismiss", DismissSuggestionAsync);
        group.MapPost("/suggestions/{id:guid}/accept", AcceptSuggestionAsync);
        group.MapGet("/items/{id:guid}/open-link", BuildOpenLinkAsync);
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
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

        if (provider.Provider != "onedrive")
        {
            // Nextcloud 等遗留来源随 P4 退役
            throw new DomainException(5334, "该文件来源已退役，请使用 OneDrive");
        }

        var result = await oneDriveService.SyncAsync(id, ct);
        return Results.Ok(ApiResponse<OneDriveSyncResultDto>.Ok(OneDriveSyncResultDto.From(result)));
    }

    private static async Task<IResult> ListItemsAsync(
        [FromQuery] string? path,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] string? order,
        [FromQuery] string? type,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileListResponse>.Ok(new FileListResponse(
            await service.ListItemsAsync(
                new FileListQuery(path, q, sort, order, type),
                page ?? 1,
                pageSize ?? 50,
                ct))));

    private static async Task<IResult> GetItemAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileItemDto>.Ok(await service.GetItemAsync(id, ct)));

    private static async Task<IResult> UploadItemAsync(
        HttpRequest request,
        [FromServices] OneDriveWriteService oneDriveWrite,
        [FromServices] PimDbContext db,
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

        // v2 只有 OneDrive 一种来源：上传经 Graph 写入并收敛本地元数据。
        // （此前该端点直接落到 Nextcloud 适配器，对 OneDrive 恒抛 5334，上传功能不可用。）
        await EnsureOneDriveProviderAsync(db, providerId, ct);

        var normalized = FileOperationService.NormalizePath(path);
        var lastSlash = normalized.LastIndexOf('/');
        var fileName = lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
        var folderPath = lastSlash <= 0 ? "/" : normalized[..lastSlash];
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new DomainException(5301, "目标路径必须包含文件或文件夹名称");
        }

        using var content = file.OpenReadStream();
        var result = await oneDriveWrite.UploadAsync(folderPath, fileName, content, contentType, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await BuildUploadedItemDtoAsync(db, result.ItemId, ct)));
    }

    /// <summary>上传只支持 OneDrive；其余（已退役的 Nextcloud）来源给出明确错误。</summary>
    private static async Task EnsureOneDriveProviderAsync(PimDbContext db, Guid providerId, CancellationToken ct)
    {
        var provider = await db.Set<FileProviderEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == providerId, ct)
            ?? throw new DomainException(5104, "文件来源不存在");
        if (provider.Provider != "onedrive")
        {
            throw new DomainException(5334, "该文件来源已退役，请使用 OneDrive");
        }
    }

    private static async Task<FileItemDto> BuildUploadedItemDtoAsync(PimDbContext db, Guid itemId, CancellationToken ct)
    {
        var item = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(row => row.IndexJobs)
            .FirstOrDefaultAsync(row => row.Id == itemId, ct)
            ?? throw new DomainException(5300, "文件不存在");
        return FileItemMapper.Map(item);
    }

    private static async Task<IResult> DownloadItemAsync(
        Guid id,
        [FromServices] OneDriveContentService service,
        CancellationToken ct)
        // 稳定直链：302 到 Graph 预授权 URL（复用内容出口的登录/归属/敏感路径三道闸）
        => Results.Redirect(await service.GetContentLinkAsync(id, ct));

    private static async Task<IResult> MoveItemAsync(
        Guid id,
        [FromBody] MoveFileRequest request,
        [FromServices] OneDriveWriteService oneDriveWrite,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        var result = await oneDriveWrite.MoveAsync(id, request.DestinationPath, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await FileOperationService.GetItemDtoAsync(db, id, ct)));
    }

    private static async Task<IResult> RenameItemAsync(
        Guid id,
        [FromBody] RenameFileRequest request,
        [FromServices] OneDriveWriteService oneDriveWrite,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        await oneDriveWrite.RenameAsync(id, request.Name, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await FileOperationService.GetItemDtoAsync(db, id, ct)));
    }

    private static async Task<IResult> DeleteItemAsync(
        Guid id,
        [FromServices] OneDriveWriteService oneDriveWrite,
        CancellationToken ct)
    {
        await oneDriveWrite.DeleteToTrashAsync(id, ct);
        // 个人版没有回收站 API（设计 §14-V5），Graph DELETE 后远端即不可见，
        // 因此不能承诺「可恢复」——诚实说明可在 OneDrive 网页回收站自行还原（复审 I-7）。
        return Results.Ok(ApiResponse<string>.Ok(
            "已删除（已移入 OneDrive 回收站；如需还原请在 OneDrive 网页版操作）"));
    }




    private static async Task<IResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? mode,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] FileSearchService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileSearchResultDto>.Ok(
            // MCP 合约里 search_files 声明了 page/pageSize，这里必须真正生效（复审发现）
            await service.SearchAsync(new FileSearchQuery(q, mode), page ?? 1, pageSize ?? 20, ct)));

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
        [FromServices] OneDriveWriteService oneDriveWrite,
        CancellationToken ct)
    {
        var webUrl = await oneDriveWrite.GetWebUrlAsync(id, ct);
        return Results.Ok(ApiResponse<FileOpenLinkDto>.Ok(new FileOpenLinkDto(webUrl, "onedrive-web")));
    }


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
