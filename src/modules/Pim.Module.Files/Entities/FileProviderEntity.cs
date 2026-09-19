using Pim.Core.Data;

namespace Pim.Module.Files.Entities;

public sealed class FileProviderEntity : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "nextcloud";
    public string BaseUrl { get; set; } = string.Empty;
    public string? InternalBaseUrl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string AppPasswordSecret { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ---- OneDrive（Graph）绑定字段 ----
    public string? ClientId { get; set; }
    public string? DriveId { get; set; }
    public string? AccountId { get; set; }
    public string? AccountName { get; set; }

    /// <summary>delta 游标；null 表示下一次同步走全量。</summary>
    public string? DeltaLink { get; set; }
    public DateTimeOffset? DeltaResetAt { get; set; }

    /// <summary>ISecretProtector 加密后的 refresh token；明文永不落库。</summary>
    public byte[]? RefreshTokenEncrypted { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>idle | syncing | error（同步健康状态，区别于绑定状态 Status）。</summary>
    public string SyncStatus { get; set; } = "idle";
    public long SyncedItemCount { get; set; }

    /// <summary>绑定等待期临时持有的加密设备码；绑定完成后即清除。</summary>
    public byte[]? DeviceCodeEncrypted { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }
    public DateTimeOffset? DeviceCodeExpiresAt { get; set; }

    public List<FileItemEntity> Items { get; } = new();
}
