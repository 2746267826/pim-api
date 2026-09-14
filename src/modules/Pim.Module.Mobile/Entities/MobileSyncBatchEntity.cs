using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Module.Mobile.Entities;

[Table("mobile_sync_batches")]
public sealed class MobileSyncBatchEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("batch_id")]
    [MaxLength(128)]
    public string BatchId { get; set; } = string.Empty;

    [Column("window_start_utc")]
    public DateTimeOffset WindowStartUtc { get; set; }

    [Column("window_end_utc")]
    public DateTimeOffset WindowEndUtc { get; set; }

    [Column("accepted_count")]
    public int AcceptedCount { get; set; }

    /// <summary>条目级校验拒绝数（如零时长汇总 #240）；不再影响批次状态（#241）。</summary>
    [Column("rejected_count")]
    public int RejectedCount { get; set; }

    /// <summary>重复/无需处理而被跳过的条目数（#243）。</summary>
    [Column("skipped_count")]
    public int SkippedCount { get; set; }

    [Column("failed_count")]
    public int FailedCount { get; set; }

    [Column("status")]
    [MaxLength(32)]
    public string Status { get; set; } = "completed";

    [Column("error_json", TypeName = "jsonb")]
    public string ErrorJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
