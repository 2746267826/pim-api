namespace Pim.Module.Files.Entities;

public sealed class FileProviderEntity
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

    public List<FileItemEntity> Items { get; } = new();
}
