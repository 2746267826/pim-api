using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.QuickNotes.Entities;

[Table("quick_note_attachments")]
public class QuickNoteAttachmentEntity : ISoftDeletable, IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("quick_note_id")]
    public Guid? QuickNoteId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    /// <summary>
    /// 附件落在哪个 provider（<c>onedrive</c> / <c>google</c>）。
    /// 工单 05 / F3 一并去掉了 <c>minio</c> 这个 Nextcloud 时代遗留的默认值：
    /// MinIO 存储随 P4 退役后它已是死语义，继续留着会让「未赋值」被误读成「存在 MinIO」。
    /// </summary>
    [Column("storage_provider")]
    [MaxLength(32)]
    public string StorageProvider { get; set; } = string.Empty;

    /// <summary>
    /// 附件落在哪个**账号行**（<c>file_providers.id</c>）。多账号并存后没有它就无法定位附件。
    /// 本列引入之前写入的老行为 null，读取时在用户其余已连接账号里回退查找。
    /// </summary>
    [Column("storage_account_id")]
    public Guid? StorageAccountId { get; set; }

    [Column("object_key")]
    public string ObjectKey { get; set; } = string.Empty;

    [Column("file_name")]
    public string FileName { get; set; } = string.Empty;

    [Column("content_type")]
    [MaxLength(255)]
    public string ContentType { get; set; } = "application/octet-stream";

    [Column("size_bytes")]
    public long SizeBytes { get; set; }

    [Column("content_hash")]
    [MaxLength(128)]
    public string? ContentHash { get; set; }

    [Column("metadata_json", TypeName = "jsonb")]
    public string MetadataJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("deleted_at")]
    public DateTimeOffset? DeletedAt { get; set; }

    [ForeignKey(nameof(QuickNoteId))]
    public QuickNoteEntity? QuickNote { get; set; }
}
