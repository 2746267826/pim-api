using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("note_processing_preferences")]
public sealed class NoteProcessingPreferenceEntity
{
    [Key, Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("auto_note_processing_enabled")] public bool AutoNoteProcessingEnabled { get; set; }
    [Column("standing_authorization_revision")] public Guid? StandingAuthorizationRevision { get; set; }
    [Column("standing_authorization_purpose"), MaxLength(64)] public string? StandingAuthorizationPurpose { get; set; }
    [Column("standing_authorization_material_scope"), MaxLength(64)] public string? StandingAuthorizationMaterialScope { get; set; }
    [Column("standing_authorization_cost_notice"), MaxLength(1000)] public string? StandingAuthorizationCostNotice { get; set; }
    [Column("standing_authorization_possible_charge_acknowledged")]
    public bool StandingAuthorizationPossibleChargeAcknowledged { get; set; }
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
