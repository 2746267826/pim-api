namespace Pim.Module.Files.Entities;

public sealed class FileItemEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public FileProviderEntity? Provider { get; set; }
    public string ExternalFileId { get; set; } = string.Empty;
    public string? ParentExternalFileId { get; set; }
    public string Path { get; set; } = "/";
    public string Name { get; set; } = string.Empty;
    public string ItemType { get; set; } = "file";
    public string? MimeType { get; set; }
    public long? Size { get; set; }
    public string? Etag { get; set; }
    public string? ContentHash { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public string? Permissions { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<FileVersionEntity> Versions { get; } = new();
    public List<FileIndexJobEntity> IndexJobs { get; } = new();
    public List<FileChunkEntity> Chunks { get; } = new();
    public List<FileAiResultEntity> AiResults { get; } = new();
    public List<FileSuggestionEntity> Suggestions { get; } = new();
}
