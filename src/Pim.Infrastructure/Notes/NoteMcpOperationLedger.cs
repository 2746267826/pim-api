using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

internal static class NoteMcpOperationStates
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

internal static class NoteMcpOperationRetention
{
    /// <summary>Completed response bodies retained per owner.</summary>
    public const int MaxCompletedResponses = 200;

    /// <summary>
    /// Completed response bodies older than this are purged. Identity rows are bounded by
    /// <see cref="NoteMcpOperationOptions.MaxIdentityRowsPerOwner"/> instead.
    /// </summary>
    public static readonly TimeSpan ResponseRetention = TimeSpan.FromDays(30);
}

/// <summary>
/// One durable request-id binding. <see cref="ResponseJson"/> is the only field that may be
/// cleared by retention; state, fingerprint and the composite identity remain until the owner's
/// hard capacity is reached, after which new identities fail closed instead of evicting any
/// existing identity.
/// </summary>
internal sealed record NoteMcpOperationRow(
    Guid OwnerId,
    Guid RequestId,
    Guid NoteId,
    string Operation,
    string OperationId,
    string Fingerprint,
    string State,
    string? ResponseJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ResponsePurgedAt,
    string? ReceiptStatus,
    Guid? Revision,
    string? Message,
    int? ErrorCode,
    string? ErrorMessage);

/// <summary>Hard per-owner capacity for permanent native-note request-id identities.</summary>
public sealed class NoteMcpOperationOptions
{
    public const int DefaultMaxIdentityRowsPerOwner = 10_000;

    /// <summary>
    /// Maximum identity rows retained for one owner. Pending, failed, unknown and completed
    /// identities all count. Once the cap is reached, new request ids are rejected before any
    /// mutation runs; existing request ids stay replayable/queryable.
    /// </summary>
    public int MaxIdentityRowsPerOwner { get; set; } = DefaultMaxIdentityRowsPerOwner;

    public bool IsValid() => MaxIdentityRowsPerOwner > 0;
}

internal enum NoteMcpOperationClaim
{
    Inserted,
    AlreadyExists,
    CapacityExceeded,
}

internal interface INoteMcpOperationLedger
{
    /// <summary>
    /// Atomically inserts the pending marker unless the owner already owns this
    /// (owner, request id) pair or has reached the hard identity capacity. Capacity accounting
    /// is serialized per owner, so concurrent instances cannot exceed the cap.
    /// </summary>
    Task<NoteMcpOperationClaim> TryInsertAsync(NoteMcpOperationRow row,
        int maxIdentityRowsPerOwner, CancellationToken ct);

    Task<NoteMcpOperationRow?> FindAsync(Guid owner, Guid requestId, CancellationToken ct);

    Task<bool> CompleteAsync(Guid owner, Guid requestId, string responseJson,
        string receiptStatus, Guid? revision, string message, DateTimeOffset completedAt,
        CancellationToken ct);

    Task<bool> FailAsync(Guid owner, Guid requestId, string state, int errorCode,
        string errorMessage, DateTimeOffset completedAt, CancellationToken ct);

    /// <summary>
    /// Clears replay response bodies only. Pending, failed, unknown and all identity rows are
    /// deliberately excluded so an old request id can never be treated as a new mutation.
    /// </summary>
    Task PurgeResponsesAsync(Guid owner, DateTimeOffset now, CancellationToken ct);

    Task PurgeResponseAsync(Guid owner, Guid requestId, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// PostgreSQL-backed ledger shared by every API instance. The composite primary key is the
/// single-winner authority for a pending marker; <c>ON CONFLICT DO NOTHING</c> makes concurrent
/// first attempts deterministic instead of relying on a process-local file lock.
/// </summary>
internal sealed class PostgresNoteMcpOperationLedger(DbContextOptions<PimDbContext> options)
    : INoteMcpOperationLedger
{
    private const int CapacityLockNamespace = 0x4E4D4350;

    public async Task<NoteMcpOperationClaim> TryInsertAsync(NoteMcpOperationRow row,
        int maxIdentityRowsPerOwner, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIdentityRowsPerOwner);
        await using var db = new PimDbContext(options);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Serialize the count+insert pair for one owner. The count is a separate command
            // after the advisory lock is acquired, so its READ COMMITTED snapshot includes the
            // previous holder's committed identities.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT pg_advisory_xact_lock(
                    {CapacityLockNamespace}, hashtext({row.OwnerId.ToString("D")}))
                """, ct);
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "note_mcp_operations"
                    ("owner_id", "request_id", "note_id", "operation", "operation_id",
                     "fingerprint", "state", "created_at")
                SELECT {row.OwnerId}, {row.RequestId}, {row.NoteId}, {row.Operation},
                       {row.OperationId}, {row.Fingerprint}, {row.State}, {row.CreatedAt}
                WHERE (
                    SELECT count(*) FROM "note_mcp_operations"
                    WHERE "owner_id" = {row.OwnerId}
                ) < {maxIdentityRowsPerOwner}
                ON CONFLICT ("owner_id", "request_id") DO NOTHING
                """, ct);
            if (affected == 1)
            {
                await transaction.CommitAsync(ct);
                return NoteMcpOperationClaim.Inserted;
            }

            // A full ledger must not hide an existing request id: retries and lookups of old
            // identities remain valid even when no new identity can be admitted.
            var exists = await db.NoteMcpOperations.AsNoTracking()
                .AnyAsync(x => x.OwnerId == row.OwnerId && x.RequestId == row.RequestId, ct);
            await transaction.CommitAsync(ct);
            return exists
                ? NoteMcpOperationClaim.AlreadyExists
                : NoteMcpOperationClaim.CapacityExceeded;
        });
    }

    public async Task<NoteMcpOperationRow?> FindAsync(Guid owner, Guid requestId,
        CancellationToken ct)
    {
        await using var db = new PimDbContext(options);
        var entity = await db.NoteMcpOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner && x.RequestId == requestId, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<bool> CompleteAsync(Guid owner, Guid requestId, string responseJson,
        string receiptStatus, Guid? revision, string message, DateTimeOffset completedAt,
        CancellationToken ct)
    {
        await using var db = new PimDbContext(options);
        var affected = await db.NoteMcpOperations
            .Where(x => x.OwnerId == owner && x.RequestId == requestId &&
                        x.State == NoteMcpOperationStates.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, NoteMcpOperationStates.Completed)
                .SetProperty(x => x.ResponseJson, responseJson)
                .SetProperty(x => x.ReceiptStatus, receiptStatus)
                .SetProperty(x => x.Revision, revision)
                .SetProperty(x => x.Message, message)
                .SetProperty(x => x.CompletedAt, completedAt)
                .SetProperty(x => x.ResponsePurgedAt, (DateTimeOffset?)null), ct);
        return affected == 1;
    }

    public async Task<bool> FailAsync(Guid owner, Guid requestId, string state, int errorCode,
        string errorMessage, DateTimeOffset completedAt, CancellationToken ct)
    {
        await using var db = new PimDbContext(options);
        var affected = await db.NoteMcpOperations
            .Where(x => x.OwnerId == owner && x.RequestId == requestId &&
                        x.State == NoteMcpOperationStates.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, state)
                .SetProperty(x => x.ReceiptStatus, state)
                .SetProperty(x => x.ErrorCode, errorCode)
                .SetProperty(x => x.ErrorMessage, errorMessage)
                .SetProperty(x => x.Message, errorMessage)
                .SetProperty(x => x.CompletedAt, completedAt), ct);
        return affected == 1;
    }

    public async Task PurgeResponsesAsync(Guid owner, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - NoteMcpOperationRetention.ResponseRetention;
        await using var db = new PimDbContext(options);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH ranked AS (
                SELECT "request_id", "completed_at",
                       row_number() OVER (
                           ORDER BY "completed_at" DESC NULLS LAST,
                                    "created_at" DESC,
                                    "request_id" DESC) AS "position"
                FROM "note_mcp_operations"
                WHERE "owner_id" = {owner}
                  AND "state" = 'completed'
                  AND "response_json" IS NOT NULL
            )
            UPDATE "note_mcp_operations" AS target
            SET "response_json" = NULL, "response_purged_at" = {now}
            FROM ranked
            WHERE target."owner_id" = {owner}
              AND target."request_id" = ranked."request_id"
              AND (ranked."completed_at" < {cutoff}
                   OR ranked."position" > {NoteMcpOperationRetention.MaxCompletedResponses})
            """, ct);
    }

    public async Task PurgeResponseAsync(Guid owner, Guid requestId, DateTimeOffset now,
        CancellationToken ct)
    {
        var cutoff = now - NoteMcpOperationRetention.ResponseRetention;
        await using var db = new PimDbContext(options);
        await db.NoteMcpOperations
            .Where(x => x.OwnerId == owner && x.RequestId == requestId &&
                        x.State == NoteMcpOperationStates.Completed &&
                        x.ResponseJson != null && x.CompletedAt < cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ResponseJson, (string?)null)
                .SetProperty(x => x.ResponsePurgedAt, (DateTimeOffset?)now), ct);
    }

    private static NoteMcpOperationRow Map(NoteMcpOperationEntity entity)
        => new(entity.OwnerId, entity.RequestId, entity.NoteId, entity.Operation,
            entity.OperationId, entity.Fingerprint, entity.State, entity.ResponseJson,
            entity.CreatedAt, entity.CompletedAt, entity.ResponsePurgedAt,
            entity.ReceiptStatus, entity.Revision, entity.Message, entity.ErrorCode,
            entity.ErrorMessage);
}
