using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("note_processing_preferences")]
public sealed class NoteProcessingPreferenceEntity
{
    [Key, Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("auto_note_processing_enabled")] public bool AutoNoteProcessingEnabled { get; set; }
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
