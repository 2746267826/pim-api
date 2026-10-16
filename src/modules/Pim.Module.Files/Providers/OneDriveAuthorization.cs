using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Providers;

/// <summary>OneDrive 的令牌刷新（委托给既有 Graph 客户端）。</summary>
public sealed class OneDriveOAuthClient(IOneDriveGraphClient client) : IFileProviderOAuthClient
{
    public string ProviderKey => OneDriveFileProvider.Key;

    public async Task<FileProviderTokenResult> RefreshAsync(
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken ct = default)
    {
        // OneDrive 的公共客户端（consumers）刷新不需要 client secret；参数保留是为了
        // 让 Google（机密客户端）能共用同一抽象。
        _ = clientSecret;
        try
        {
            var result = await client.RefreshAsync(clientId, refreshToken, ct);
            return new FileProviderTokenResult(result.AccessToken, result.RefreshToken, result.ExpiresIn, result.Scope);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode is 400 or 401)
        {
            // invalid_grant / 凭证吊销：重试没有意义，必须引导用户重新授权
            throw new ProviderAuthorizationExpiredException(exception.Message);
        }
        catch (OneDriveGraphException exception)
        {
            throw OneDriveExceptionMapper.Translate(exception);
        }
    }
}

/// <summary>OneDrive 设备码授权（沿用既有设备码端点与轮询语义）。</summary>
public sealed class OneDriveAuthorizer(IOneDriveGraphClient client) : IDeviceCodeAuthorizer
{
    public string ProviderKey => OneDriveFileProvider.Key;

    public FileProviderAuthorizationMode Mode => FileProviderAuthorizationMode.DeviceCode;

    public async Task<FileProviderAuthorizationChallenge> BeginAsync(
        FileProviderAuthorizationRequest request,
        CancellationToken ct = default)
    {
        var clientId = RequireClientId(request);
        var deviceCode = await OneDriveExceptionMapper.GuardAsync(
            () => client.RequestDeviceCodeAsync(clientId, ct));
        return new FileProviderAuthorizationChallenge(
            FileProviderAuthorizationMode.DeviceCode,
            deviceCode.UserCode,
            deviceCode.VerificationUri,
            deviceCode.ExpiresIn,
            DeviceCode: deviceCode.DeviceCode);
    }

    public async Task<FileProviderAuthorizationResult> PollAsync(
        FileProviderAuthorizationRequest request,
        string deviceCode,
        CancellationToken ct = default)
    {
        var clientId = RequireClientId(request);
        OneDriveTokenResult token;
        try
        {
            token = await client.PollDeviceCodeAsync(clientId, deviceCode, ct);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode is 400 or 401)
        {
            throw ClassifyPollFailure(exception.Message);
        }
        catch (OneDriveGraphException exception)
        {
            throw OneDriveExceptionMapper.Translate(exception);
        }

        var drive = await OneDriveExceptionMapper.GuardAsync(() => client.GetDriveAsync(token.AccessToken, ct));
        var me = await OneDriveExceptionMapper.GuardAsync(() => client.GetMeAsync(token.AccessToken, ct));

        return new FileProviderAuthorizationResult(
            new FileProviderAccount(
                OneDriveFileProvider.Key,
                me.AccountId,
                me.DisplayName,
                drive.DriveId,
                OneDriveGraphSemantics.RootExternalId),
            token.RefreshToken,
            token.ExpiresIn);
    }

    private static string RequireClientId(FileProviderAuthorizationRequest request)
        => string.IsNullOrWhiteSpace(request.ClientId)
            ? throw new FileProviderAuthorizationFailedException(
                "Missing Azure application client id for OneDrive")
            : request.ClientId.Trim();

    /// <summary>按 RFC 8628 的 error 码把轮询失败分类（沿用既有实现的分支口径）。</summary>
    private static Exception ClassifyPollFailure(string detail)
    {
        if (detail.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
        {
            return new FileProviderAuthorizationPendingException(null, detail);
        }

        if (detail.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
        {
            // RFC 8628：要求客户端降低轮询频率
            return new FileProviderAuthorizationPendingException(7, detail);
        }

        if (detail.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
        {
            return new FileProviderAuthorizationDeniedException(detail);
        }

        if (detail.Contains("expired_token", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("authorization_expired", StringComparison.OrdinalIgnoreCase))
        {
            return new FileProviderAuthorizationFailedException(detail);
        }

        return new FileProviderAuthorizationPendingException(null, detail);
    }
}
