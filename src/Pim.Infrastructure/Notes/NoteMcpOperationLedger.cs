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
    /// <summary>Completed response bodies retained per owner. Identity rows are never capped.</summary>
    public const int MaxCompletedResponses = 200;

    /// <summary>Completed response bodies older than this are purged. Identity rows remain.</summary>
    public static readonly TimeSpan ResponseRetention = TimeSpan.FromDays(30);
}

/// <summary>
/// One durable request-id binding. <see cref="ResponseJson"/> is the only field that may be
/// cleared by retention; state, fingerprint and the composite identity remain forever.
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

internal interface INoteMcpOperationLedger
{
    /// <summary>
    /// Atomically inserts the pending marker. Returns false when any instance already owns
    /// this (owner, request id) pair; callers must then load and validate the existing row.
    /// </summary>
    Task<bool> TryInsertAsync(NoteMcpOperationRow row, CancellationToken ct);

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
}

/// <summary>
/// PostgreSQL-backed ledger shared by every API instance. The composite primary key is the
/// single-winner authority for a pending marker; <c>ON CONFLICT DO NOTHING</c> makes concurrent
/// first attempts deterministic instead of relying on a process-local file lock.
/// </summary>
internal sealed class PostgresNoteMcpOperationLedger(DbContextOptions<PimDbContext> options)
    : INoteMcpOperationLedger
{
    public async Task<bool> TryInsertAsync(NoteMcpOperationRow row, CancellationToken ct)
    {
        await using var db = new PimDbContext(options);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "note_mcp_operations"
                ("owner_id", "request_id", "note_id", "operation", "operation_id",
                 "fingerprint", "state", "created_at")
            VALUES
                ({row.OwnerId}, {row.RequestId}, {row.NoteId}, {row.Operation},
                 {row.OperationId}, {row.Fingerprint}, {row.State}, {row.CreatedAt})
            ON CONFLICT ("owner_id", "request_id") DO NOTHING
            """, ct);
        return affected == 1;
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
            UPDATE "note_mcp_operations"
            SET "response_json" = NULL, "response_purged_at" = {now}
            WHERE "owner_id" = {owner}
              AND "state" = 'completed'
              AND "response_json" IS NOT NULL
              AND "completed_at" < {cutoff}
            """, ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH ranked AS (
                SELECT "request_id",
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
              AND ranked."position" > {NoteMcpOperationRetention.MaxCompletedResponses}
            """, ct);
    }

    private static NoteMcpOperationRow Map(NoteMcpOperationEntity entity)
        => new(entity.OwnerId, entity.RequestId, entity.NoteId, entity.Operation,
            entity.OperationId, entity.Fingerprint, entity.State, entity.ResponseJson,
            entity.CreatedAt, entity.CompletedAt, entity.ResponsePurgedAt,
            entity.ReceiptStatus, entity.Revision, entity.Message, entity.ErrorCode,
            entity.ErrorMessage);
}
