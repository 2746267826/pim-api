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

public sealed record FileProviderBindingStartResult(
    Guid ProviderId,
    string ProviderKey,
    string Mode,
    string? UserCode,
    string? VerificationUri,
    int? ExpiresIn,
    string? AuthorizationUrl)
{
    /// <summary>
    /// 浏览器跳转形态的一次性 state。**只用于让端点把它写进 HttpOnly Cookie 做浏览器绑定**，
    /// 不作为独立字段进响应体（它已经在 authorizationUrl 里）。
    /// </summary>
    public string? State { get; init; }
}

/// <summary>
/// Status: pending | connected | expired | denied | error。
/// <paramref name="ProviderId"/> 是**规范账号行**的 id：同一账号重绑时服务端会复用既有行
/// 并删掉本次的待授权行，因此轮询必须把这个 id 回给客户端，
/// 否则客户端会继续拿已经删掉的那个 id 去轮询/同步，全部报「来源不存在」。
/// </summary>
public sealed record FileProviderBindingStatusResult(
    Guid ProviderId,
    string Status,
    string? DriveId,
    string? AccountId,
    string? AccountName,
    string? UserCode,
    string? VerificationUri,
    DateTimeOffset? DeviceCodeExpiresAt,
    int? PollIntervalSeconds = null);

/// <summary>
/// 通用绑定流程（工单 05 / F1、F2）：设备码轮询与浏览器跳转两种形态共用一套状态机。
///
/// 与旧 <c>OneDriveBindingService</c> 的关键差别（F2）：
/// **每次绑定都新建一行账号**，重绑定不再复用同一条记录 —— 否则「同一 provider 多账号」
/// 从入口就不可能实现。未完成的绑定行（<c>account_id IS NULL</c>）由部分唯一索引放行，
/// 并在启动新一轮绑定时清理掉同 provider 的过期残留，避免脏行堆积。
/// </summary>
public sealed class FileProviderAuthorizationService
{
    /// <summary>授权等待期的有效期上限（设备码由 provider 决定，跳转授权固定 10 分钟）。</summary>
    private static readonly TimeSpan RedirectStateLifetime = TimeSpan.FromMinutes(10);

    private readonly PimDbContext _db;
    private readonly IFileProviderFactory _providers;
    private readonly IEnumerable<IFileProviderAuthorizer> _authorizers;
    private readonly FileProviderCredentialSource _credentials;
    private readonly FileProviderTokenCache _tokenCache;
    private readonly ISecretProtector _protector;
    private readonly ILogger<FileProviderAuthorizationService>? _logger;
    private readonly TimeProvider _clock;

    public FileProviderAuthorizationService(
        PimDbContext db,
        IFileProviderFactory providers,
        IEnumerable<IFileProviderAuthorizer> authorizers,
        ISecretProtector protector,
        ILogger<FileProviderAuthorizationService>? logger = null,
        TimeProvider? clock = null,
        FileProviderCredentialSource? credentials = null,
        FileProviderTokenCache? tokenCache = null)
    {
        _db = db;
        _providers = providers;
        _authorizers = authorizers.ToList();
        _protector = protector;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _credentials = credentials ?? new FileProviderCredentialSource(new ConfigurationBuilder().Build());
        _tokenCache = tokenCache ?? new FileProviderTokenCache();
    }

    /// <summary>发起一轮新的绑定。</summary>
    public async Task<FileProviderBindingStartResult> StartAsync(
        Guid userId,
        string providerKey,
        string? clientId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new DomainException(5324, "需要文件来源类型");
        }

        providerKey = providerKey.Trim().ToLowerInvariant();
        if (!_providers.IsSupported(providerKey))
        {
            throw new DomainException(5323, $"不支持的文件来源类型：{providerKey}");
        }

        var authorizer = ResolveAuthorizer(providerKey);
        var credentials = _credentials.Resolve(providerKey, clientId);
        var effectiveClientId = credentials.ClientId;
        if (authorizer.Mode == FileProviderAuthorizationMode.DeviceCode
            && string.IsNullOrWhiteSpace(effectiveClientId))
        {
            throw new DomainException(5324, "需要 Azure 应用注册的 Client ID");
        }

        await PurgeStalePendingAsync(userId, providerKey, ct);

        var now = _clock.GetUtcNow();
        var provider = new FileProviderEntity
        {
            UserId = userId,
            Provider = providerKey,
            ClientId = effectiveClientId,
            Status = "pending",
            UpdatedAt = now,
        };
        _db.Set<FileProviderEntity>().Add(provider);
        await _db.SaveChangesAsync(ct);

        var request = new FileProviderAuthorizationRequest(
            provider.Id, userId, effectiveClientId, credentials.ClientSecret, credentials.RedirectUri);

        FileProviderAuthorizationChallenge challenge;
        try
        {
            challenge = await authorizer.BeginAsync(request, ct);
        }
        catch
        {
            // 发起失败不留脏行：否则用户会看到一个永远 pending 的账号
            _db.Set<FileProviderEntity>().Remove(provider);
            await _db.SaveChangesAsync(ct);
            throw;
        }

        // 设备码形态存 device code（轮询用），跳转形态存一次性 state（回调校验用）；
        // 两者共用「服务端保存的一次性机密」语义，因此共用一列。
        var secret = challenge.Mode == FileProviderAuthorizationMode.DeviceCode
            ? challenge.DeviceCode
            : challenge.State;
        provider.PendingAuthorizationSecretEncrypted = string.IsNullOrEmpty(secret)
            ? null
            : Encoding.UTF8.GetBytes(_protector.Protect(secret));
        provider.UserCode = challenge.UserCode;
        provider.VerificationUri = challenge.VerificationUri;
        provider.PendingAuthorizationExpiresAt = challenge.Mode == FileProviderAuthorizationMode.DeviceCode
            ? now.AddSeconds(challenge.ExpiresIn ?? 900)
            : now.Add(RedirectStateLifetime);
        provider.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "Files provider binding started for user {UserId} ({ProviderKey}, mode={Mode})",
            userId, providerKey, challenge.Mode);

        return new FileProviderBindingStartResult(
            provider.Id,
            providerKey,
            challenge.Mode == FileProviderAuthorizationMode.DeviceCode ? "device-code" : "browser-redirect",
            challenge.UserCode,
            challenge.VerificationUri,
            challenge.ExpiresIn,
            challenge.AuthorizationUrl)
        {
            State = challenge.State,
        };
    }

    /// <summary>查询绑定状态；设备码形态会顺带轮询一次远端。</summary>
    public async Task<FileProviderBindingStatusResult> GetStatusAsync(
        Guid userId,
        Guid providerId,
        CancellationToken ct = default)
    {
        var provider = await LoadOwnedAsync(userId, providerId, ct);
        var authorizer = ResolveAuthorizer(provider.Provider);

        if (provider.Status != "pending")
        {
            return Connected(provider);
        }

        if (authorizer.Mode != FileProviderAuthorizationMode.DeviceCode)
        {
            // 浏览器跳转形态：状态由回调决定，轮询只回报当前状态
            return new FileProviderBindingStatusResult(
                provider.Id, provider.Status, provider.DriveId, provider.AccountId, provider.AccountName,
                null, null, null);
        }

        if (provider.PendingAuthorizationSecretEncrypted is null
            || provider.PendingAuthorizationExpiresAt is not { } expiresAt
            || expiresAt <= _clock.GetUtcNow())
        {
            provider.Status = "expired";
            provider.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return new FileProviderBindingStatusResult(
                provider.Id, "expired", null, null, null, provider.UserCode, provider.VerificationUri,
                provider.PendingAuthorizationExpiresAt);
        }

        var secretBytes = provider.PendingAuthorizationSecretEncrypted;
        var deviceCode = _protector.Unprotect(Encoding.UTF8.GetString(secretBytes));
        var credentials = _credentials.Resolve(provider.Provider, provider.ClientId);
        var request = new FileProviderAuthorizationRequest(
            provider.Id, userId, credentials.ClientId, credentials.ClientSecret, credentials.RedirectUri);

        try
        {
            var result = await ((IDeviceCodeAuthorizer)authorizer).PollAsync(request, deviceCode, ct);

            // 守卫：远端往返期间若发生重新绑定（一次性机密已换），本轮结果作废，
            // 否则旧凭据会覆盖新一轮绑定并清空新的待授权机密。
            var fresh = await _db.Set<FileProviderEntity>()
                .AsNoTracking()
                .Where(row => row.Id == providerId)
                .Select(row => new { row.Status, row.PendingAuthorizationSecretEncrypted })
                .SingleAsync(ct);
            if (fresh.Status != "pending" || fresh.PendingAuthorizationSecretEncrypted is null
                || !fresh.PendingAuthorizationSecretEncrypted.AsSpan().SequenceEqual(secretBytes))
            {
                return new FileProviderBindingStatusResult(
                    provider.Id, "pending", null, null, null, null, null, null);
            }

            var connected = await MarkConnectedAsync(provider, result, ct);
            return Connected(connected);
        }
        catch (FileProviderAuthorizationPendingException pending)
        {
            return new FileProviderBindingStatusResult(
                provider.Id, "pending", null, null, null, provider.UserCode, provider.VerificationUri,
                provider.PendingAuthorizationExpiresAt, pending.PollIntervalSeconds);
        }
        catch (FileProviderAuthorizationDeniedException)
        {
            await MarkFailedAsync(provider, "denied", ct);
            return new FileProviderBindingStatusResult(provider.Id, "denied", null, null, null, null, null, null);
        }
        catch (FileProviderAuthorizationFailedException)
        {
            await MarkFailedAsync(provider, "expired", ct);
            return new FileProviderBindingStatusResult(provider.Id, "expired", null, null, null, null, null, null);
        }
    }

    /// <summary>
    /// 浏览器跳转形态的回调（无鉴权上下文：浏览器直接跳过来，只有一次性 state 可依据）。
    /// 返回成功连接的账号行 id；state 非法/过期/重放一律抛明确错误。
    /// </summary>
    public async Task<FileProviderEntity> CompleteRedirectAsync(
        string providerKey,
        string? code,
        string? state,
        string? error,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            // Google 在用户拒绝时回 error=access_denied
            throw new FileProviderAuthorizationDeniedException($"Provider returned error: {error}");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new FileProviderAuthorizationFailedException("回调缺少 code 或 state");
        }

        if (!_providers.IsSupported(providerKey))
        {
            throw new DomainException(5323, $"不支持的文件来源类型：{providerKey}");
        }

        var provider = await FindPendingByStateAsync(providerKey, state, ct)
            ?? throw new FileProviderAuthorizationFailedException("授权 state 无效或已过期，请重新发起授权");

        if (provider.PendingAuthorizationExpiresAt is { } expiresAt && expiresAt <= _clock.GetUtcNow())
        {
            await MarkFailedAsync(provider, "expired", ct);
            throw new FileProviderAuthorizationFailedException("授权已过期，请重新发起授权");
        }

        var authorizer = ResolveAuthorizer(providerKey);
        if (authorizer is not IBrowserRedirectAuthorizer redirect)
        {
            throw new ProviderCapabilityNotSupportedException("browser redirect authorization", providerKey);
        }

        var credentials = _credentials.Resolve(providerKey, provider.ClientId);
        var request = new FileProviderAuthorizationRequest(
            provider.Id, provider.UserId, credentials.ClientId, credentials.ClientSecret, credentials.RedirectUri);

        var result = await redirect.CompleteAsync(request, code, state, ct);
        return await MarkConnectedAsync(provider, result, ct);
    }

    /// <summary>切换附件/闪念图片的默认落点账号（F2 补充设计）。</summary>
    public async Task SetDefaultAsync(Guid userId, Guid providerId, CancellationToken ct = default)
    {
        var provider = await LoadOwnedAsync(userId, providerId, ct);
        if (provider.Status != "connected")
        {
            throw new DomainException(5321, "文件来源尚未完成绑定");
        }

        var all = await _db.Set<FileProviderEntity>()
            .Where(row => row.UserId == userId && row.IsDefault)
            .ToListAsync(ct);
        foreach (var row in all)
        {
            row.IsDefault = false;
            row.UpdatedAt = _clock.GetUtcNow();
        }

        provider.IsDefault = true;
        provider.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    public async Task DisconnectAsync(Guid userId, Guid providerId, CancellationToken ct = default)
    {
        var provider = await LoadOwnedAsync(userId, providerId, ct);
        if (provider.Provider == "local") throw new DomainException(5323, "内置存储无需连接，不能断开；可在文件中管理内容");

        var items = await _db.Set<FileItemEntity>()
            .Where(item => item.ProviderId == providerId)
            .ToListAsync(ct);
        _db.Set<FileItemEntity>().RemoveRange(items);
        var wasDefault = provider.IsDefault;
        _db.Set<FileProviderEntity>().Remove(provider);
        await _db.SaveChangesAsync(ct);

        if (wasDefault)
        {
            // 默认账号被删掉后必须补位，否则附件/闪念会「没有落点」
            var replacement = await FileProviderLookup.FindDefaultAsync(_db, userId, ct: ct);
            if (replacement is not null)
            {
                replacement.IsDefault = true;
                replacement.UpdatedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
            }
        }

        _logger?.LogInformation(
            "Files provider disconnected for user {UserId} ({ProviderKey})", userId, provider.Provider);
    }

    /// <summary>
    /// 把授权结果落到账号行上，返回**规范行**（可能与传入的待绑定行不是同一条，见下）。
    ///
    /// 同一账号重绑（用户对同一个网盘账号再点一次「连接」）必须回到同一条记录：
    /// 唯一键 <c>(user_id, provider, account_id)</c> 不允许同一账号存在两行。
    /// 因此这里先把既有账号行的凭据与游标作废并写入新值，再删掉本次的待绑定行 ——
    /// 这与旧实现「重绑定复用同一条记录、旧凭据与游标全部作废」的语义一致，
    /// 同时又允许**另一个账号**开出新的一行（F2 的多账号）。
    /// </summary>
    private async Task<FileProviderEntity> MarkConnectedAsync(
        FileProviderEntity provider,
        FileProviderAuthorizationResult result,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var existingSameAccount = await _db.Set<FileProviderEntity>()
            .FirstOrDefaultAsync(row => row.UserId == provider.UserId
                && row.Provider == result.Account.ProviderKey
                && row.AccountId == result.Account.AccountId
                && row.Id != provider.Id, ct);

        if (existingSameAccount is not null)
        {
            // 与在途刷新串行：否则旧的刷新结果可能在重绑之后覆盖新凭据（复审 Important）。
            using var refreshScope = await _tokenCache.EnterRefreshAsync(existingSameAccount.Id, ct);
            await _db.Entry(existingSameAccount).ReloadAsync(ct);
            ApplyConnection(existingSameAccount, result, now, resetSyncState: true);
            // 换 clientId 重绑时必须一并更新客户端身份：新 refresh token 属于新的客户端，
            // 继续用旧 clientId 刷新会直接 invalid_client（复审 Important）。
            existingSameAccount.ClientId = provider.ClientId ?? existingSameAccount.ClientId;

            // 复用的行可能不是默认落点（例如它在迁移回填时处于 expired、本来是 false）。
            // 若整个用户没有任何默认账号，这里必须补上，否则附件/闪念会「没有落点」。
            var userHasDefault = await _db.Set<FileProviderEntity>()
                .AnyAsync(row => row.UserId == existingSameAccount.UserId
                    && row.IsDefault
                    && row.Id != existingSameAccount.Id, ct);
            if (!userHasDefault)
            {
                existingSameAccount.IsDefault = true;
            }

            _db.Set<FileProviderEntity>().Remove(provider);
            await _db.SaveChangesAsync(ct);
            _tokenCache.Invalidate(existingSameAccount.Id);
            _logger?.LogInformation(
                "Files provider reconnected for user {UserId} ({ProviderKey}, account {AccountId}); existing row reused",
                existingSameAccount.UserId, existingSameAccount.Provider, existingSameAccount.AccountId);
            return existingSameAccount;
        }

        ApplyConnection(provider, result, now, resetSyncState: false);

        var hasDefault = await _db.Set<FileProviderEntity>()
            .AnyAsync(row => row.UserId == provider.UserId && row.IsDefault && row.Id != provider.Id, ct);
        if (!hasDefault)
        {
            // 第一个连接成功的账号自动成为默认落点
            provider.IsDefault = true;
        }

        await _db.SaveChangesAsync(ct);
        _logger?.LogInformation(
            "Files provider connected for user {UserId} ({ProviderKey}, account {AccountId})",
            provider.UserId, provider.Provider, provider.AccountId);
        return provider;
    }

    private void ApplyConnection(
        FileProviderEntity target,
        FileProviderAuthorizationResult result,
        DateTimeOffset now,
        bool resetSyncState)
    {
        target.Status = "connected";
        target.DriveId = result.Account.DriveId;
        target.AccountId = result.Account.AccountId;
        target.AccountName = result.Account.DisplayName;
        target.Provider = result.Account.ProviderKey;
        target.RefreshTokenEncrypted = result.RefreshToken is null
            ? null
            : Encoding.UTF8.GetBytes(_protector.Protect(result.RefreshToken));
        target.TokenExpiresAt = now.AddSeconds(Math.Max(60, result.ExpiresIn - 120));
        target.PendingAuthorizationSecretEncrypted = null;
        target.UserCode = null;
        target.VerificationUri = null;
        target.PendingAuthorizationExpiresAt = null;
        target.LastError = null;
        target.UpdatedAt = now;

        if (resetSyncState)
        {
            // 重绑 = 旧游标作废，下一次同步走全量（与旧实现一致）；
            // 「解析不出父路径」的连续阻塞计数也一并清零，否则会缩短重绑后的重试窗口。
            target.DeltaLink = null;
            target.DeltaResetAt = null;
            target.SyncStatus = "idle";
            target.SyncedItemCount = 0;
            target.DeferredBlockCount = 0;
        }
    }

    private async Task MarkFailedAsync(FileProviderEntity provider, string status, CancellationToken ct)
    {
        provider.Status = status;
        provider.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 按一次性 state 找待授权行。state 是密文存储的，无法直接用 SQL 等值查询，
    /// 因此枚举该 provider 下的 pending 行逐个解密比对 —— pending 行数量天然很小。
    /// </summary>
    private async Task<FileProviderEntity?> FindPendingByStateAsync(
        string providerKey,
        string state,
        CancellationToken ct)
    {
        var candidates = await _db.Set<FileProviderEntity>()
            .Where(row => row.Provider == providerKey
                && row.Status == "pending"
                && row.PendingAuthorizationSecretEncrypted != null)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            string stored;
            try
            {
                stored = _protector.Unprotect(
                    Encoding.UTF8.GetString(candidate.PendingAuthorizationSecretEncrypted!));
            }
            catch (Exception)
            {
                // 解不开的行只可能是损坏数据，跳过而不是把整次回调打成 500
                continue;
            }

            if (CryptographicEquals(stored, state))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool CryptographicEquals(string left, string right)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    /// <summary>
    /// 清理同一用户、同一 provider 下**没成功连接过**的过期授权尝试，避免脏行堆积。
    ///
    /// 判据必须是 <c>AccountId == null</c>（从未拿到账号身份），**不能**只按 status 筛：
    /// 正式账号在令牌失效后会变成 <c>expired</c>，且连接成功时 <c>PendingAuthorizationExpiresAt</c>
    /// 已被清空；若按「非 connected 且待授权已过期」清理，用户一点「重新授权」就会先删掉
    /// 这个账号行，外键级联删掉它全部的 file_items，已发出的闪念附件也会因为
    /// storage_account_id 指向被删行而读不回来（复审 Critical）。
    /// </summary>
    private async Task PurgeStalePendingAsync(Guid userId, string providerKey, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var stale = await _db.Set<FileProviderEntity>()
            .Where(row => row.UserId == userId
                && row.Provider == providerKey
                && row.AccountId == null
                && row.Status != "connected"
                && (row.PendingAuthorizationExpiresAt == null || row.PendingAuthorizationExpiresAt < now))
            .ToListAsync(ct);
        if (stale.Count == 0)
        {
            return;
        }

        _db.Set<FileProviderEntity>().RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    private IFileProviderAuthorizer ResolveAuthorizer(string providerKey)
        => _authorizers.FirstOrDefault(authorizer =>
               string.Equals(authorizer.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
           ?? throw new DomainException(5323, $"不支持的文件来源类型：{providerKey}");

    private async Task<FileProviderEntity> LoadOwnedAsync(Guid userId, Guid providerId, CancellationToken ct)
    {
        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct);
        if (provider is null || provider.UserId != userId)
        {
            throw new DomainException(5320, "文件来源绑定不存在");
        }

        return provider;
    }

    private static FileProviderBindingStatusResult Connected(FileProviderEntity provider)
        => new(
            provider.Id,
            provider.Status,
            provider.DriveId,
            provider.AccountId,
            provider.AccountName,
            null,
            null,
            null);
}
