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

    /// <summary>按状态码 + reason 分类。</summary>
    public static Exception FromBody(int statusCode, string body, int? retryAfterSeconds = null)
    {
        var reason = ReadErrorReason(body);
        var message = ReadErrorMessage(body) ?? $"Google Drive request failed with status {statusCode}";

        if (IsCursorFailure(statusCode, reason, message))
        {
            return new ProviderCursorExpiredException(message);
        }

        return statusCode switch
        {
            401 => new ProviderUnauthorizedException(message),
            403 when IsRateLimit(reason) =>
                new ProviderTransientException(429, retryAfterSeconds, message),
            // 权限类 403 是**用户可自愈**的（重新授权 / 检查该文件的共享权限），
            // 不能报成 502「网盘服务暂时不可用」——那会把「你该去重新连接」误导成「等一会儿再试」。
            403 when IsPermissionDenied(reason) =>
                new ProviderAuthorizationExpiredException(
                    $"{message}（Google 拒绝了该操作：{reason}；请到设置页重新连接该账号或检查文件权限）"),
            403 => new GoogleDriveException(statusCode, reason, retryAfterSeconds, message, Truncate(body)),
            404 => new ProviderItemNotFoundException(message),
            410 => new ProviderCursorExpiredException(message),
            429 => new ProviderTransientException(429, retryAfterSeconds, message),
            >= 500 => new ProviderTransientException(statusCode, retryAfterSeconds, message),
            _ => new GoogleDriveException(statusCode, reason, retryAfterSeconds, message, Truncate(body)),
        };
    }

    /// <summary>
    /// 权限被拒的 403：这类是「授权/共享权限」问题，用户能自己修（重新连接或调整权限）。
    /// </summary>
    private static bool IsPermissionDenied(string? reason)
        => reason is "insufficientPermissions" or "appNotAuthorizedToFile" or "domainPolicy"
            or "forbidden" or "fileNotDownloadable" or "cannotDownloadAbusiveFile";

    private static bool IsRateLimit(string? reason)
        => reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded"
            or "dailyLimitExceeded" or "sharingRateLimitExceeded";

    private static bool IsCursorFailure(int statusCode, string? reason, string message)
    {
        if (statusCode == 410)
        {
            return true;
        }

        if (reason is not null
            && reason.Contains("pageToken", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return statusCode == 400
               && message.Contains("page token", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ReadErrorReason(string body)
    {
        foreach (var element in EnumerateErrorEntries(body))
        {
            var reason = GoogleDriveClient.ReadString(element, "reason");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                return reason;
            }
        }

        // 有些端点（令牌端点）用扁平的 "error": "invalid_grant"
        var flat = ReadFlatError(body);
        return flat;
    }

    internal static string? ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var error))
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
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
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
            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var element in errors.EnumerateArray())
            {
                yield return element.Clone();
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
