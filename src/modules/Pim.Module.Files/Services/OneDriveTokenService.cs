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
/// OneDrive access token 获取：共享内存缓存（单例，跨请求生效）+ 过期前刷新；
/// refresh token 只以密文落库。刷新在每 provider 锁内串行，锁内重读数据库现值，
/// 避免 Graph 轮换 refresh token 后并发刷新者用旧值覆盖或误置 expired（复审 I2）。
/// 刷新失败（invalid_grant 等）会把绑定状态置为 expired，引导用户重新绑定。
/// </summary>
public sealed class OneDriveTokenService
{
    private const int ExpiryBufferSeconds = 120;

    private readonly PimDbContext _db;
    private readonly IOneDriveGraphClient _client;
    private readonly ISecretProtector _protector;
    private readonly OneDriveTokenCache _cache;
    private readonly ILogger<OneDriveTokenService>? _logger;
    private readonly TimeProvider _clock;

    public OneDriveTokenService(
        PimDbContext db,
        IOneDriveGraphClient client,
        ISecretProtector protector,
        ILogger<OneDriveTokenService>? logger = null,
        TimeProvider? clock = null,
        OneDriveTokenCache? cache = null)
    {
        _db = db;
        _client = client;
        _protector = protector;
        _cache = cache ?? new OneDriveTokenCache();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(Guid providerId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValid(providerId, now.AddSeconds(ExpiryBufferSeconds), out var cachedToken))
        {
            return cachedToken;
        }

        using var refreshScope = await _cache.EnterRefreshAsync(providerId, ct);
        now = _clock.GetUtcNow();
        if (_cache.TryGetValid(providerId, now.AddSeconds(ExpiryBufferSeconds), out cachedToken))
        {
            // 等锁期间别的请求已完成刷新（用等锁后的时钟判断，复审 M-1）
            return cachedToken;
        }

        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new DomainException(5320, "OneDrive 绑定不存在");
        if (provider.Provider != "onedrive" || provider.RefreshTokenEncrypted is not { Length: > 0 })
        {
            throw new DomainException(5321, "OneDrive 未绑定或绑定不完整");
        }

        // 拿到锁后重读数据库现值：并发赢家可能已轮换 refresh token 并落库
        await _db.Entry(provider).ReloadAsync(ct);
        if (provider.Status != "connected" || provider.RefreshTokenEncrypted is not { Length: > 0 })
        {
            // Reload 后绑定可能已被重绑（凭据置空）或失效（复审 I-1），不能用旧值续跑
            throw new DomainException(5321, "OneDrive 绑定状态已变化，请刷新后重试");
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
            _cache.Invalidate(providerId);
            throw new DomainException(5322, "OneDrive 授权已失效，请重新绑定");
        }

        provider.RefreshTokenEncrypted = Encoding.UTF8.GetBytes(
            _protector.Protect(refreshed.RefreshToken ?? refreshToken));
        provider.TokenExpiresAt = _clock.GetUtcNow().AddSeconds(Math.Max(60, refreshed.ExpiresIn - ExpiryBufferSeconds));
        provider.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        var accessToken = refreshed.AccessToken;
        _cache.Set(providerId, accessToken, _clock.GetUtcNow().AddSeconds(refreshed.ExpiresIn));
        _logger?.LogDebug("OneDrive access token refreshed for provider {ProviderId}", providerId);
        return accessToken;
    }

    public void InvalidateCached(Guid providerId)
    {
        _cache.Invalidate(providerId);
    }
}
