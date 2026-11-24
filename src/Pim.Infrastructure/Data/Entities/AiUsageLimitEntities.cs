using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Infrastructure.Data.Entities;

[Table("ai_provider_connections")]
public sealed class AiProviderConnectionEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("name"), MaxLength(120)] public string Name { get; set; } = "";
    [Column("normalized_name"), MaxLength(120)] public string NormalizedName { get; set; } = "";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("ai_usage_limit_rules")]
public sealed class AiUsageLimitRuleEntity
{
    [Key, Column("connection_id")] public Guid ConnectionId { get; set; }
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("enabled")] public bool Enabled { get; set; }
    [Column("period"), MaxLength(16)] public string Period { get; set; } = "month";
    [Column("warning_percent")] public int WarningPercent { get; set; } = 80;
    [Column("token_limit")] public long? TokenLimit { get; set; }
    [Column("cost_limit")] public decimal? CostLimit { get; set; }
    [Column("currency"), MaxLength(3)] public string? Currency { get; set; }
    [Column("time_zone_id"), MaxLength(128)] public string TimeZoneId { get; set; } = "Asia/Shanghai";
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("ai_usage_limit_leases")]
public sealed class AiUsageLimitLeaseEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("connection_id")] public Guid ConnectionId { get; set; }
    [Column("request_key"), MaxLength(200)] public string RequestKey { get; set; } = "";
    [Column("purpose"), MaxLength(128)] public string Purpose { get; set; } = "";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
}

[Table("ai_usage_reminder_events")]
public sealed class AiUsageReminderEventEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("connection_id")] public Guid ConnectionId { get; set; }
    [Column("period"), MaxLength(16)] public string Period { get; set; } = "month";
    [Column("cycle_key"), MaxLength(16)] public string CycleKey { get; set; } = "";
    [Column("level"), MaxLength(16)] public string Level { get; set; } = "warning";
    [Column("warning_percent")] public int WarningPercent { get; set; } = 80;
    [Column("time_zone_id"), MaxLength(128)] public string TimeZoneId { get; set; } = "Asia/Shanghai";
    [Column("metrics_json", TypeName = "jsonb")] public string MetricsJson { get; set; } = "[]";
    [Column("snapshot_json", TypeName = "jsonb")] public string SnapshotJson { get; set; } = "{}";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("ai_usage_reminder_event_reads")]
public sealed class AiUsageReminderEventReadEntity : IUserOwnedEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("event_id")] public Guid EventId { get; set; }
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("read_at")] public DateTimeOffset ReadAt { get; set; } = DateTimeOffset.UtcNow;
}
