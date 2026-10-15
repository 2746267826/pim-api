using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Providers.Google;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// Google Drive 接入测试（工单 05 / F4）。
///
/// 覆盖：浏览器跳转授权的地址形状（scope 必须是 <c>.../auth/drive</c>，且带
/// <c>access_type=offline</c> + <c>prompt=consent</c>）、缺凭据/回调地址不匹配时必须**明确报错**、
/// change token 的「首次全量 → 此后增量」、pageToken 失效重置全量、
/// 以及 refresh token 失效与游标失效**互不混淆**。
///
/// 真实的 Google 跳转授权需要维护者在浏览器点「同意」，见 PR 描述里的人工作业步骤；
/// 这里用真实 HTTP 层的 stub（真 HttpClient + 真 JSON 解析）证明协议实现本身正确。
/// </summary>
public class GoogleDriveProviderTests
{
    private static readonly Guid UserId = Guid.Parse("cccccccc-1111-2222-3333-444444444455");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    private sealed class HarnessClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestSecretProtector : Pim.Infrastructure.Secrets.ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    /// <summary>
    /// 一个够真的 Drive/Drive-OAuth stub：按路径与查询串路由，返回真实形状的 JSON。
    /// 用真 <see cref="HttpClient"/>，因此 JSON 解析、错误体解析、Authorization 头都在被测路径上。
    /// </summary>
    private sealed class StubGoogleApi
    {
        public string RootId { get; set; } = "root-real-id";

        public StubGoogleApi()
        {
            var root = DriveFile(RootId, "My Drive", GoogleDriveClient.FolderMimeType, null, Array.Empty<string>());
            FilesById[RootId] = root;
            // Drive 接受 "root" 作为盘根别名（PIM 就是用它解析真实 id 的）
            FilesById["root"] = root;
        }

        /// <summary>files.list 的第 N 次调用返回的页（超出则返回空页）。</summary>
        public Queue<object> FilesListPages { get; } = new();

        /// <summary>changes.list 的第 N 次调用返回的页。</summary>
        public Queue<object> ChangesPages { get; } = new();

        public string StartPageToken { get; set; } = "token-1000";

        public List<string> Requests { get; } = [];

        /// <summary>收到的 Origin 头（断点续传会话初始化必须带）。</summary>
        public List<string?> Origins { get; } = [];

        /// <summary>POST /files 新建目录时返回的 id。</summary>
        public string CreatedFolderId { get; set; } = "created-folder-id";

        /// <summary>让所有 Drive 调用回 404，用来验证远端不存在的幂等语义。</summary>
        public bool NotFoundForEverything { get; set; }

        /// <summary>files.get 的按 id 索引；根目录默认存在（盘根真实 id 靠它解析）。</summary>
        public Dictionary<string, object> FilesById { get; } = new(StringComparer.Ordinal);

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method.Method} {url}");
            Origins.Add(request.Headers.TryGetValues("Origin", out var origins) ? origins.FirstOrDefault() : null);

            // 身份取自 Drive 的 about.get（不是 OAuth userinfo：那需要额外的身份 scope）
            if (NotFoundForEverything && !url.Contains("/oauth2", StringComparison.Ordinal))
            {
                return Json(404, Error("notFound", "File not found: no-such-file"));
            }

            if (url.Contains("/drive/v3/about", StringComparison.Ordinal))
            {
                return Json(200, new
                {
                    user = new { permissionId = "google-sub-1", displayName = "Google User", emailAddress = "g@example.test" },
                });
            }

            if (url.Contains("oauth2/v3/userinfo", StringComparison.Ordinal))
            {
                // 只申请了 drive scope 时这个端点会 401；桩如实复现，避免掩盖 scope 缺失
                return Json(401, Error("insufficientPermissions", "Request had insufficient authentication scopes"));
            }

            if (url.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal))
            {
                return Json(200, new
                {
                    access_token = "google-access-1",
                    refresh_token = "google-refresh-1",
                    expires_in = 3600,
                    scope = GoogleAuthorizer.DriveScope,
                });
            }

            if (url.Contains("/drive/v3/changes/startPageToken", StringComparison.Ordinal))
            {
                return Json(200, new { startPageToken = StartPageToken });
            }

            if (url.Contains("/drive/v3/changes?", StringComparison.Ordinal))
            {
                if (ChangesPages.Count == 0)
                {
                    return Json(400, Error("invalidPageToken", "Invalid Value"));
                }

                var page = ChangesPages.Dequeue();
                return page is HttpResponseMessage response ? response : Json(200, page);
            }

            if (url.Contains("/drive/v3/files/", StringComparison.Ordinal))
            {
                // files.get：按 id 返回对应条目（解析盘根真实 id 也走这里）
                var id = Uri.UnescapeDataString(url.Split("/drive/v3/files/")[1].Split('?')[0]);
                return FilesById.TryGetValue(id, out var file)
                    ? Json(200, file)
                    : Json(404, Error("notFound", $"file {id} not found"));
            }

            if (url.Contains("/upload/drive/v3/files?uploadType=resumable", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.Location = new Uri("https://upload.example.com/session");
                return response;
            }

            if (url.Contains("/drive/v3/files?", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                // files.create：必须在响应体里给出新条目的 id（调用方不得再 search 回查）
                return Json(200, Folder(CreatedFolderId, "新目录", null));
            }

            if (url.Contains("/drive/v3/files?", StringComparison.Ordinal))
            {
                if (FilesListPages.Count == 0)
                {
                    return Json(200, new { files = Array.Empty<object>() });
                }

                var page = FilesListPages.Dequeue();
                return page is HttpResponseMessage response ? response : Json(200, page);
            }

            return Json(404, Error("notFound", $"unrouted: {url}"));
        }

        private static object DriveFile(
            string id,
            string name,
            string mimeType,
            long? size,
            string[] parents,
            string? md5 = null)
            => new
            {
                id,
                name,
                mimeType,
                size = size?.ToString(),
                modifiedTime = "2026-09-30T10:00:00.000Z",
                md5Checksum = md5,
                parents,
                trashed = false,
                webViewLink = $"https://drive.google.com/file/d/{id}/view",
                webContentLink = $"https://drive.google.com/uc?id={id}",
                thumbnailLink = $"https://lh3.googleusercontent.com/{id}",
            };

        public static object File(string id, string name, string? parent, long size = 10, string? md5 = "md5-1")
            => DriveFile(id, name, "text/plain", size, parent is null ? [] : [parent], md5);

        public static object Folder(string id, string name, string? parent)
            => DriveFile(id, name, GoogleDriveClient.FolderMimeType, null, parent is null ? [] : [parent]);

        private static object Error(string reason, string message)
            => new { error = new { code = 400, message, errors = new[] { new { reason, message } } } };

        private static HttpResponseMessage Json(int status, object payload)
            => new((HttpStatusCode)status)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"google-provider-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    /// <summary>
    /// 带上 Google 的凭据配置：服务端配置是 client secret 的唯一来源（不入库、不回显）。
    /// 缺配置的用例走 <see cref="GoogleAuthorizer"/> 直接断言明确报错，而不是靠这里。
    /// </summary>
    private static Microsoft.Extensions.Configuration.IConfiguration GoogleConfiguration()
        => new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pim:Files:google:ClientId"] = "google-client-id",
                ["Pim:Files:google:ClientSecret"] = "google-client-secret",
            })
            .Build();

    private static FilesProviderHarness CreateHarness(PimDbContext db, StubGoogleApi api)
        => new(
            db,
            graph: null,
            protector: new TestSecretProtector(),
            clock: new HarnessClock(Now),
            google: new GoogleDriveClient(new StubHttpClientFactory(
                new StubHttpHandler { Responder = api.Respond })),
            configuration: GoogleConfiguration());

    private static FileProviderEntity SeedGoogleProvider(PimDbContext db, string accountId = "google-sub-1")
    {
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "google",
            AccountId = accountId,
            ClientId = "google-client-id",
            Status = "connected",
            IsDefault = true,
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes("protected::google-refresh-1"),
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    // ---- 授权 ----

    [Fact]
    public async Task AuthorizationUrl_UsesDriveScope_WithOfflineAccessAndConsent()
    {
        var api = new StubGoogleApi();
        using var harness = CreateHarness(CreateDb(), api);
        var authorizer = new GoogleAuthorizer(harness.Get<GoogleDriveClient>());

        var challenge = await authorizer.BeginAsync(new FileProviderAuthorizationRequest(
            Guid.NewGuid(), UserId, "client-id", "client-secret",
            "https://home.hsww.party:15858/api/v1/files/google/callback"));

        var url = challenge.AuthorizationUrl!;
        Assert.StartsWith(GoogleAuthorizer.AuthorizationEndpoint, url, StringComparison.Ordinal);
        // ⚠️ 必须是 Drive 的 scope，不是 Microsoft Graph 的 Files.ReadWrite.All
        Assert.Contains(Uri.EscapeDataString(GoogleAuthorizer.DriveScope), url, StringComparison.Ordinal);
        Assert.DoesNotContain("Files.ReadWrite.All", url, StringComparison.Ordinal);
        // 少了这两个参数就拿不到 refresh token（一小时后静默掉线）
        Assert.Contains("access_type=offline", url, StringComparison.Ordinal);
        Assert.Contains("prompt=consent", url, StringComparison.Ordinal);
        Assert.Contains("state=", url, StringComparison.Ordinal);
        // state 是服务端生成的一次性值，且不放进 URL 以外的地方
        Assert.False(string.IsNullOrWhiteSpace(challenge.State));
    }

    [Fact]
    public async Task Authorization_WithoutCredentials_FailsExplicitly()
    {
        var api = new StubGoogleApi();
        using var harness = CreateHarness(CreateDb(), api);
        var authorizer = new GoogleAuthorizer(harness.Get<GoogleDriveClient>());

        var error = await Assert.ThrowsAsync<FileProviderAuthorizationFailedException>(
            () => authorizer.BeginAsync(new FileProviderAuthorizationRequest(
                Guid.NewGuid(), UserId, ClientId: null, ClientSecret: null, RedirectUri: null)));

        Assert.Contains("PIM_FILES_GOOGLE_CLIENT_ID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorization_WithRedirectUriThatDoesNotMatchRegisteredCallback_FailsExplicitly()
    {
        var api = new StubGoogleApi();
        using var harness = CreateHarness(CreateDb(), api);
        var authorizer = new GoogleAuthorizer(harness.Get<GoogleDriveClient>());

        var error = await Assert.ThrowsAsync<FileProviderAuthorizationFailedException>(
            () => authorizer.BeginAsync(new FileProviderAuthorizationRequest(
                Guid.NewGuid(), UserId, "client-id", "client-secret",
                "https://example.test/other/path")));

        Assert.Contains("/api/v1/files/google/callback", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteRedirect_ExchangesCode_AndReturnsAccount()
    {
        var api = new StubGoogleApi();
        using var harness = CreateHarness(CreateDb(), api);
        var authorizer = new GoogleAuthorizer(harness.Get<GoogleDriveClient>());

        var result = await authorizer.CompleteAsync(
            new FileProviderAuthorizationRequest(
                Guid.NewGuid(), UserId, "client-id", "client-secret",
                "https://home.hsww.party:15858/api/v1/files/google/callback"),
            "auth-code",
            "state-from-server");

        Assert.Equal("google", result.Account.ProviderKey);
        Assert.Equal("google-sub-1", result.Account.AccountId);
        Assert.Equal("google-refresh-1", result.RefreshToken);
    }

    /// <summary>
    /// refresh token 失效（Testing 发布状态下 7 天过期 / 用户撤销）必须走「授权已失效」，
    /// 与 F4.3 的 change token 失效是**两回事**（工单明确要求不得混为一谈）。
    /// </summary>
    [Fact]
    public async Task RefreshTokenInvalidGrant_MarksBindingExpired_NotCursorReset()
    {
        var api = new StubGoogleApi();
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        provider.DeltaLink = "delta:token-1000";
        await db.SaveChangesAsync();

        // 让令牌端点回 invalid_grant
        var failing = new StubGoogleApi();
        using var harness = new FilesProviderHarness(
            db,
            graph: null,
            protector: new TestSecretProtector(),
            clock: new HarnessClock(Now),
            google: new GoogleDriveClient(new StubHttpClientFactory(new StubHttpHandler
            {
                Responder = request => request.RequestUri!.ToString().Contains("token", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""",
                            Encoding.UTF8,
                            "application/json"),
                    }
                    : failing.Respond(request),
            })));

        var error = await Assert.ThrowsAsync<Pim.Core.Exceptions.DomainException>(
            () => harness.Tokens.GetAccessTokenAsync(provider.Id));

        Assert.Equal(5322, error.ErrorCode);
        var reloaded = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("expired", reloaded.Status);
        // 游标与「游标失效」语义严格分开：授权失效不动游标、不置 delta_reset_at
        Assert.Equal("delta:token-1000", reloaded.DeltaLink);
        Assert.Null(reloaded.DeltaResetAt);
    }

    // ---- 第二轮 review 的发现 ----

    /// <summary>
    /// 原生文档的导出目标必须按类型选：Drive 只支持 Docs→text/plain、
    /// Sheets→csv、Slides→pdf，对 Sheets/Slides 请求 text/plain 会直接 400。
    /// </summary>
    [Theory]
    [InlineData("application/vnd.google-apps.document", "text/plain")]
    [InlineData("application/vnd.google-apps.spreadsheet", "text/csv")]
    [InlineData("application/vnd.google-apps.presentation", "application/pdf")]
    [InlineData("application/vnd.google-apps.form", null)]
    [InlineData("application/vnd.google-apps.drawing", null)]
    [InlineData("application/vnd.google-apps.folder", null)]
    [InlineData("application/pdf", null)]
    public void ExportMimeType_MatchesDriveSupportMatrix(string mimeType, string? expected)
        => Assert.Equal(expected, GoogleDriveClient.ExportMimeTypeFor(mimeType));

    /// <summary>
    /// 新建目录必须直接用 POST /files 响应里的 id。
    /// Drive 的搜索索引是最终一致的，创建后立刻 files.list 回查会随机查不到 →
    /// 误报「未能读回」，而网盘上目录已经建好，重试会堆出同名孤儿目录。
    /// </summary>
    [Fact]
    public async Task CreateFolder_UsesIdFromCreateResponse_WithoutSearchRequery()
    {
        var api = new StubGoogleApi();
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        api.FilesById[api.RootId] = StubGoogleApi.Folder(api.RootId, "My Drive", null);
        api.CreatedFolderId = "created-folder-id";
        using var harness = CreateHarness(db, api);
        var session = await harness.Sessions.OpenAsync(provider);
        var folder = await session.Content.GetItemAsync(api.RootId) ?? throw new InvalidOperationException();

        var createdId = await session.Writer.CreateFolderAsync(
            new RemoteFolderRef(api.RootId, folder.Path ?? "/"), "新目录");

        Assert.Equal(api.CreatedFolderId, createdId);
        // 创建之后不得再按名字去 search 回查
        Assert.DoesNotContain(
            api.Requests,
            r => r.Contains("name%20%3D", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 断点续传会话必须带 Origin：Drive 只有在初始化时看到它，才会给浏览器的分片 PUT
    /// 返回 CORS 响应头，否则浏览器直传被跨域拦下。
    /// </summary>
    [Fact]
    public async Task ResumableUploadSession_SendsOriginHeader()
    {
        var api = new StubGoogleApi();
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        api.FilesById[api.RootId] = StubGoogleApi.Folder(api.RootId, "My Drive", null);
        using var harness = CreateHarness(db, api);
        var session = await harness.Sessions.OpenAsync(provider);

        var upload = await session.Writer.CreateUploadSessionAsync(
            new RemoteFolderRef(api.RootId, "/"), "big.bin");

        Assert.Equal("https://upload.example.com/session", upload.UploadUrl);
        Assert.Contains(api.Origins, origin => origin == "https://home.hsww.party:15858");
    }

    /// <summary>
    /// 403 的语义分流：权限类是**用户可自愈**的（重新授权/检查权限），
    /// 不能和限流一样被报成「网盘服务暂时不可用」；限流仍走可重试的瞬态异常。
    /// </summary>
    [Fact]
    public void Forbidden_PermissionDenied_IsUserActionable_NotTransient()
    {
        var permissionDenied = GoogleErrorMapper.FromBody(
            403,
            """{"error":{"code":403,"message":"The user does not have sufficient permissions for this file.","errors":[{"reason":"insufficientPermissions"}]}}""");

        var expired = Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderAuthorizationExpiredException>(
            permissionDenied);
        Assert.Contains("insufficientPermissions", expired.Message, StringComparison.Ordinal);

        // Drive 对**单个文件**权限不足时实际给的 reason 是 insufficientFilePermissions，
        // 漏掉它会让这类错误重新落回「上游故障」（复审 Important）
        var fileLevelDenied = GoogleErrorMapper.FromBody(
            403,
            """{"error":{"code":403,"message":"The user does not have sufficient permissions for this file.","errors":[{"reason":"insufficientFilePermissions"}]}}""");
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderAuthorizationExpiredException>(
            fileLevelDenied);

        var throttled = GoogleErrorMapper.FromBody(
            403,
            """{"error":{"code":403,"message":"Rate Limit Exceeded","errors":[{"reason":"rateLimitExceeded"}]}}""");
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderTransientException>(throttled);

        // 普通权限类 403（含共享盘继承权限这类长尾 reason，以及还没见过的 reason）
        // → 可自愈的 400/5322；**逐个 reason 打补丁是治标**，所以默认归入权限类。
        foreach (var reason in new[]
                 {
                     "cannotModifyInheritedTeamDrivePermission",
                     "appNotAuthorizedToFile",
                     "domainPolicy",
                     "forbidden",
                     "someFutureReasonWeHaveNotSeen",
                 })
        {
            var mapped = GoogleErrorMapper.FromBody(
                403,
                "{\"error\":{\"code\":403,\"message\":\"Forbidden\",\"errors\":[{\"reason\":\""
                + reason
                + "\"}]}}");
            Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderAuthorizationExpiredException>(mapped);
        }

        // 但**运维/配置类** 403 不是用户能修的：API 未启用 / 项目被停用
        // 必须单列，按服务端故障出口（502 + Error 日志），不能伪装成「请重新连接」
        foreach (var reason in new[]
                 {
                     "accessNotConfigured",
                     "projectNotAuthorized",
                     "clientProjectBlocked",
                     "SERVICE_DISABLED",
                     "CONSUMER_SUSPENDED",
                 })
        {
            var mapped = GoogleErrorMapper.FromBody(
                403,
                "{\"error\":{\"code\":403,\"message\":\"Forbidden\",\"errors\":[{\"reason\":\""
                + reason
                + "\"}]}}");
            Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderConfigurationFailureException>(mapped);
        }

        // 新版 Google API 把机器可读的错误放在 error.details[] 的 google.rpc.ErrorInfo 里，
        // 只读 error.errors[] 会漏掉运维类错误
        var errorInfo = GoogleErrorMapper.FromBody(
            403,
            """
            {"error":{"code":403,"message":"Google Drive API has not been used in project 123 before or it is disabled.",
            "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SERVICE_DISABLED","domain":"googleapis.com"}]}}
            """);
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderConfigurationFailureException>(errorInfo);

        // 解析必须对「合法 JSON 但形状异常」健壮：不能让解析本身抛异常（那会变成 500）
        foreach (var weird in new[] { "null", "[]", "{\"error\":null}", "{\"error\":{\"errors\":[null]}}", "{}", "<html>502</html>" })
        {
            var exception = GoogleErrorMapper.FromBody(403, weird);
            Assert.NotNull(exception);
        }

        // 空间/配额不足既不是限流（等一会儿不会好）也不是授权失效（重新连接没用），单列一类
        var quota = GoogleErrorMapper.FromBody(
            403,
            """{"error":{"code":403,"message":"The user's Drive storage quota has been exceeded.","errors":[{"reason":"storageQuotaExceeded"}]}}""");
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderStorageQuotaExceededException>(quota);
    }

    /// <summary>
    /// 远端 404 的**幂等**语义必须仍然成立：读不到东西要返回 null、删不到东西要当成功。
    /// 归一化之后若还捕错异常类型，这些幂等路径会变成错误响应（复审 Important）。
    /// </summary>
    [Fact]
    public async Task RemoteNotFound_KeepsIdempotentNullAndNoOpSemantics()
    {
        var api = new StubGoogleApi { NotFoundForEverything = true };
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        using var harness = CreateHarness(db, api);
        var session = await harness.Sessions.OpenAsync(provider);

        // 按 id 回读：不存在 → null（调用方据此给出「文件不存在」，而不是抛异常穿透）
        Assert.Null(await session.Content.GetItemAsync("no-such-file"));

        // 删除一个远端已不存在的条目：与 OneDrive 实现一致，视为成功
        await session.Writer.DeleteItemAsync("no-such-file");

        // 撤销一个已不存在的权限：同上
        await session.Shares!.RevokeSharePermissionAsync("no-such-file", "perm-x");
    }

    /// <summary>
    /// 游标失效判定必须限定状态码：带 "pageToken" 字样的 403/429/503 不能被当成游标失效，
    /// 否则会绕过退避重试与权限分类（复审 Minor）。
    /// </summary>
    [Fact]
    public void CursorFailure_IsLimitedToGoneAndBadRequest()
    {
        var badRequest = GoogleErrorMapper.FromBody(
            400, """{"error":{"errors":[{"reason":"invalidPageToken"}]}}""");
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderCursorExpiredException>(badRequest);

        var gone = GoogleErrorMapper.FromBody(
            410, """{"error":{"message":"The requested page token has expired."}}""");
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderCursorExpiredException>(gone);

        // 同样的 reason 出现在 403/429/503 上，必须各归各类，不得被游标分支抢占
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderAuthorizationExpiredException>(
            GoogleErrorMapper.FromBody(403, """{"error":{"errors":[{"reason":"invalidPageToken"}]}}"""));
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderTransientException>(
            GoogleErrorMapper.FromBody(429, """{"error":{"errors":[{"reason":"invalidPageToken"}]}}"""));
        Assert.IsType<Pim.Module.Files.Providers.Abstractions.ProviderTransientException>(
            GoogleErrorMapper.FromBody(503, """{"error":{"errors":[{"reason":"invalidPageToken"}]}}"""));
    }

    // ---- 同步：首次全量 → 此后增量 ----

    [Fact]
    public async Task FirstSync_FullCrawl_ThenStoresDeltaCursor()
    {
        var api = new StubGoogleApi();
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        api.FilesListPages.Enqueue(new
        {
            files = new[]
            {
                StubGoogleApi.Folder("folder-1", "合同", api.RootId),
                StubGoogleApi.File("file-1", "房屋租赁合同.pdf", "folder-1", 2048, "md5-file-1"),
            },
        });
        // files.get 也要认得这两个条目（Drive 只给父 id 时，父路径靠它们兜底解析）
        api.FilesById["folder-1"] = StubGoogleApi.Folder("folder-1", "合同", api.RootId);
        api.FilesById["file-1"] = StubGoogleApi.File("file-1", "房屋租赁合同.pdf", "folder-1", 2048, "md5-file-1");
        using var harness = CreateHarness(db, api);
        var engine = new FileTreeSyncEngine(
            db, harness.Sessions, harness.Tokens, harness.Providers, null, new HarnessClock(Now));

        var result = await engine.SyncAsync(provider.Id);

        Assert.Equal(2, result.ItemsApplied);
        // 首次同步没有游标 → 全量扫描
        Assert.True(result.FullRecrawl);
        var rows = await db.Set<FileItemEntity>().Where(i => i.ProviderId == provider.Id).ToListAsync();
        Assert.Equal("/合同", rows.Single(r => r.ExternalFileId == "folder-1").Path);
        // Drive 只给父 id 不给父路径：这一条必须靠「补解析」拿到正确路径
        Assert.Equal("/合同/房屋租赁合同.pdf", rows.Single(r => r.ExternalFileId == "file-1").Path);
        Assert.Equal("md5-file-1", rows.Single(r => r.ExternalFileId == "file-1").Etag);

        var updated = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("delta:token-1000", updated.DeltaLink);
        Assert.Equal("idle", updated.SyncStatus);
        // 首扫用的是 files.list，不是 changes.list
        Assert.Contains(api.Requests, r => r.Contains("/drive/v3/files?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondSync_UsesChangeToken_AndAdvancesIt()
    {
        var api = new StubGoogleApi();
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        provider.DeltaLink = "delta:token-1000";
        await db.SaveChangesAsync();

        api.ChangesPages.Enqueue(new
        {
            changes = new object[]
            {
                new
                {
                    removed = false,
                    fileId = "file-2",
                    file = StubGoogleApi.File("file-2", "新文件.txt", api.RootId, 12, "md5-file-2"),
                },
            },
            newStartPageToken = "token-2000",
        });
        using var harness = CreateHarness(db, api);
        var engine = new FileTreeSyncEngine(
            db, harness.Sessions, harness.Tokens, harness.Providers, null, new HarnessClock(Now));

        var result = await engine.SyncAsync(provider.Id);

        Assert.Equal(1, result.ItemsApplied);
        var row = await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "file-2");
        Assert.Equal("/新文件.txt", row.Path);
        // newStartPageToken 必须落库：nextPageToken 是一次性分页游标，下次拿它请求会被拒
        var updated = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("delta:token-2000", updated.DeltaLink);
        Assert.Contains(api.Requests, r => r.Contains("pageToken=token-1000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovedChange_SoftDeletesItem()
    {
        var api = new StubGoogleApi();
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        provider.DeltaLink = "delta:token-1000";
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = "file-gone",
            Path = "/gone.txt",
            Name = "gone.txt",
            ItemType = "file",
        });
        await db.SaveChangesAsync();

        api.ChangesPages.Enqueue(new
        {
            changes = new object[] { new { removed = true, fileId = "file-gone" } },
            newStartPageToken = "token-2000",
        });
        using var harness = CreateHarness(db, api);
        var engine = new FileTreeSyncEngine(
            db, harness.Sessions, harness.Tokens, harness.Providers, null, new HarnessClock(Now));

        var result = await engine.SyncAsync(provider.Id);

        Assert.Equal(1, result.ItemsDeleted);
        var row = await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "file-gone");
        Assert.True(row.IsDeleted);
    }

    /// <summary>
    /// F4.3：change token（pageToken）失效 → 重置为全量重扫，并记录 delta_reset_at。
    /// 这与「授权失效」不是一回事：绑定仍然是 connected。
    /// </summary>
    [Fact]
    public async Task InvalidPageToken_ResetsToFullRecrawl_WithoutTouchingAuthorization()
    {
        var api = new StubGoogleApi();
        await using var db = CreateDb();
        var provider = SeedGoogleProvider(db);
        provider.DeltaLink = "delta:stale-token";
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = "file-stale",
            Path = "/旧.txt",
            Name = "旧.txt",
            ItemType = "file",
            LastSeenAt = Now.AddDays(-7),
        });
        await db.SaveChangesAsync();

        // 第一次 changes.list 回 invalidPageToken（StubGoogleApi 队列空时的默认行为）；
        // 重置后的全量重扫返回只剩 file-keep。
        api.FilesListPages.Enqueue(new { files = new[] { StubGoogleApi.File("file-keep", "保留.txt", api.RootId) } });
        using var harness = CreateHarness(db, api);
        var engine = new FileTreeSyncEngine(
            db, harness.Sessions, harness.Tokens, harness.Providers, null, new HarnessClock(Now));

        var result = await engine.SyncAsync(provider.Id);

        Assert.True(result.FullRecrawl);
        Assert.True((await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "file-stale")).IsDeleted);
        Assert.False((await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "file-keep")).IsDeleted);
        var updated = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.NotNull(updated.DeltaResetAt);
        Assert.Equal("token-1000", updated.DeltaLink!.Replace("delta:", string.Empty));
        // 授权状态不受影响：这不是「掉授权」
        Assert.Equal("connected", updated.Status);
    }

    // ---- F2：多账号隔离（AC-2）----

    /// <summary>
    /// AC-2：两个 OneDrive 账号并存时，**行、游标、令牌互不干扰**。
    /// 两个账号各自有一份 delta 页；同步 A 之后 B 的条目与游标必须纹丝不动，反之亦然。
    /// </summary>
    [Fact]
    public async Task TwoAccounts_KeepRowsCursorsAndTokensIndependent()
    {
        var routing = new AccountRoutingGraphClient();
        await using var db = CreateDb();
        var accountA = SeedOneDriveProvider(db, "acc-A", "refresh-A", isDefault: true);
        var accountB = SeedOneDriveProvider(db, "acc-B", "refresh-B", isDefault: false);

        routing.For("refresh-A").DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.Folder("root-A-1", "A 的目录", parentId: "root-1"),
            OneDriveDeltaPageFactory.File("file-A-1", "a.txt", parentId: "root-A-1", parentPath: "/drive/root:/A 的目录")));
        routing.For("refresh-A").DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-A-2", "a2.txt")));
        routing.For("refresh-B").DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-B-1", "b.txt")));
        routing.For("refresh-B").DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-B-2", "b2.txt")));

        using var harness = new FilesProviderHarness(
            db, routing, new TestSecretProtector(), new HarnessClock(Now));
        var engine = new FileTreeSyncEngine(
            db, harness.Sessions, harness.Tokens, harness.Providers, null, new HarnessClock(Now));

        // 第一轮：两个账号各自推进
        await engine.SyncAsync(accountA.Id);
        await engine.SyncAsync(accountB.Id);

        var rowsA = await db.Set<FileItemEntity>()
            .Where(i => i.ProviderId == accountA.Id).Select(i => i.ExternalFileId).ToListAsync();
        var rowsB = await db.Set<FileItemEntity>()
            .Where(i => i.ProviderId == accountB.Id).Select(i => i.ExternalFileId).ToListAsync();
        Assert.Equal(new[] { "file-A-1", "root-A-1" }, rowsA.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "file-B-1" }, rowsB.OrderBy(x => x).ToArray());

        // 第二轮：A 的增量不得把 B 的游标带跑，也不得覆盖 B 的行
        var cursorBBefore = (await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == accountB.Id)).DeltaLink;
        db.ChangeTracker.Clear();
        await engine.SyncAsync(accountA.Id);

        var a = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == accountA.Id);
        var b = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == accountB.Id);
        Assert.NotEqual(a.DeltaLink, b.DeltaLink);
        Assert.Equal(cursorBBefore, b.DeltaLink);
        Assert.False((await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "file-B-1")).IsDeleted);
        // 刷新令牌按账号各自使用（路由替身按 refresh token 分派，走错账号会拿不到页）
        Assert.Contains("refresh-A", routing.UsedRefreshTokens);
        Assert.Contains("refresh-B", routing.UsedRefreshTokens);

        // 账号维度唯一键的另一半：两行必须是不同的 account_id
        Assert.NotEqual(a.AccountId, b.AccountId);
    }

    private static FileProviderEntity SeedOneDriveProvider(
        PimDbContext db,
        string accountId,
        string refreshToken,
        bool isDefault)
    {
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "onedrive",
            AccountId = accountId,
            ClientId = "cid",
            Status = "connected",
            IsDefault = isDefault,
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes($"protected::{refreshToken}"),
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    /// <summary>
    /// 按 **refresh token** 分派到两个独立的假 Graph 后端：
    /// 这样「令牌用错账号」会在测试里表现为拿不到页，而不是被掩盖。
    /// </summary>
    private sealed class AccountRoutingGraphClient : IOneDriveGraphClient
    {
        private readonly Dictionary<string, FakeOneDriveGraphClient> _byRefreshToken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeOneDriveGraphClient> _byAccessToken = new(StringComparer.Ordinal);

        public List<string> UsedRefreshTokens { get; } = [];

        public FakeOneDriveGraphClient For(string refreshToken)
        {
            if (!_byRefreshToken.TryGetValue(refreshToken, out var client))
            {
                client = new FakeOneDriveGraphClient();
                _byRefreshToken[refreshToken] = client;
            }

            return client;
        }

        private FakeOneDriveGraphClient Route(string accessToken)
            => _byAccessToken.TryGetValue(accessToken, out var client)
                ? client
                : throw new InvalidOperationException($"未知 access token：{accessToken}");

        public Task<OneDriveDeviceCodeStart> RequestDeviceCodeAsync(string clientId, CancellationToken ct = default)
            => Task.FromResult(new OneDriveDeviceCodeStart("device-code", "USER-CODE", "https://www.microsoft.com/link", 900));

        public Task<OneDriveTokenResult> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OneDriveTokenResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
        {
            UsedRefreshTokens.Add(refreshToken);
            var accessToken = $"access-for-{refreshToken}";
            _byAccessToken[accessToken] = For(refreshToken);
            return Task.FromResult(new OneDriveTokenResult(accessToken, refreshToken, 3600, "Files.ReadWrite.All offline_access"));
        }

        public Task<OneDriveDriveInfo> GetDriveAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(new OneDriveDriveInfo("drive", "personal", 1, 2));

        public Task<OneDriveAccountInfo> GetMeAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult(new OneDriveAccountInfo("acc", "User"));

        public Task<OneDriveDeltaPage> GetDeltaPageAsync(string accessToken, string url, CancellationToken ct = default)
            => Route(accessToken).GetDeltaPageAsync(accessToken, url, ct);

        public Task<string?> GetDownloadUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Route(accessToken).GetDownloadUrlAsync(accessToken, itemId, ct);

        public Task<string?> GetThumbnailUrlAsync(string accessToken, string itemId, string size, CancellationToken ct = default)
            => Route(accessToken).GetThumbnailUrlAsync(accessToken, itemId, size, ct);

        public Task<string?> GetPreviewUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Route(accessToken).GetPreviewUrlAsync(accessToken, itemId, ct);

        public Task<OneDriveSmallContent?> DownloadSmallAsync(string accessToken, string itemId, long maxBytes, CancellationToken ct = default)
            => Route(accessToken).DownloadSmallAsync(accessToken, itemId, maxBytes, ct);

        public Task PutSmallContentAsync(string accessToken, string itemId, byte[] bytes, string contentType, CancellationToken ct = default)
            => Route(accessToken).PutSmallContentAsync(accessToken, itemId, bytes, contentType, ct);

        public Task<string> PatchItemAsync(string accessToken, string itemId, string? newName, string? newParentId, CancellationToken ct = default)
            => Route(accessToken).PatchItemAsync(accessToken, itemId, newName, newParentId, ct);

        public Task DeleteItemAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Route(accessToken).DeleteItemAsync(accessToken, itemId, ct);

        public Task<OneDriveShareLink> CreateShareLinkAsync(
            string accessToken,
            string itemId,
            OneDriveSharePermission permission,
            DateTimeOffset? expiration,
            CancellationToken ct = default)
            => Route(accessToken).CreateShareLinkAsync(accessToken, itemId, permission, expiration, ct);

        public Task RevokeSharePermissionAsync(string accessToken, string itemId, string permissionId, CancellationToken ct = default)
            => Route(accessToken).RevokeSharePermissionAsync(accessToken, itemId, permissionId, ct);

        public Task<IReadOnlyList<OneDriveShareLink>> ListSharePermissionsAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Route(accessToken).ListSharePermissionsAsync(accessToken, itemId, ct);

        public Task<string> PutNewFileByPathAsync(string accessToken, string itemPath, byte[] bytes, string contentType, CancellationToken ct = default)
            => Route(accessToken).PutNewFileByPathAsync(accessToken, itemPath, bytes, contentType, ct);

        public Task<string> CreateFolderAsync(string accessToken, string folderPath, string name, CancellationToken ct = default)
            => Route(accessToken).CreateFolderAsync(accessToken, folderPath, name, ct);

        public Task<OneDrivePathItem?> GetItemByPathAsync(string accessToken, string itemPath, CancellationToken ct = default)
            => Route(accessToken).GetItemByPathAsync(accessToken, itemPath, ct);

        public Task<OneDrivePathItem?> GetItemByIdAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Route(accessToken).GetItemByIdAsync(accessToken, itemId, ct);

        public Task<OneDriveUploadSession> CreateUploadSessionAsync(
            string accessToken,
            string itemPath,
            string fileName,
            CancellationToken ct = default)
            => Route(accessToken).CreateUploadSessionAsync(accessToken, itemPath, fileName, ct);

        public Task<string?> GetItemWebUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
            => Route(accessToken).GetItemWebUrlAsync(accessToken, itemId, ct);
    }
}
