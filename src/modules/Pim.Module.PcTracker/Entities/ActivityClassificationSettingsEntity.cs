using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

[Table("pc_activity_classification_settings")]
public class ActivityClassificationSettingsEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("settings_key")]
    [MaxLength(64)]
    public string SettingsKey { get; set; } = "default";

    [Column("recommended_minimum_classification_duration_minutes")]
    public int RecommendedMinimumClassificationDurationMinutes { get; set; } = 5;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
