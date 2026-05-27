namespace Pim.Module.Files.Entities;

public sealed class FileAiResultEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileItemId { get; set; }
    public FileItemEntity? FileItem { get; set; }
    public Guid VersionId { get; set; }
    public FileVersionEntity? Version { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string TagsJson { get; set; } = "[]";
    public string? Language { get; set; }
    public string? Sensitivity { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Model { get; set; }
    public Guid? AiRequestLogId { get; set; }
    public string EvidenceChunkIdsJson { get; set; } = "[]";
}
