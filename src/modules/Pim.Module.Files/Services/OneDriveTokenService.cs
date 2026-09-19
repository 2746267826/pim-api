using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

/// <summary>
/// OneDrive access token 获取：内存缓存 + 过期前刷新；refresh token 只以密文落库。
/// 刷新失败（invalid_grant 等）会把绑定状态置为 expired，引导用户重新绑定。
/// </summary>
public sealed class OneDriveTokenService
{
    private const int ExpiryBufferSeconds = 120;

    private readonly PimDbContext _db;
    private readonly IOneDriveGraphClient _client;
    private readonly ISecretProtector _protector;
    private readonly ILogger<OneDriveTokenService>? _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, (string AccessToken, DateTimeOffset ExpiresAt)> _memoryCache = new();

    public OneDriveTokenService(
        PimDbContext db,
        IOneDriveGraphClient client,
        ISecretProtector protector,
        ILogger<OneDriveTokenService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _client = client;
        _protector = protector;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(Guid providerId, CancellationToken ct = default)
    {
        if (_memoryCache.TryGetValue(providerId, out var cached)
            && cached.ExpiresAt > _clock.GetUtcNow().AddSeconds(ExpiryBufferSeconds))
        {
            return cached.AccessToken;
        }

        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new DomainException(5320, "OneDrive 绑定不存在");
        if (provider.Provider != "onedrive" || provider.RefreshTokenEncrypted is not { Length: > 0 })
        {
            throw new DomainException(5321, "OneDrive 未绑定或绑定不完整");
        }

        var refreshToken = _protector.Unprotect(Encoding.UTF8.GetString(provider.RefreshTokenEncrypted));
        OneDriveTokenResult refreshed;
        try
        {
            refreshed = await _client.RefreshAsync(provider.ClientId ?? string.Empty, refreshToken, ct);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode is 400 or 401)
        {
            provider.Status = "expired";
            provider.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            _memoryCache.TryRemove(providerId, out _);
            throw new DomainException(5322, "OneDrive 授权已失效，请重新绑定");
        }

        provider.RefreshTokenEncrypted = Encoding.UTF8.GetBytes(
            _protector.Protect(refreshed.RefreshToken ?? refreshToken));
        provider.TokenExpiresAt = _clock.GetUtcNow().AddSeconds(Math.Max(60, refreshed.ExpiresIn - ExpiryBufferSeconds));
        provider.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        var accessToken = refreshed.AccessToken;
        _memoryCache[providerId] = (accessToken, _clock.GetUtcNow().AddSeconds(refreshed.ExpiresIn));
        _logger?.LogDebug("OneDrive access token refreshed for provider {ProviderId}", providerId);
        return accessToken;
    }

    public void InvalidateCached(Guid providerId)
    {
        _memoryCache.TryRemove(providerId, out _);
    }
}
