using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.QuickNotes.Entities;

[Table("quick_note_attachments")]
public class QuickNoteAttachmentEntity : ISoftDeletable
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("quick_note_id")]
    public Guid? QuickNoteId { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("storage_provider")]
    [MaxLength(32)]
    public string StorageProvider { get; set; } = "minio";

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
