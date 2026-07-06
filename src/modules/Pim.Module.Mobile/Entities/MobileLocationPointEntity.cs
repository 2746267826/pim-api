using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_location_points")]
public sealed class MobileLocationPointEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("recorded_at_utc")]
    public DateTimeOffset RecordedAtUtc { get; set; }

    [Column("latitude")]
    public decimal Latitude { get; set; }

    [Column("longitude")]
    public decimal Longitude { get; set; }

    [Column("horizontal_accuracy_meters")]
    public decimal HorizontalAccuracyMeters { get; set; }

    [Column("provider")]
    [MaxLength(64)]
    public string Provider { get; set; } = string.Empty;

    [Column("source")]
    [MaxLength(64)]
    public string Source { get; set; } = string.Empty;

    [Column("altitude_meters")]
    public decimal? AltitudeMeters { get; set; }

    [Column("vertical_accuracy_meters")]
    public decimal? VerticalAccuracyMeters { get; set; }

    [Column("speed_meters_per_second")]
    public decimal? SpeedMetersPerSecond { get; set; }

    [Column("speed_accuracy_meters_per_second")]
    public decimal? SpeedAccuracyMetersPerSecond { get; set; }

    [Column("bearing_degrees")]
    public decimal? BearingDegrees { get; set; }

    [Column("bearing_accuracy_degrees")]
    public decimal? BearingAccuracyDegrees { get; set; }

    [Column("is_mock")]
    public bool IsMock { get; set; }

    [Column("quality")]
    [MaxLength(32)]
    public string Quality { get; set; } = "usable";

    [Column("raw_json", TypeName = "jsonb")]
    public string RawJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
