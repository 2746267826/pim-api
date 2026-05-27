namespace Pim.Module.Files.Entities;

public sealed class FileIndexJobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileItemId { get; set; }
    public FileItemEntity? FileItem { get; set; }
    public Guid? VersionId { get; set; }
    public FileVersionEntity? Version { get; set; }
    public string Status { get; set; } = "pending";
    public string Stage { get; set; } = "metadata";
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
