using System.Reflection;
using System.Text;
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
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Providers.Google;
using Pim.Module.Files.Providers.Local;
using Pim.Infrastructure.Storage;
using Pim.Module.Files.Services;

namespace Pim.Module.Files;

public sealed class FilesModule : IModule
{
    public string Name => "files";
    public string Version => "2.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.AddScoped<FileProviderBindingService>();
        services.Configure<ManagedBinaryOptions>(configuration.GetSection("ManagedStorage"));
        services.AddSingleton<ManagedBinaryStore>();
        services.AddSingleton<LocalFileStore>();
        services.AddScoped<LocalFileGrants>();
        services.AddScoped<IFileProvider, LocalFileProvider>();
        services.AddScoped<FileOperationService>();
        services.AddScoped<FileSearchService>();
        // 文本抽取后端（可选）：只服务 read_file_text 的 pdf 等分支。
        // Tika 服务本身随 P4 从 compose 移除；未配置时 read_file_text 明确返回 5336。
        services.AddScoped<IFileTextExtractionService, TikaFileTextExtractionService>();

        // ---- 通用 provider 抽象（工单 05 / F1）----
        services.AddSingleton<SensitivePathPolicy>();
        services.AddSingleton<FileProviderSyncGate>();
        services.AddSingleton<FileProviderTokenCache>();
        services.AddSingleton<GoogleRootIdCache>();
        services.AddSingleton(sp => new FileTransientRateLimiter(sp.GetService<TimeProvider>()));
        services.AddSingleton<FileProviderCredentialSource>();
        services.AddScoped<IFileProviderFactory, FileProviderFactory>();
        services.AddScoped<FileProviderTokenService>();
        services.AddScoped<FileProviderSessionFactory>();
        services.AddScoped<FileProviderAuthorizationService>();
        services.AddScoped<FileTreeSyncEngine>();
        services.AddScoped<FileTextExtractor>(sp => new FileTextExtractor(
            // pdf 等非文本类型只能靠 Tika；不传的话 read_file_text 对 pdf 永远报「不支持」
            sp.GetService<IFileTextExtractionService>(),
            sp.GetService<ILogger<FileTextExtractor>>()));
        services.AddScoped<FileContentService>();
        services.AddScoped<FileWriteService>();
        services.AddScoped<FileShareService>();
        // QuickNotes 等模块经此把附件存入用户自己的网盘
        services.AddScoped<IFileAttachmentStore, FileAttachmentStore>();

        // ---- OneDrive（Graph）----
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
        // 取内容直链要读 302 的 Location，必须显式关掉自动跟随：
        // 默认 HttpClientHandler 会把 302 跟到 CDN，Location 就此丢失（issue #342 复审）。
        // 不能改上面那个共享客户端——DownloadSmallAsync 等内容出口依赖跟随语义。
        services.AddHttpClient(OneDriveGraphClient.NoRedirectHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });
        services.AddScoped<OneDriveGraphClient>();
        services.AddScoped<IOneDriveGraphClient>(sp => sp.GetRequiredService<OneDriveGraphClient>());
        services.AddScoped<IFileProvider, OneDriveFileProvider>();
        services.AddScoped<IFileProviderOAuthClient, OneDriveOAuthClient>();
        services.AddScoped<IFileProviderAuthorizer, OneDriveAuthorizer>();

        // ---- Google Drive（工单 05 / F4）----
        services.AddHttpClient(GoogleDriveClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<GoogleDriveClient>();
        services.AddScoped<IFileProvider, GoogleFileProvider>();
        services.AddScoped<IFileProviderOAuthClient, GoogleOAuthClient>();
        services.AddScoped<IFileProviderAuthorizer, GoogleAuthorizer>();

        services.AddScoped<FileSyncJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapLocalFileEndpoints();
        var group = endpoints.MapGroup(FileEndpointPaths.Root).RequireAuthorization();

        group.MapGet("/providers", async (
            [FromServices] FileProviderBindingService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<FileProviderDto>>.Ok(await service.ListProvidersAsync(ct))))
        .Produces<ApiResponse<IReadOnlyList<FileProviderDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(500);

        // OneDrive：设备码流（用户在浏览器输入 user code 同意，无回调端点）
        group.MapPost("/providers/onedrive", StartOneDriveBindingAsync)
        .Produces<ApiResponse<ProviderBindingStartDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);

        // Google：浏览器跳转授权（工单 F4.1）。开始端点返回授权地址，由前端 window.location 跳转。
        group.MapPost("/providers/google/authorize", StartGoogleAuthorizationAsync)
        .Produces<ApiResponse<ProviderBindingStartDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // Google 的浏览器跳转回调：**必须匿名**（浏览器 302 回来时没有 PIM 凭据），
        // 安全性靠一次性 state。放在授权组之外单独挂载。
        endpoints.MapGet(FileEndpointPaths.GoogleCallback, GoogleCallbackAsync)
            .AllowAnonymous()
            .Produces(200)
            .Produces(400);

        group.MapGet("/providers/{id:guid}/binding-status", GetBindingStatusAsync)
        .Produces<ApiResponse<ProviderBindingStatusDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapDelete("/providers/{id:guid}", DisconnectProviderAsync)
        .Produces<ApiResponse<bool>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        // F2：切换附件/闪念图片的默认落点账号
        group.MapPost("/providers/{id:guid}/default", SetDefaultProviderAsync)
        .Produces<ApiResponse<bool>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        group.MapPost("/providers/{id:guid}/sync", SyncProviderAsync)
        // 双形态 200：Hangfire 可用时返回 ProviderBindingStartDto 之外的 started 形态（常态）；
        // 后台设施不可用时退回同步执行并返回 FileSyncResultDto。
        // OpenAPI 单响应码只能承载一个 schema，这里标注常态形态。
        .Produces<ApiResponse<ProviderSyncStartedDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(503)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/providers/{id:guid}/sync-status", SyncStatusAsync)
        .Produces<ApiResponse<ProviderSyncStatusDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/items", ListItemsAsync)
        .Produces<ApiResponse<FileListResponse>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}", GetItemAsync)
        .Produces<ApiResponse<FileItemDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/items/upload", UploadItemAsync)
        .Produces<ApiResponse<FileItemDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/download", DownloadItemAsync)
        .Produces(302)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        // REQ-20 / AC-20.1：下载直链以 **JSON** 形式返回，页面 window.open 直链。
        // 不再让前端用 fetch 跟随 302 去「读 response.url」——那会把整个文件体真拉一遍。
        group.MapGet("/items/{id:guid}/download-url", DownloadUrlAsync)
        .Produces<ApiResponse<FileLinkDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/items/{id:guid}/move", MoveItemAsync)
        .Produces<ApiResponse<FileItemDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/items/{id:guid}/rename", RenameItemAsync)
        .Produces<ApiResponse<FileItemDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapDelete("/items/{id:guid}", DeleteItemAsync)
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/content", ContentAsync)
        .Produces(302)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/thumbnail", ThumbnailAsync)
        .Produces(302)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/preview-url", PreviewUrlAsync)
        .Produces<ApiResponse<FileLinkDto>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/text", GetTextAsync)
        .Produces<ApiResponse<FileTextDto>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapPut("/items/{id:guid}/text", SaveTextAsync)
        .Produces<ApiResponse<bool>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/snapshots", ListSnapshotsAsync)
        .Produces<ApiResponse<IReadOnlyList<FileTextSnapshotDto>>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/items/{id:guid}/snapshots/{snapshotId:guid}/restore", RestoreSnapshotAsync)
        .Produces<ApiResponse<bool>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        // read_file_text（MCP）与 item 级恢复此前只有处理器、没有路由，工具调用恒 404（复审 C-3/C-4）
        group.MapGet("/items/{id:guid}/extracted-text", ReadTextAsync)
        .Produces<ApiResponse<FileTextDto>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/items/{id:guid}/restore", RestoreItemAsync)
        .Produces<ApiResponse<FileWriteResultDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/search", SearchAsync)
        .Produces<ApiResponse<FileSearchResultDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/suggestions", ListSuggestionsAsync)
        .Produces<ApiResponse<IReadOnlyList<FileSuggestionDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/suggestions/{id:guid}/dismiss", DismissSuggestionAsync)
        .Produces<ApiResponse<FileSuggestionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/suggestions/{id:guid}/accept", AcceptSuggestionAsync)
        .Produces<ApiResponse<FileSuggestionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/open-link", BuildOpenLinkAsync)
        .Produces<ApiResponse<FileOpenLinkDto>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        // REQ-15：在当前目录新建文件夹（F2：需要指明账号）
        group.MapPost("/folders", CreateFolderAsync)
        .Produces<ApiResponse<FileItemDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        // REQ-14：大文件浏览器直传——服务器只创建会话与登记结果，不搬字节（F2：需要指明账号）
        group.MapPost("/items/upload-session", CreateUploadSessionAsync)
        .Produces<ApiResponse<UploadSessionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapPost("/items/upload-session/complete", CompleteUploadSessionAsync)
        .Produces<ApiResponse<FileItemDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);
        // REQ-21：分享链接（生成 / 撤销 / 我的分享）
        group.MapPost("/items/{id:guid}/share", CreateShareAsync)
        .Produces<ApiResponse<FileShareDto>>(200)
        .Produces(403)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/items/{id:guid}/shares", ListSharesAsync)
        .Produces<ApiResponse<IReadOnlyList<FileShareDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapDelete("/items/{id:guid}/shares/{permissionId}", RevokeShareAsync)
        .Produces<ApiResponse<bool>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(502)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);
        group.MapGet("/shares", ListAllSharesAsync)
        .Produces<ApiResponse<IReadOnlyList<FileShareDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);
    }

    /// <summary>
    /// 浏览器跳转授权的回调（工单 F4.1）。
    ///
    /// **必须 AllowAnonymous**：这是 Google 把用户浏览器 302 回来的地址，请求上没有任何
    /// PIM 凭据。安全性完全建立在**一次性、短时效、服务端加密保存的 state** 上
    /// （<see cref="FileProviderAuthorizationService.CompleteRedirectAsync"/> 按密文比对）。
    /// 因此这里刻意不读取 <c>ICurrentUserService</c>，也就没有任何「按当前用户查」的漏洞面。
    ///
    /// 回调地址：<c>https://home.hsww.party:15858/api/v1/files/google/callback</c>
    /// （该地址同时必须登记到 Google Cloud Console 的 Authorized redirect URIs，
    /// 否则 Google 直接回 redirect_uri_mismatch，回调根本不会到达这里）。
    /// </summary>
    private static async Task<IResult> GoogleCallbackAsync(
        HttpContext httpContext,
        [FromServices] FileProviderAuthorizationService service,
        CancellationToken ct)
    {
        // 日志从请求容器取，不放进方法签名：本仓的模块 DI 守卫用例要求
        // 路由处理器的 [FromServices] 依赖必须是模块程序集内的类型。
        var logger = httpContext.RequestServices.GetService<ILogger<FilesModule>>();
        var query = httpContext.Request.Query;
        var code = query["code"].FirstOrDefault();
        var state = query["state"].FirstOrDefault();
        var error = query["error"].FirstOrDefault();

        // 浏览器绑定校验：state 必须是**本浏览器发起授权时**下发的那一个。
        // 少了这一步，攻击者可以把自己发起的 state 让受害者完成，把受害者的网盘绑到自己账号上。
        var cookieState = httpContext.Request.Cookies[GoogleStateCookie];
        if (!string.IsNullOrEmpty(error))
        {
            // 用户点了拒绝：交给下面统一处理，不因为缺 cookie 而给出误导性的错误
        }
        else if (!StateMatches(cookieState, state))
        {
            logger?.LogWarning("Google Drive authorization callback rejected: state cookie mismatch");
            return Results.Content(
                CallbackPage("连接 Google Drive 失败", "授权校验未通过（state 与发起授权的浏览器不一致），请重新发起授权。"),
                "text/html; charset=utf-8",
                statusCode: StatusCodes.Status400BadRequest);
        }

        httpContext.Response.Cookies.Delete(GoogleStateCookie, new CookieOptions { Path = FileEndpointPaths.Root });

        try
        {
            await service.CompleteRedirectAsync(GoogleFileProvider.Key, code, state, error, ct);
            return Results.Content(CallbackPage("已连接 Google Drive", "授权成功，可以关闭本页面回到 PIM。"), "text/html; charset=utf-8");
        }
        catch (Exception exception) when (exception is DomainException or FileProviderAuthorizationFailedException
                                              or FileProviderAuthorizationDeniedException
                                              or ProviderCapabilityNotSupportedException)
        {
            // 页面只给用户可读的原因，不回显 code/state，也不回显任何令牌（B5）
            logger?.LogWarning("Google Drive authorization callback failed: {Reason}", exception.Message);
            return Results.Content(
                CallbackPage("连接 Google Drive 失败", exception.Message),
                "text/html; charset=utf-8",
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Google 授权 state 的浏览器绑定 Cookie 名。</summary>
    private const string GoogleStateCookie = "pim_google_oauth_state";

    /// <summary>固定时间比较 state，避免时序侧信道；任一侧为空一律判不匹配。</summary>
    private static bool StateMatches(string? cookieState, string? queryState)
        => !string.IsNullOrEmpty(cookieState)
           && !string.IsNullOrEmpty(queryState)
           && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(cookieState),
               Encoding.UTF8.GetBytes(queryState));

    /// <summary>回调结果页。不含任何令牌/授权码，也不含脚本。</summary>
    private static string CallbackPage(string title, string message)
    {
        static string Escape(string value) => value
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        builder.Append("<title>").Append(Escape(title)).Append("</title></head>");
        builder.Append("<body style=\"font-family:system-ui,sans-serif;padding:2rem;line-height:1.6\">");
        builder.Append("<h1 style=\"font-size:1.25rem\">").Append(Escape(title)).Append("</h1>");
        builder.Append("<p>").Append(Escape(message)).Append("</p>");
        builder.Append("</body></html>");
        return builder.ToString();
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        // 进程崩溃/重启会留下永久 "syncing"：启动时统一复位为可感知的中断态
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
                "Background job infrastructure is not available; scheduled files sync is disabled.");
            return;
        }

        try
        {
            jobClient.Enqueue<FileSyncJob>(job => job.RunAllAsync());
            recurringJobs.AddOrUpdate<FileSyncJob>(
                "files-providers-sync",
                job => job.RunAllAsync(),
                "*/20 * * * *");
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Failed to schedule the recurring files sync job.");
        }
    }

    // ---- 内容出口 ----

    private static async Task<IResult> ContentAsync(
        Guid id,
        [FromServices] FileContentService service,
        CancellationToken ct)
        => Results.Redirect(await service.GetContentLinkAsync(id, ct));

    private static async Task<IResult> ThumbnailAsync(
        Guid id,
        [FromQuery] string? size,
        [FromServices] FileContentService service,
        CancellationToken ct)
        => Results.Redirect(await service.GetThumbnailLinkAsync(id, string.IsNullOrWhiteSpace(size) ? "medium" : size, ct));

    private static async Task<IResult> PreviewUrlAsync(
        Guid id,
        [FromServices] FileContentService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileLinkDto>.Ok(new FileLinkDto(await service.GetPreviewLinkAsync(id, ct))));

    private static async Task<IResult> GetTextAsync(
        Guid id,
        [FromServices] FileContentService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileTextDto>.Ok(FileTextDto.From(await service.GetTextAsync(id, ct))));

    private static async Task<IResult> SaveTextAsync(
        Guid id,
        [FromBody] SaveFileTextRequest request,
        [FromServices] FileContentService service,
        CancellationToken ct)
    {
        await service.SaveTextAsync(id, request.Content, ct);
        return Results.Ok(ApiResponse<bool>.Ok(true));
    }

    private static async Task<IResult> ListSnapshotsAsync(
        Guid id,
        [FromServices] FileContentService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<FileTextSnapshotDto>>.Ok(await service.ListSnapshotsAsync(id, ct)));

    private static async Task<IResult> RestoreSnapshotAsync(
        Guid id,
        Guid snapshotId,
        [FromServices] FileContentService service,
        CancellationToken ct)
    {
        await service.RestoreSnapshotAsync(id, snapshotId, ct);
        return Results.Ok(ApiResponse<bool>.Ok(true));
    }

    // ---- 绑定 ----

    private static async Task<IResult> StartOneDriveBindingAsync(
        [FromBody] StartProviderBindingRequest request,
        [FromServices] FileProviderAuthorizationService service,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken ct)
        => Results.Ok(ApiResponse<ProviderBindingStartDto>.Ok(ProviderBindingStartDto.From(
            await service.StartAsync(RequireUserId(currentUser), OneDriveFileProvider.Key, request.ClientId, ct))));

    /// <summary>
    /// 发起 Google 浏览器跳转授权（工单 F4.1）。
    /// 返回 <c>authorizationUrl</c>，前端整页跳转到它；用户点「同意」后 Google 把浏览器
    /// 302 回 <c>/api/v1/files/google/callback</c>。
    ///
    /// **agent 无法端到端自动跑完这一步**：必须有真人在浏览器点同意。
    /// </summary>
    private static async Task<IResult> StartGoogleAuthorizationAsync(
        HttpContext httpContext,
        [FromServices] FileProviderAuthorizationService service,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken ct)
    {
        var result = await service.StartAsync(RequireUserId(currentUser), GoogleFileProvider.Key, clientId: null, ct);

        // 把一次性 state 同时写进 HttpOnly Cookie，把「这个 state」绑定到**发起授权的浏览器**。
        // 没有这一步的话，攻击者可以自己发起授权拿到合法 state，再诱导受害者完成授权，
        // 从而把受害者的网盘绑到攻击者的账号上（login CSRF）。
        // SameSite=Lax：Google 的顶层跳转会带上它，跨站子请求不会。
        if (!string.IsNullOrEmpty(result.State))
        {
            httpContext.Response.Cookies.Append(
                GoogleStateCookie,
                result.State,
                new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = httpContext.Request.IsHttps,
                    MaxAge = TimeSpan.FromMinutes(10),
                    Path = FileEndpointPaths.Root,
                });
        }

        return Results.Ok(ApiResponse<ProviderBindingStartDto>.Ok(ProviderBindingStartDto.From(result)));
    }

    private static async Task<IResult> GetBindingStatusAsync(
        Guid id,
        [FromServices] FileProviderAuthorizationService service,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken ct)
        => Results.Ok(ApiResponse<ProviderBindingStatusDto>.Ok(ProviderBindingStatusDto.From(
            await service.GetStatusAsync(RequireUserId(currentUser), id, ct))));

    private static async Task<IResult> SetDefaultProviderAsync(
        Guid id,
        [FromServices] FileProviderAuthorizationService service,
        [FromServices] ICurrentUserService currentUser,
        CancellationToken ct)
    {
        await service.SetDefaultAsync(RequireUserId(currentUser), id, ct);
        return Results.Ok(ApiResponse<bool>.Ok(true));
    }

    private static async Task<IResult> DisconnectProviderAsync(
        Guid id,
        [FromServices] FileProviderAuthorizationService service,
        [FromServices] FileProviderTokenService tokens,
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
        HttpContext httpContext,
        [FromServices] FileTreeSyncEngine syncEngine,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        // 后台设施是**可选**依赖（Hangfire 未启用时也要能工作），因此不用 [FromServices]：
        // 那个特性表示必需依赖，宿主解析不到会让端点 500。
        var jobClient = httpContext.RequestServices.GetService<IBackgroundJobClient>();
        // 全局用户过滤器保证只能看到自己的 provider。
        // F1.3：**这里原先硬判 provider.Provider != "onedrive"**；现在只问注册表，
        // 新增网盘不需要改这一行。
        var provider = await db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.Provider })
            .FirstOrDefaultAsync(ct);
        if (provider is null)
            throw new DomainException(5104, "文件来源不存在");

        var providers = httpContext.RequestServices.GetRequiredService<IFileProviderFactory>();
        if (!providers.IsSupported(provider.Provider))
        {
            throw new DomainException(5323, $"该文件来源（{provider.Provider}）不受支持");
        }

        // REQ-25：手动同步**后台化**。此前在请求内跑完整同步（增量一页≈秒级、全量重扫分钟级），
        // 大更新时页面像卡死。现在只入队并立即返回「已开始」；进度与结果由
        // GET /providers/{id}/sync-status 暴露（AC-25.1 / AC-25.3）。
        if (jobClient is null)
        {
            // 后台设施不可用（例如未启用 Hangfire）时退回同步执行，而不是假装已开始——静默失败是禁止的
            var fallback = await syncEngine.SyncAsync(id, ct);
            return Results.Ok(ApiResponse<FileSyncResultDto>.Ok(FileSyncResultDto.From(fallback)));
        }

        try
        {
            jobClient.Enqueue<FileSyncJob>(job => job.RunOneAsync(id));
        }
        catch (Exception)
        {
            // 入队失败**不能**回复「已开始」——那会让用户以为在同步，实际什么都没发生
            // （AC-25.3 明令禁止静默失败）。这里如实报错并说明可稍后重试。
            return Results.Json(
                ApiResponse<string>.Error(5391, "同步任务入队失败，请稍后重试"),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(ApiResponse<ProviderSyncStartedDto>.Ok(
            new ProviderSyncStartedDto(true, "已开始同步，可继续浏览；完成后会显示结果")));
    }

    /// <summary>REQ-25：同步状态（横幅数据源）。</summary>
    private static async Task<IResult> SyncStatusAsync(
        Guid id,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        // 全局用户过滤器保证只能看到自己的 provider
        var provider = await db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new
            {
                item.SyncStatus,
                item.LastError,
                item.LastSyncAt,
                item.SyncedItemCount,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainException(5104, "文件来源不存在");

        // #353：已索引总量单独统计（未删除条目），与「最近一次运行的应用数」区分。
        var totalIndexed = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .CountAsync(item => item.ProviderId == id && !item.IsDeleted, ct);

        return Results.Ok(ApiResponse<ProviderSyncStatusDto>.Ok(new ProviderSyncStatusDto(
            provider.SyncStatus ?? "idle",
            provider.LastError,
            provider.LastSyncAt,
            provider.SyncedItemCount,
            totalIndexed)));
    }

    // ---- 条目 ----

    /// <summary>
    /// 目录列表。F5 口径：**按账号**——列表要么显式带 <paramref name="providerId"/>，
    /// 要么该用户只有一个已连接账号（沿用旧行为）。多账号且未指定时返回明确错误，
    /// 而不是把两个盘的同名路径合并成一棵语义错乱的树。
    /// </summary>
    private static async Task<IResult> ListItemsAsync(
        [FromQuery] string? path,
        [FromQuery] Guid? providerId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] string? order,
        [FromQuery] string? type,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        // 默认页大小 = P3 确认的 100/页：旧默认 50 会让直接调用该端点（含 MCP get_files）
        // 拿到的条数与前端不一致，也与工单口径不符（复审 Important）。
        => Results.Ok(ApiResponse<FileListResponse>.Ok(new FileListResponse(
            await service.ListItemsAsync(
                new FileListQuery(path, q, sort, order, type),
                page ?? 1,
                pageSize ?? FileOperationService.DefaultPageSize,
                providerId,
                ct))));

    private static async Task<IResult> GetItemAsync(
        Guid id,
        [FromServices] FileOperationService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileItemDto>.Ok(await service.GetItemAsync(id, ct)));

    private static async Task<IResult> UploadItemAsync(
        HttpRequest request,
        [FromServices] FileWriteService writeService,
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

        var normalized = FileOperationService.NormalizePath(path);
        var lastSlash = normalized.LastIndexOf('/');
        var fileName = lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
        var folderPath = lastSlash <= 0 ? "/" : normalized[..lastSlash];
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new DomainException(5301, "目标路径必须包含文件或文件夹名称");
        }

        using var content = file.OpenReadStream();
        var result = await writeService.UploadAsync(providerId, folderPath, fileName, content, contentType, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await BuildUploadedItemDtoAsync(db, result.ItemId, ct)));
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
        [FromServices] FileContentService service,
        CancellationToken ct)
        // 稳定直链：302 到 provider 预授权 URL（复用内容出口的登录/归属/敏感路径三道闸）
        => Results.Redirect(await service.GetContentLinkAsync(id, ct));

    /// <summary>
    /// REQ-20 / AC-20.1：下载直链的 JSON 形态。页面拿到 URL 后 <c>window.open</c>，
    /// 浏览器直接从网盘域取内容；**页面不会为了「看一眼 response.url」而把整文件拉进内存**。
    /// </summary>
    private static async Task<IResult> DownloadUrlAsync(
        Guid id,
        [FromServices] FileContentService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileLinkDto>.Ok(new FileLinkDto(await service.GetContentLinkAsync(id, ct))));

    private static async Task<IResult> MoveItemAsync(
        Guid id,
        [FromBody] MoveFileRequest request,
        [FromServices] FileWriteService writeService,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        await writeService.MoveAsync(id, request.DestinationPath, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await FileOperationService.GetItemDtoAsync(db, id, ct)));
    }

    private static async Task<IResult> RenameItemAsync(
        Guid id,
        [FromBody] RenameFileRequest request,
        [FromServices] FileWriteService writeService,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        await writeService.RenameAsync(id, request.Name, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await FileOperationService.GetItemDtoAsync(db, id, ct)));
    }

    private static async Task<IResult> DeleteItemAsync(
        Guid id,
        [FromServices] FileWriteService writeService,
        CancellationToken ct)
    {
        await writeService.DeleteToTrashAsync(id, ct);
        // 个人版网盘没有统一的回收站恢复 API，DELETE 后远端即不可见，
        // 因此不能承诺「可恢复」——诚实说明可在网盘网页版回收站自行还原。
        return Results.Ok(ApiResponse<string>.Ok(
            "已删除（已移入网盘回收站；如需还原请在网盘网页版操作）"));
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? mode,
        [FromQuery] Guid? providerId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] FileSearchService service,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileSearchResultDto>.Ok(
            // MCP 合约里 search_files 声明了 page/pageSize，这里必须真正生效（复审发现）
            // F5：默认跨账号（每条命中自带 providerId），可显式按账号过滤。
            await service.SearchAsync(new FileSearchQuery(q, mode), page ?? 1, pageSize ?? 20, providerId, ct)));

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

    private static async Task<IResult> CreateFolderAsync(
        [FromBody] CreateFolderRequest request,
        [FromServices] FileWriteService writeService,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        var result = await writeService.CreateFolderAsync(request.ProviderId, request.Path, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await FileOperationService.GetItemDtoAsync(db, result.ItemId, ct)));
    }

    private static async Task<IResult> CreateUploadSessionAsync(
        [FromBody] CreateUploadSessionRequest request,
        [FromServices] FileWriteService writeService,
        CancellationToken ct)
    {
        var session = await writeService.CreateUploadSessionAsync(
            request.ProviderId, request.Path, request.FileName, ct);
        return Results.Ok(ApiResponse<UploadSessionDto>.Ok(new UploadSessionDto(
            session.UploadUrl,
            session.ExpirationDateTime,
            request.Path,
            request.FileName)));
    }

    private static async Task<IResult> CompleteUploadSessionAsync(
        [FromBody] CompleteUploadRequest request,
        [FromServices] FileWriteService writeService,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        var result = await writeService.RegisterUploadedFileAsync(
            request.ProviderId, request.Path, request.FileName, request.UploadedItemId, ct);
        return Results.Ok(ApiResponse<FileItemDto>.Ok(
            await FileOperationService.GetItemDtoAsync(db, result.ItemId, ct)));
    }

    private static async Task<IResult> CreateShareAsync(
        Guid id,
        [FromBody] CreateShareRequest request,
        [FromServices] FileShareService shares,
        CancellationToken ct)
        => Results.Ok(ApiResponse<FileShareDto>.Ok(
            await shares.CreateAsync(id, request.PermissionType, request.ExpiresInDays, ct)));

    private static async Task<IResult> ListSharesAsync(
        Guid id,
        [FromServices] FileShareService shares,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<FileShareDto>>.Ok(await shares.ListForItemAsync(id, ct)));

    private static async Task<IResult> RevokeShareAsync(
        Guid id,
        string permissionId,
        [FromServices] FileShareService shares,
        CancellationToken ct)
    {
        await shares.RevokeAsync(id, permissionId, ct);
        return Results.Ok(ApiResponse<bool>.Ok(true));
    }

    private static async Task<IResult> ListAllSharesAsync(
        [FromQuery] int? limit,
        [FromServices] FileShareService shares,
        CancellationToken ct)
        => Results.Ok(ApiResponse<IReadOnlyList<FileShareDto>>.Ok(
            await shares.ListAllAsync(limit ?? 50, ct)));

    private static async Task<IResult> BuildOpenLinkAsync(
        Guid id,
        [FromServices] FileWriteService writeService,
        [FromServices] PimDbContext db,
        CancellationToken ct)
    {
        var webUrl = await writeService.GetWebUrlAsync(id, ct);
        var providerKey = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => item.Provider!.Provider)
            .FirstOrDefaultAsync(ct) ?? "unknown";
        return Results.Ok(ApiResponse<FileOpenLinkDto>.Ok(new FileOpenLinkDto(webUrl, $"{providerKey}-web")));
    }

    private static async Task<IResult> ReadTextAsync(
        Guid id,
        [FromQuery] long? maxBytes,
        [FromServices] FileContentService service,
        CancellationToken ct)
    {
        var text = await service.ReadTextAsync(id, maxBytes, ct);
        return Results.Ok(ApiResponse<FileTextDto>.Ok(FileTextDto.From(text)));
    }

    private static async Task<IResult> RestoreItemAsync(
        Guid id,
        [FromServices] FileWriteService service,
        CancellationToken ct)
    {
        var result = await service.RestoreAsync(id, ct);
        return Results.Ok(ApiResponse<FileWriteResultDto>.Ok(
            new FileWriteResultDto(result.ItemId, result.Path)));
    }
}

public static class FileEndpointPaths
{
    public const string Root = "/api/v1/files";
    public const string Providers = $"{Root}/providers";
    public const string GoogleAuthorization = $"{Providers}/google/authorize";
    public const string GoogleCallback = "/api/v1/files/google/callback";

    public static string ProviderSync(string id) => $"{Providers}/{id}/sync";
    public static string Item(string id) => $"{Root}/items/{id}";
    public static string ItemDownload(string id) => $"{Item(id)}/download";
}
