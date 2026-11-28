using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using Pim.Core.Data;

namespace Pim.Infrastructure.Data.Entities;

public static class RecycleBinOperationStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
}

public static class RecycleBinPurgePhases
{
    public const string Pending = "pending";
    public const string DbCommitted = "db_committed";
    public const string Completed = "completed";
    public const string Abandoned = "abandoned";
}

public static class RecycleBinPurgeDomains
{
    public const string Note = "note";
    public const string File = "file";
    public const string Task = "task";
    public const string Calendar = "calendar";
}

/// <summary>
/// A soft-delete generation is identified by its tombstone timestamp. A purge receipt
/// recorded for one generation must never be confused with a later trash cycle of the
/// same object.
/// </summary>
public static class RecycleBinPurgeGenerations
{
    public static string FromDeletedAt(DateTimeOffset deletedAt) =>
        deletedAt.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Durable idempotency ledger for a unified recycle-bin batch. The receipt id is chosen
/// before any item runs so a retried request with the same operation id can never mint a
/// second receipt, even when the first attempt was interrupted after destructive work.
/// </summary>
[Table("recycle_bin_operations")]
public sealed class RecycleBinOperationEntity : IUserOwnedEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid UserId { get; set; }
    [Column("operation_id")] public Guid OperationId { get; set; }
    [Column("action"), MaxLength(16)] public string Action { get; set; } = "";
    [Column("payload_hash"), MaxLength(64)] public string PayloadHash { get; set; } = "";
    [Column("status"), MaxLength(16)] public string Status { get; set; } = RecycleBinOperationStatuses.Running;
    [Column("processing_token")] public Guid ProcessingToken { get; set; }
    [Column("lease_expires_at")] public DateTimeOffset LeaseExpiresAt { get; set; }
    [Column("receipt_json")] public string? ReceiptJson { get; set; }
    [Column("attempt_count")] public int AttemptCount { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Durable per-item purge ledger shared by every recycle-bin domain.
///
/// <para>
/// Notes commit a <c>pending</c> intent before touching material/derived files and advance
/// it through <c>db_committed</c> to <c>completed</c>, so a retry can distinguish "never
/// started" from "files gone, database pending" and from "database committed, ledger
/// finalization pending" without guessing.
/// </para>
/// <para>
/// Files and calendar items have one PostgreSQL commit for their destructive work, so they
/// write an already-<c>completed</c> receipt in that same <c>SaveChanges</c>. The unique key
/// is <c>(owner, domain, item, generation)</c>, so a receipt only proves the exact trashed
/// generation was purged; a missing object without a matching receipt still fails closed.
/// </para>
/// </summary>
[Table("recycle_bin_purge_intents")]
public sealed class RecycleBinPurgeIntentEntity : IUserOwnedEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid UserId { get; set; }
    [Column("domain"), MaxLength(16)] public string Domain { get; set; } = "";
    [Column("item_id")] public Guid ItemId { get; set; }
    [Column("generation"), MaxLength(32)] public string Generation { get; set; } = "";
    [Column("expected_deleted_at")] public DateTimeOffset? ExpectedDeletedAt { get; set; }
    [Column("title"), MaxLength(500)] public string Title { get; set; } = "";
    [Column("phase"), MaxLength(24)] public string Phase { get; set; } = RecycleBinPurgePhases.Pending;
    [Column("lease_token")] public Guid? LeaseToken { get; set; }
    [Column("lease_expires_at")] public DateTimeOffset? LeaseExpiresAt { get; set; }
    [Column("attempt_count")] public int AttemptCount { get; set; }
    [Column("material_versions")] public int MaterialVersions { get; set; }
    [Column("asr_tasks")] public int AsrTasks { get; set; }
    [Column("derived_tasks")] public int DerivedTasks { get; set; }
    [Column("ai_calls")] public int AiCalls { get; set; }
    [Column("ai_images")] public int AiImages { get; set; }
    [Column("ai_audio")] public int AiAudio { get; set; }
    // Cleanup handles are persisted before any file is deleted. A retry after a
    // rolled-back PostgreSQL commit can still find the ASR/derived task files' AI
    // logs even though the task files themselves are already gone.
    [Column("asr_task_ids")] public string AsrTaskIdsJson { get; set; } = "[]";
    [Column("derived_task_ids")] public string DerivedTaskIdsJson { get; set; } = "[]";
    [Column("ai_log_ids")] public string AiLogIdsJson { get; set; } = "[]";
    [Column("last_error"), MaxLength(2000)] public string? LastError { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
}
