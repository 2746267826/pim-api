using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.QuickNotes.Entities;

public static class QuickNoteStatuses
{
    public const string Inbox = "inbox";
    public const string Processed = "processed";
    public const string Archived = "archived";

    public static bool IsValid(string status)
        => status is Inbox or Processed or Archived;
}

public static class QuickNoteSources
{
    public const string WebFloating = "web-floating";
    public const string WebPage = "web-page";
}

[Table("quick_notes")]
public class QuickNoteEntity : ISoftDeletable
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("content_markdown")]
    public string ContentMarkdown { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(32)]
    public string Status { get; set; } = QuickNoteStatuses.Inbox;

    [Column("source")]
    [MaxLength(64)]
    public string Source { get; set; } = QuickNoteSources.WebPage;

    [Column("metadata_json", TypeName = "jsonb")]
    public string MetadataJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("archived_at")]
    public DateTimeOffset? ArchivedAt { get; set; }

    [Column("deleted_at")]
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<QuickNoteAttachmentEntity> Attachments { get; set; } = new List<QuickNoteAttachmentEntity>();
}
