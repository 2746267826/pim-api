namespace Pim.Module.Files.Entities;

public sealed class FileChunkEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FileItemId { get; set; }
    public FileItemEntity? FileItem { get; set; }
    public Guid VersionId { get; set; }
    public FileVersionEntity? Version { get; set; }
    public int ChunkIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public string TextHash { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string? QdrantPointId { get; set; }
}
