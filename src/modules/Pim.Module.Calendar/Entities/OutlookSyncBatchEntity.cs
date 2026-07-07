using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.Calendar.Entities;

[Table("outlook_sync_batches")]
public class OutlookSyncBatchEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("provider")][MaxLength(40)] public string Provider { get; set; } = "outlook";
    [Column("status")][MaxLength(40)] public string Status { get; set; } = "running";
    [Column("read_count")] public int ReadCount { get; set; }
    [Column("created_count")] public int CreatedCount { get; set; }
    [Column("updated_count")] public int UpdatedCount { get; set; }
    [Column("conflict_count")] public int ConflictCount { get; set; }
    [Column("confirmation_count")] public int ConfirmationCount { get; set; }
    [Column("failure_count")] public int FailureCount { get; set; }
    [Column("steps_json", TypeName = "jsonb")] public string StepsJson { get; set; } = "[]";
    [Column("errors_json", TypeName = "jsonb")] public string ErrorsJson { get; set; } = "[]";
    [Column("error_summary")] public string? ErrorSummary { get; set; }
    [Column("started_at")] public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("finished_at")] public DateTimeOffset? FinishedAt { get; set; }
}
