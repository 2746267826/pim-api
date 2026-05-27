namespace Pim.Module.Files.Entities;

public sealed class FileVersionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileItemId { get; set; }
    public FileItemEntity? FileItem { get; set; }
    public string ExternalVersionId { get; set; } = string.Empty;
    public string? Etag { get; set; }
    public long? Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "history";
    public bool IsCurrent { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}
