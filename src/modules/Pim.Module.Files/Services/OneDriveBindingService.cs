using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

public sealed record OneDriveBindingStartResult(Guid ProviderId, string UserCode, string VerificationUri, int ExpiresIn);

/// <summary>Status: pending | connected | expired | denied。</summary>
public sealed record OneDriveBindingStatusResult(
    string Status,
    string? DriveId,
    string? AccountId,
    string? AccountName,
    string? UserCode,
    string? VerificationUri,
    DateTimeOffset? DeviceCodeExpiresAt);

/// <summary>
/// OneDrive 绑定流程：设备码启动 / 状态轮询（poll-on-demand）/ 断开清理。
/// 每个用户仅允许一条 onedrive 绑定（复用唯一索引语义，重绑定走同一条记录）。
/// </summary>
public sealed class OneDriveBindingService
{
    private readonly PimDbContext _db;
    private readonly IOneDriveGraphClient _client;
    private readonly ISecretProtector _protector;
    private readonly ILogger<OneDriveBindingService>? _logger;
    private readonly TimeProvider _clock;

    public OneDriveBindingService(
        PimDbContext db,
        IOneDriveGraphClient client,
        ISecretProtector protector,
        ILogger<OneDriveBindingService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _client = client;
        _protector = protector;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<OneDriveBindingStartResult> StartBindingAsync(Guid userId, string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new DomainException(5324, "需要 Azure 应用注册的 Client ID");
        }

        clientId = clientId.Trim();
        var provider = await _db.Set<FileProviderEntity>()
            .SingleOrDefaultAsync(p => p.UserId == userId && p.Provider == "onedrive", ct);
        if (provider is null)
        {
            provider = new FileProviderEntity
            {
                UserId = userId,
                Provider = "onedrive",
            };
            _db.Set<FileProviderEntity>().Add(provider);
        }

        var now = _clock.GetUtcNow();
        var deviceCode = await _client.RequestDeviceCodeAsync(clientId, ct);
        provider.ClientId = clientId;
        provider.Status = "pending";
        provider.DeviceCodeEncrypted = Encoding.UTF8.GetBytes(_protector.Protect(deviceCode.DeviceCode));
        provider.UserCode = deviceCode.UserCode;
        provider.VerificationUri = deviceCode.VerificationUri;
        provider.DeviceCodeExpiresAt = now.AddSeconds(deviceCode.ExpiresIn);
        // 重新绑定 = 旧凭据与游标全部作废
        provider.RefreshTokenEncrypted = null;
        provider.TokenExpiresAt = null;
        provider.DeltaLink = null;
        provider.SyncStatus = "idle";
        provider.LastError = null;
        provider.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger?.LogInformation("OneDrive binding started for user {UserId}", userId);
        return new OneDriveBindingStartResult(provider.Id, deviceCode.UserCode, deviceCode.VerificationUri, deviceCode.ExpiresIn);
    }

    public async Task<OneDriveBindingStatusResult> GetBindingStatusAsync(Guid userId, Guid providerId, CancellationToken ct = default)
    {
        var provider = await LoadOwnedAsync(userId, providerId, ct);

        if (provider.Status != "pending")
        {
            return new OneDriveBindingStatusResult(
                provider.Status, provider.DriveId, provider.AccountId, provider.AccountName, null, null, null);
        }

        if (provider.DeviceCodeEncrypted is null
            || provider.DeviceCodeExpiresAt is not { } expiresAt
            || expiresAt <= _clock.GetUtcNow())
        {
            provider.Status = "expired";
            provider.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return new OneDriveBindingStatusResult(
                "expired", null, null, null, provider.UserCode, provider.VerificationUri, provider.DeviceCodeExpiresAt);
        }

        var deviceCode = _protector.Unprotect(Encoding.UTF8.GetString(provider.DeviceCodeEncrypted));
        try
        {
            var token = await _client.PollDeviceCodeAsync(provider.ClientId ?? string.Empty, deviceCode, ct);
            var drive = await _client.GetDriveAsync(token.AccessToken, ct);
            var me = await _client.GetMeAsync(token.AccessToken, ct);

            var now = _clock.GetUtcNow();
            provider.Status = "connected";
            provider.DriveId = drive.DriveId;
            provider.AccountId = me.AccountId;
            provider.AccountName = me.DisplayName;
            provider.RefreshTokenEncrypted = token.RefreshToken is null
                ? null
                : Encoding.UTF8.GetBytes(_protector.Protect(token.RefreshToken));
            provider.TokenExpiresAt = now.AddSeconds(Math.Max(60, token.ExpiresIn - 120));
            provider.DeviceCodeEncrypted = null;
            provider.UserCode = null;
            provider.VerificationUri = null;
            provider.DeviceCodeExpiresAt = null;
            provider.LastError = null;
            provider.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            _logger?.LogInformation("OneDrive binding connected for user {UserId}", userId);
            return new OneDriveBindingStatusResult(
                "connected", provider.DriveId, provider.AccountId, provider.AccountName, null, null, null);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode is 400 or 401)
        {
            var detail = exception.Message;
            if (detail.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                return new OneDriveBindingStatusResult(
                    "pending", null, null, null, provider.UserCode, provider.VerificationUri, provider.DeviceCodeExpiresAt);
            }
            if (detail.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                return new OneDriveBindingStatusResult(
                    "pending", null, null, null, provider.UserCode, provider.VerificationUri, provider.DeviceCodeExpiresAt);
            }
            if (detail.Contains("expired_token", StringComparison.OrdinalIgnoreCase))
            {
                provider.Status = "expired";
                provider.UpdatedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return new OneDriveBindingStatusResult(
                    "expired", null, null, null, null, null, null);
            }
            if (detail.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
            {
                provider.Status = "denied";
                provider.UpdatedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return new OneDriveBindingStatusResult(
                    "denied", null, null, null, null, null, null);
            }

            throw;
        }
    }

    public async Task DisconnectAsync(Guid userId, Guid providerId, CancellationToken ct = default)
    {
        var provider = await LoadOwnedAsync(userId, providerId, ct);
        if (provider.Provider != "onedrive")
        {
            throw new DomainException(5323, "该文件来源不是 OneDrive");
        }

        var items = await _db.Set<FileItemEntity>()
            .Where(item => item.ProviderId == providerId)
            .ToListAsync(ct);
        _db.Set<FileItemEntity>().RemoveRange(items);
        _db.Set<FileProviderEntity>().Remove(provider);
        await _db.SaveChangesAsync(ct);
        _logger?.LogInformation("OneDrive binding disconnected for user {UserId}", userId);
    }

    private async Task<FileProviderEntity> LoadOwnedAsync(Guid userId, Guid providerId, CancellationToken ct)
    {
        var provider = await _db.Set<FileProviderEntity>().SingleOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new DomainException(5320, "OneDrive 绑定不存在");
        if (provider.UserId != userId)
        {
            throw new DomainException(5320, "OneDrive 绑定不存在");
        }

        return provider;
    }
}
