using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Services;

/// <summary>
/// 通用 access token 获取（工单 05 / F1）：共享内存缓存（单例，跨请求生效）+ 过期前刷新；
/// refresh token 只以密文落库。刷新在**每账号**锁内串行，锁内重读数据库现值，
/// 避免 provider 轮换 refresh token 后并发刷新者用旧值覆盖或误置 expired。
///
/// 多账号隔离（F2.4）：缓存与刷新锁的键都是**账号行 id**，因此同 provider 的多个账号
/// 各自持有独立令牌与独立刷新锁，互不干扰。
///
/// 与 provider 无关：刷新协议由 <see cref="IFileProviderOAuthClient"/> 实现提供。
/// </summary>
public sealed class FileProviderTokenService
{
    private const int ExpiryBufferSeconds = 120;

    private readonly PimDbContext _db;
    private readonly IReadOnlyList<IFileProviderOAuthClient> _oauthClients;
    private readonly FileProviderCredentialSource _credentials;
    private readonly ISecretProtector _protector;
    private readonly FileProviderTokenCache _cache;
    private readonly ILogger<FileProviderTokenService>? _logger;
    private readonly TimeProvider _clock;

    public FileProviderTokenService(
        PimDbContext db,
        IEnumerable<IFileProviderOAuthClient> oauthClients,
        ISecretProtector protector,
        ILogger<FileProviderTokenService>? logger = null,
        TimeProvider? clock = null,
        FileProviderTokenCache? cache = null,
        FileProviderCredentialSource? credentials = null)
    {
        _db = db;
        _oauthClients = oauthClients.ToList();
        _protector = protector;
        _cache = cache ?? new FileProviderTokenCache();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _credentials = credentials ?? new FileProviderCredentialSource(new ConfigurationBuilder().Build());
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
            // 等锁期间别的请求已完成刷新（用等锁后的时钟判断）
            return cachedToken;
        }

        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new DomainException(5320, "文件来源绑定不存在");
        var oauth = ResolveOAuthClient(provider.Provider);
        if (oauth is null || provider.RefreshTokenEncrypted is not { Length: > 0 })
        {
            throw new DomainException(5321, "文件来源未绑定或绑定不完整");
        }

        // 拿到锁后重读数据库现值：并发赢家可能已轮换 refresh token 并落库
        await _db.Entry(provider).ReloadAsync(ct);
        if (provider.Status != "connected" || provider.RefreshTokenEncrypted is not { Length: > 0 })
        {
            // Reload 后绑定可能已被重绑（凭据置空）或失效，不能用旧值续跑
            throw new DomainException(5321, "文件来源绑定状态已变化，请刷新后重试");
        }

        var refreshToken = _protector.Unprotect(Encoding.UTF8.GetString(provider.RefreshTokenEncrypted));
        var credentials = _credentials.Resolve(provider.Provider, provider.ClientId);
        FileProviderTokenResult refreshed;
        try
        {
            refreshed = await oauth.RefreshAsync(
                credentials.ClientId ?? string.Empty,
                credentials.ClientSecret,
                refreshToken,
                ct);
        }
        catch (ProviderAuthorizationExpiredException)
        {
            await MarkExpiredAsync(provider, ct);
            throw new DomainException(5322, "文件来源授权已失效，请重新绑定");
        }

        provider.RefreshTokenEncrypted = Encoding.UTF8.GetBytes(
            _protector.Protect(refreshed.RefreshToken ?? refreshToken));
        provider.TokenExpiresAt = _clock.GetUtcNow().AddSeconds(Math.Max(60, refreshed.ExpiresIn - ExpiryBufferSeconds));
        provider.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        var accessToken = refreshed.AccessToken;
        _cache.Set(providerId, accessToken, _clock.GetUtcNow().AddSeconds(refreshed.ExpiresIn));
        _logger?.LogDebug(
            "Access token refreshed for provider {ProviderId} ({ProviderKey})",
            providerId, provider.Provider);
        return accessToken;
    }

    public void InvalidateCached(Guid providerId)
    {
        _cache.Invalidate(providerId);
    }

    /// <summary>把绑定置为 expired 并清缓存（授权失效的唯一出口，避免各处重复实现）。</summary>
    public async Task MarkExpiredAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct);
        if (provider is null)
        {
            return;
        }

        await MarkExpiredAsync(provider, ct);
    }

    private async Task MarkExpiredAsync(FileProviderEntity provider, CancellationToken ct)
    {
        provider.Status = "expired";
        provider.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        _cache.Invalidate(provider.Id);
    }

    private IFileProviderOAuthClient? ResolveOAuthClient(string? providerKey)
        => _oauthClients.FirstOrDefault(client =>
            string.Equals(client.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
}
