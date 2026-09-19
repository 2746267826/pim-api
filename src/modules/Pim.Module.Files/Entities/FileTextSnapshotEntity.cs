using Pim.Core.Data;

namespace Pim.Module.Files.Entities;

/// <summary>
/// 文本文件编辑/恢复前的内容快照（弥补 OneDrive 个人版版本 API 不确定性，设计文档 §8）。
/// 仅小文本文件（≤2MB）启用；随 FileItem 级联删除。
/// </summary>
public sealed class FileTextSnapshotEntity : IUserOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid ItemId { get; set; }
    public FileItemEntity? Item { get; set; }
    public string ExternalFileId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ByteSize { get; set; }

    /// <summary>pre-edit（编辑前）| pre-restore（恢复前）。</summary>
    public string Reason { get; set; } = "pre-edit";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
