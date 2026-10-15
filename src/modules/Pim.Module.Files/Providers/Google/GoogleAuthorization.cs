using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Providers.Google;

/// <summary>
/// 把 Google 的 HTTP 错误翻译成通用抽象异常。
///
/// 关键点：Google 的 403 既可能是「限流」（可重试）、也可能是「权限不足」（不可重试），
/// 只看状态码必然误判，因此必须解析错误体里的 <c>reason</c>。
/// </summary>
public static class GoogleErrorMapper
{
    /// <summary>从令牌端点的失败响应构造异常。invalid_grant = 授权已失效，必须重新授权。</summary>
    public static Exception FromTokenFailure(HttpStatusCode status, string body)
    {
        var reason = ReadErrorReason(body);
        var message = ReadErrorMessage(body) ?? "Google token request failed";

        if (reason is "invalid_grant" or "invalid_client" or "unauthorized_client")
        {
            // 工单 F4.5：refresh token 失效（例如 Testing 发布状态下 7 天过期）走这里，
            // 与 F4.3 的 change token 失效是**两回事**。
            return new ProviderAuthorizationExpiredException($"{message} ({reason})");
        }

        if ((int)status == 401)
        {
            return new ProviderUnauthorizedException(message);
        }

        return status is HttpStatusCode.TooManyRequests || (int)status >= 500
            ? new ProviderTransientException((int)status, null, message)
            : new GoogleDriveException((int)status, reason, null, message, Truncate(body));
    }

    /// <summary>从一般 Drive 端点的失败响应构造异常（需要读体，因此是异步）。</summary>
    public static async Task<Exception> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var retryAfter = response.Headers.RetryAfter?.Delta is { } delta
            ? (int)Math.Ceiling(delta.TotalSeconds)
            : (int?)null;
        return FromBody((int)response.StatusCode, body, retryAfter);
    }

    /// <summary>
    /// 按状态码 + reason 分类。
    ///
    /// 分类用的是**全部 reason**而不是第一个：Google 的响应常常**新旧结构并存**
    /// （`error.errors[].reason` 给一个泛化的 `forbidden`，真正的根因在
    /// `error.details[].reason` 里，例如 `SERVICE_DISABLED`）。
    /// 只取第一个 reason 会让运维类根因被泛化 reason 遮蔽，退化回「用户权限」的语义（复审 Important）。
    /// </summary>
    public static Exception FromBody(int statusCode, string body, int? retryAfterSeconds = null)
    {
        var reasons = ReadErrorReasons(body);
        var reason = reasons.Count > 0 ? reasons[0] : null;
        var message = ReadErrorMessage(body) ?? $"Google Drive request failed with status {statusCode}";

        if (IsCursorFailure(statusCode, reason, message))
        {
            return new ProviderCursorExpiredException(message);
        }

        return statusCode switch
        {
            401 => new ProviderUnauthorizedException(message),
            // 顺序即优先级，且判定用**全部 reason**（新旧结构常并存，只看第一个会遮蔽根因）：
            // ① 运维/配置（只有运维能修，必须进监控）
            // ② 限流（可退避重试） ③ 空间配额（用户可清理） ④ 其余一律权限兜底
            403 when reasons.Any(IsProjectConfigurationFailure) =>
                new ProviderConfigurationFailureException(
                    $"{message}（Google 侧配置/运维问题：{FirstMatching(reasons, IsProjectConfigurationFailure)}）"),
            403 when reasons.Any(IsRateLimit) =>
                new ProviderTransientException(429, retryAfterSeconds, message),
            403 when reasons.Any(IsStorageQuota) =>
                new ProviderStorageQuotaExceededException(
                    $"{message}（Google 网盘空间或配额不足：{FirstMatching(reasons, IsStorageQuota)}）"),
            // 其余 403 一律按「权限/授权」处理，**不再落回上游故障**：
            // 403 的语义就是「调用方没有被允许」，它永远不是「网盘挂了」；报成 502 会把
            // 「你该去重新连接 / 检查该文件权限」误导成「等一会儿再试」。
            // 逐个 reason 打补丁会没完没了（已知的就有 insufficientPermissions、
            // insufficientFilePermissions、cannotModifyInheritedTeamDrivePermission、
            // appNotAuthorizedToFile、domainPolicy、fileNotDownloadable、cannotDownloadAbusiveFile、
            // abuse……），因此这里用「默认归入权限类」收口。
            403 => new ProviderAuthorizationExpiredException(
                $"{message}（Google 拒绝了该操作：{reason ?? "forbidden"}；"
                + "请到设置页重新连接该账号或检查该文件的权限）"),
            404 => new ProviderItemNotFoundException(message),
            410 => new ProviderCursorExpiredException(message),
            429 => new ProviderTransientException(429, retryAfterSeconds, message),
            >= 500 => new ProviderTransientException(statusCode, retryAfterSeconds, message),
            _ => new GoogleDriveException(statusCode, reason, retryAfterSeconds, message, Truncate(body)),
        };
    }

    /// <summary>取第一个命中的 reason（用于给用户/日志一个具体名字）。</summary>
    private static string? FirstMatching(IReadOnlyList<string> reasons, Func<string?, bool> predicate)
        => reasons.FirstOrDefault(predicate) ?? reasons.FirstOrDefault();

    /// <summary>
    /// 项目级/运维级 403：Google 项目未启用 Drive API（<c>accessNotConfigured</c> /
    /// 新式 <c>ErrorInfo.reason=SERVICE_DISABLED</c>）、项目被停用或受限
    /// （<c>projectNotAuthorized</c> / <c>clientProjectBlocked</c> / <c>CONSUMER_SUSPENDED</c>）。
    ///
    /// 这类问题**用户侧无法自愈**（重新授权、检查文件权限都没用），必须让运维看到，
    /// 因此不能归入「权限类 400」。
    /// </summary>
    private static bool IsProjectConfigurationFailure(string? reason)
        => reason is "accessNotConfigured" or "projectNotAuthorized" or "clientProjectBlocked"
            or "billingNotEnabled" or "SERVICE_DISABLED" or "CONSUMER_SUSPENDED"
            or "serviceDisabled" or "consumerSuspended";

    /// <summary>
    /// 空间/配额不足（与 <see cref="IsRateLimit"/> 的区别：限流等一会儿能好，配额要用户自己清理）。
    /// </summary>
    private static bool IsStorageQuota(string? reason)
        => reason is "storageQuotaExceeded" or "teamDriveFileLimitExceeded";

    private static bool IsRateLimit(string? reason)
        => reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded"
            or "dailyLimitExceeded" or "sharingRateLimitExceeded";

    /// <summary>
    /// 游标失效判定**必须限定状态码**：只有 410（Drive 明确表示 token 过期）与
    /// 400 + 游标类 reason 才算。否则一个带 "pageToken" 字样的 403/429/503
    /// 会被误判成游标失效，从而绕过退避重试与权限分类（复审 Minor）。
    /// </summary>
    private static bool IsCursorFailure(int statusCode, string? reason, string message)
    {
        if (statusCode == 410)
        {
            return true;
        }

        if (statusCode != 400)
        {
            return false;
        }

        return (reason is not null && reason.Contains("pageToken", StringComparison.OrdinalIgnoreCase))
               || message.Contains("page token", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 收集错误体里**全部**可读的 reason（先旧式 <c>error.errors[]</c>，再新式
    /// <c>error.details[]</c> 的 ErrorInfo），去重保序。分类时按整组判定，避免漏掉根因。
    /// </summary>
    internal static IReadOnlyList<string> ReadErrorReasons(string body)
    {
        var reasons = new List<string>();
        foreach (var element in EnumerateErrorEntries(body))
        {
            var reason = GoogleDriveClient.ReadString(element, "reason");
            if (!string.IsNullOrWhiteSpace(reason) && !reasons.Contains(reason, StringComparer.Ordinal))
            {
                reasons.Add(reason);
            }
        }

        foreach (var detail in EnumerateErrorDetails(body))
        {
            var reason = GoogleDriveClient.ReadString(detail, "reason");
            if (!string.IsNullOrWhiteSpace(reason) && !reasons.Contains(reason, StringComparer.Ordinal))
            {
                reasons.Add(reason);
            }
        }

        var flat = ReadFlatError(body);
        if (!string.IsNullOrWhiteSpace(flat) && !reasons.Contains(flat, StringComparer.Ordinal))
        {
            reasons.Add(flat);
        }

        return reasons;
    }

    /// <summary>保留「第一个 reason」的便捷读法（消息与既有调用点用）。</summary>
    internal static string? ReadErrorReason(string body)
        => ReadErrorReasons(body).FirstOrDefault();

    /// <summary>读取 <c>error.details[]</c>（只返回对象元素，防御异常形状）。</summary>
    private static IEnumerable<JsonElement> EnumerateErrorDetails(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!TryGetErrorObject(document.RootElement, out var error)
                || !error.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var element in details.EnumerateArray())
            {
                // 数组元素可能是 null/字符串/数字：只认对象，别让解析本身抛异常
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // `@type` 缺失/为空时**仍然采纳**：这里刻意「宁可多认，不可漏认」——
                // 漏掉一个运维类 reason 会让它静默退化成「用户权限」文案，
                // 而多认一个未知 reason 只是走权限兜底（同样可自愈）。
                // 但若给了 `@type`，就必须是 ErrorInfo 才认，避免把别的详情类型当错误码。
                var type = GoogleDriveClient.ReadString(element, "@type");
                if (type is { Length: > 0 }
                    && !type.EndsWith("google.rpc.ErrorInfo", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return element.Clone();
            }
        }
    }

    /// <summary>
    /// 取 <c>error</c> 对象。合法 JSON 里 <c>error</c> 也可能是 null / 字符串 / 数组，
    /// 不检查 ValueKind 会直接抛 <see cref="InvalidOperationException"/>，
    /// 最终变成 500（复审 Minor）。
    /// </summary>
    private static bool TryGetErrorObject(JsonElement root, out JsonElement error)
    {
        error = default;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("error", out var candidate)
            || candidate.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        error = candidate;
        return true;
    }

    internal static string? ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error))
            {
                return null;
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            var message = GoogleDriveClient.ReadString(error, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应（网关错误页等）
        }

        return null;
    }

    private static string? ReadFlatError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateErrorEntries(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!TryGetErrorObject(document.RootElement, out var error)
                || !error.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var element in errors.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    yield return element.Clone();
                }
            }
        }
    }

    private static string Truncate(string body)
        => body.Length <= 512 ? body : body[..512];
}

/// <summary>Google 的令牌刷新（机密客户端：需要 client secret）。</summary>
public sealed class GoogleOAuthClient(GoogleDriveClient client) : IFileProviderOAuthClient
{
    public string ProviderKey => GoogleFileProvider.Key;

    public async Task<FileProviderTokenResult> RefreshAsync(
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            // 不允许静默失败：缺 secret 时刷新必然 401，不如当场说清楚
            throw new ProviderAuthorizationExpiredException(
                "Google OAuth client secret 未配置（PIM_FILES_GOOGLE_CLIENT_SECRET），无法刷新令牌");
        }

        var result = await client.RefreshAsync(clientId, clientSecret, refreshToken, ct);
        return new FileProviderTokenResult(result.AccessToken, result.RefreshToken, result.ExpiresIn, result.Scope);
    }
}

/// <summary>
/// Google 的**浏览器跳转授权**（工单 F4.1，维护者拍板采用）。
///
/// ⚠️ scope 是 <c>https://www.googleapis.com/auth/drive</c>（restricted），
/// **不是** Microsoft Graph 的 <c>Files.ReadWrite.All</c>：照抄后者会直接拿到
/// <c>invalid_scope</c>，Google 接入当场卡死。个人自用不需要上架审核。
///
/// <c>access_type=offline</c> + <c>prompt=consent</c> 是**必须具备**的：
/// 少了前者拿不到 refresh token（每次授权一小时后掉线），
/// 少了后者在用户已授权过时 Google 不会重新下发 refresh token。
/// </summary>
public sealed class GoogleAuthorizer(GoogleDriveClient client) : IBrowserRedirectAuthorizer
{
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>整个网盘读写（restricted scope，个人自用）。</summary>
    public const string DriveScope = "https://www.googleapis.com/auth/drive";

    /// <summary>
    /// 回调路径（与工单给定的 <c>https://home.hsww.party:15858/api/v1/files/google/callback</c>
    /// 的 path 部分一致）。端点按它挂载；配置的 redirect uri 若指向别的 path，
    /// 会在发起授权时**明确报错**（见 <see cref="BeginAsync"/>），而不是等 Google 回一个
    /// <c>redirect_uri_mismatch</c> 或回调落到没有路由的地方。
    /// </summary>
    public const string CallbackPathValue = "/api/v1/files/google/callback";

    public string ProviderKey => GoogleFileProvider.Key;

    public FileProviderAuthorizationMode Mode => FileProviderAuthorizationMode.BrowserRedirect;

    public string CallbackPath => CallbackPathValue;

    public Task<FileProviderAuthorizationChallenge> BeginAsync(
        FileProviderAuthorizationRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            throw new FileProviderAuthorizationFailedException(
                "Google Drive 未配置：需要 PIM_FILES_GOOGLE_CLIENT_ID / PIM_FILES_GOOGLE_CLIENT_SECRET");
        }

        var redirectUri = request.RedirectUri;
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            throw new FileProviderAuthorizationFailedException("Google Drive 未配置回调地址");
        }

        if (!redirectUri.Contains(CallbackPathValue, StringComparison.Ordinal))
        {
            throw new FileProviderAuthorizationFailedException(
                $"Google 回调地址必须包含 {CallbackPathValue}（当前为 {redirectUri}）；"
                + "该地址需同时在 Google Cloud Console 登记为 Authorized redirect URI");
        }

        // 一次性 state：服务端加密保存，回调时按密文比对（防伪造、防重放）
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var url = new System.Text.StringBuilder(AuthorizationEndpoint)
            .Append("?client_id=").Append(Uri.EscapeDataString(request.ClientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&response_type=code")
            .Append("&scope=").Append(Uri.EscapeDataString(DriveScope))
            .Append("&access_type=offline")
            .Append("&prompt=consent")
            .Append("&include_granted_scopes=true")
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .ToString();

        return Task.FromResult(new FileProviderAuthorizationChallenge(
            FileProviderAuthorizationMode.BrowserRedirect,
            AuthorizationUrl: url,
            State: state));
    }

    public async Task<FileProviderAuthorizationResult> CompleteAsync(
        FileProviderAuthorizationRequest request,
        string code,
        string state,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            throw new FileProviderAuthorizationFailedException(
                "Google Drive 未配置：需要 PIM_FILES_GOOGLE_CLIENT_ID / PIM_FILES_GOOGLE_CLIENT_SECRET");
        }

        if (string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            throw new FileProviderAuthorizationFailedException("Google Drive 未配置回调地址");
        }

        // state 的**权威校验**在 FileProviderAuthorizationService（它按密文比对并检查过期）；
        // 这里只做一次形状检查，避免把空值送去换令牌。
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new FileProviderAuthorizationFailedException("回调缺少 state");
        }

        var token = await client.ExchangeCodeAsync(
            request.ClientId,
            request.ClientSecret,
            code,
            request.RedirectUri,
            ct);

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            // 没有 refresh token = 一小时后必然掉线。明确失败，不落一条「看起来连上了」的绑定。
            throw new FileProviderAuthorizationFailedException(
                "Google 未返回 refresh token（请确认授权时使用了 prompt=consent 并重新授权）");
        }

        var user = await client.GetAboutUserAsync(token.AccessToken, ct);
        return new FileProviderAuthorizationResult(
            new FileProviderAccount(GoogleFileProvider.Key, user.Subject, user.DisplayName, null, GoogleFileProvider.RootId),
            token.RefreshToken,
            token.ExpiresIn);
    }
}
