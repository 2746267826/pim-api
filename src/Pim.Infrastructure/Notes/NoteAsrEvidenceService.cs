using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

public sealed record NoteAsrEvidenceWriteResult(Guid EvidenceId, bool Duplicate);
public sealed record NoteAsrEvidenceSummary(int Expected, int Succeeded, bool Complete);

/// <summary>
/// Durable bridge from local/Batch execution to the note-visible transcript without fabricating an
/// SDK/cloud AiRequestLog. Callers identify only an existing owner-scoped attempt and segment.
/// </summary>
public interface INoteAsrEvidenceSink
{
    Task<NoteAsrEvidenceWriteResult> WriteAsync(Guid ownerId, Guid attemptId, int segmentIndex,
        string receiptKind, string providerHandle, string resultText, string? coverageJson,
        bool updateAttemptCounters, CancellationToken ct);

    Task<NoteAsrEvidenceSummary> FinalizeBatchAsync(Guid ownerId, Guid attemptId,
        bool providerTerminalSuccess, CancellationToken ct);

    /// <summary>Trusted background entry whose owner must come from a persisted job row, never HTTP state.</summary>
    Task<NoteAsrEvidenceWriteResult> WriteForPersistedOwnerAsync(Guid ownerId, Guid attemptId, int segmentIndex,
        string receiptKind, string providerHandle, string resultText, string? coverageJson,
        bool updateAttemptCounters, CancellationToken ct);

    /// <summary>Trusted background finalizer paired with WriteForPersistedOwnerAsync.</summary>
    Task<NoteAsrEvidenceSummary> FinalizeBatchForPersistedOwnerAsync(Guid ownerId, Guid attemptId,
        bool providerTerminalSuccess, CancellationToken ct);
}

public sealed class NoteAsrEvidenceService(PimDbContext db, NoteAsrTaskStore tasks,
    ICurrentUserService user) : INoteAsrEvidenceSink
{
    private const int MaxResultChars = 2_000_000;
    private const int MaxCoverageChars = 100_000;

    public async Task<NoteAsrEvidenceWriteResult> WriteAsync(Guid ownerId, Guid attemptId, int segmentIndex,
        string receiptKind, string providerHandle, string resultText, string? coverageJson,
        bool updateAttemptCounters, CancellationToken ct)
    {
        var owner = RequireOwner(ownerId);
        return await WriteCoreAsync(owner, attemptId, segmentIndex, receiptKind, providerHandle,
            resultText, coverageJson, updateAttemptCounters, ct);
    }

    public async Task<NoteAsrEvidenceWriteResult> WriteForPersistedOwnerAsync(Guid ownerId, Guid attemptId,
        int segmentIndex, string receiptKind, string providerHandle, string resultText,
        string? coverageJson, bool updateAttemptCounters, CancellationToken ct)
    {
        if (ownerId == Guid.Empty) throw new DomainException(40101, "持久化任务缺少 owner");
        return await WriteCoreAsync(ownerId, attemptId, segmentIndex, receiptKind, providerHandle,
            resultText, coverageJson, updateAttemptCounters, ct);
    }

    private async Task<NoteAsrEvidenceWriteResult> WriteCoreAsync(Guid owner, Guid attemptId,
        int segmentIndex, string receiptKind, string providerHandle, string resultText,
        string? coverageJson, bool updateAttemptCounters, CancellationToken ct)
    {
        if (receiptKind is not ("local" or "batch") || string.IsNullOrWhiteSpace(providerHandle) ||
            providerHandle.Length > 256 || string.IsNullOrWhiteSpace(resultText) ||
            resultText.Length > MaxResultChars || coverageJson?.Length > MaxCoverageChars)
            throw new DomainException(40001, "分片回执来源、handle、结果或覆盖范围无效");
        ValidateCoverage(coverageJson);

        var attempt = await db.NoteAsrAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "执行快照不存在");
        if (segmentIndex < 0 || segmentIndex >= attempt.ExpectedSegments)
            throw new DomainException(40001, "分片不属于当前执行快照");
        var state = await tasks.ReadAsync(owner, attempt.TaskId, ct);
        if (state.Request.VersionId != attempt.VersionId || state.InputSha256 != attempt.InputSha256 ||
            state.Steps.Count != attempt.ExpectedSegments)
            throw new DomainException(40901, "执行快照与固定笔记材料不一致");
        var step = state.Steps[segmentIndex];
        if (step.Index != segmentIndex)
            throw new DomainException(40901, "分片索引与固定计划不一致");

        var resultSha = Sha256(resultText);
        var exact = await db.NoteAsrSegmentEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner && x.AttemptId == attemptId &&
                x.FencingToken == attempt.FencingToken && x.SegmentIndex == segmentIndex &&
                x.ReceiptKind == receiptKind, ct);
        if (exact is not null)
        {
            if (updateAttemptCounters)
            {
                await SyncAttemptCounterAsync(owner, attempt, ct);
                await RecoverTaskEvidenceAsync(owner, state, attempt, ct);
            }
            return ExactOrConflict(exact, providerHandle, resultSha, coverageJson);
        }

        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        NoteAsrEvidenceWriteResult written;
        if (ownsTransaction)
        {
            // Production uses EnableRetryOnFailure(3). Only the DB-only evidence unit may be
            // replayed; task-store writes happen after commit and are never part of this retry.
            written = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var replay = await ExactEvidenceAsync();
                if (replay is not null)
                {
                    if (updateAttemptCounters) await SyncAttemptCounterCoreAsync(owner, attempt, ct);
                    await tx.CommitAsync(ct);
                    return ExactOrConflict(replay, providerHandle, resultSha, coverageJson);
                }

                var evidence = NewEvidence();
                db.NoteAsrSegmentEvidence.Add(evidence);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException error) when (IsUniqueViolation(error))
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    db.Entry(evidence).State = EntityState.Detached;
                    var winner = await ExactEvidenceAsync();
                    if (winner is null) throw;
                    return ExactOrConflict(winner, providerHandle, resultSha, coverageJson);
                }

                if (updateAttemptCounters) await SyncAttemptCounterCoreAsync(owner, attempt, ct);
                await tx.CommitAsync(ct);
                return new NoteAsrEvidenceWriteResult(evidence.Id, false);
            });
        }
        else
        {
            // Routing already owns a retrying execution strategy/transaction. Never open a nested
            // retry unit here; a savepoint keeps a unique-race recoverable inside that transaction.
            const string savepoint = "note_asr_evidence_write";
            await db.Database.ExecuteSqlRawAsync($"SAVEPOINT {savepoint}", ct);
            var evidence = NewEvidence();
            db.NoteAsrSegmentEvidence.Add(evidence);
            try
            {
                await db.SaveChangesAsync(ct);
                await db.Database.ExecuteSqlRawAsync($"RELEASE SAVEPOINT {savepoint}", ct);
                if (updateAttemptCounters) await SyncAttemptCounterCoreAsync(owner, attempt, ct);
                written = new(evidence.Id, false);
            }
            catch (DbUpdateException error) when (IsUniqueViolation(error))
            {
                await db.Database.ExecuteSqlRawAsync($"ROLLBACK TO SAVEPOINT {savepoint}", ct);
                await db.Database.ExecuteSqlRawAsync($"RELEASE SAVEPOINT {savepoint}", ct);
                db.Entry(evidence).State = EntityState.Detached;
                exact = await ExactEvidenceAsync();
                if (exact is null) throw;
                written = ExactOrConflict(exact, providerHandle, resultSha, coverageJson);
            }
        }

        if (updateAttemptCounters)
            await RecoverTaskEvidenceAsync(owner, state, attempt, ct);
        return written;

        NoteAsrSegmentEvidenceEntity NewEvidence() => new()
        {
            OwnerId = owner, AttemptId = attempt.Id, TaskId = attempt.TaskId, VersionId = attempt.VersionId,
            Route = attempt.Route, FencingToken = attempt.FencingToken, SegmentIndex = segmentIndex,
            RecordingId = step.RecordingId, RecordingSegment = step.RecordingSegment, ChunkIndex = step.ChunkIndex,
            SourceFileIndex = step.FileIndex, SourceSha256 = step.Sha256, SourceOffsetSamples = step.SourceOffset,
            Samples = step.Samples, ReceiptKind = receiptKind, ProviderHandle = providerHandle.Trim(),
            ResultText = resultText, ResultSha256 = resultSha, CoverageJson = coverageJson
        };
        Task<NoteAsrSegmentEvidenceEntity?> ExactEvidenceAsync() =>
            db.NoteAsrSegmentEvidence.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OwnerId == owner && x.AttemptId == attemptId &&
                    x.FencingToken == attempt.FencingToken && x.SegmentIndex == segmentIndex &&
                    x.ReceiptKind == receiptKind, ct);
    }

    public async Task<NoteAsrEvidenceSummary> FinalizeBatchAsync(Guid ownerId, Guid attemptId,
        bool providerTerminalSuccess, CancellationToken ct)
    {
        var owner = RequireOwner(ownerId);
        return await FinalizeBatchCoreAsync(owner, attemptId, providerTerminalSuccess, ct);
    }

    public async Task<NoteAsrEvidenceSummary> FinalizeBatchForPersistedOwnerAsync(Guid ownerId,
        Guid attemptId, bool providerTerminalSuccess, CancellationToken ct)
    {
        if (ownerId == Guid.Empty) throw new DomainException(40101, "持久化任务缺少 owner");
        return await FinalizeBatchCoreAsync(ownerId, attemptId, providerTerminalSuccess, ct);
    }

    private async Task<NoteAsrEvidenceSummary> FinalizeBatchCoreAsync(Guid owner, Guid attemptId,
        bool providerTerminalSuccess, CancellationToken ct)
    {
        var attempt = await db.NoteAsrAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "执行快照不存在");
        if (attempt.Route != NoteAsrRoute.CloudMultimodal || attempt.ExecutionMode != "batch")
            throw new DomainException(40901, "该执行快照不是 Batch 多模态路径");
        var evidence = await db.NoteAsrSegmentEvidence.AsNoTracking().Where(x => x.OwnerId == owner &&
            x.AttemptId == attemptId && x.FencingToken == attempt.FencingToken && x.ReceiptKind == "batch")
            .OrderBy(x => x.SegmentIndex).ToListAsync(ct);
        var indexes = evidence.Select(x => x.SegmentIndex).Distinct().OrderBy(x => x).ToArray();
        var complete = providerTerminalSuccess && indexes.Length == attempt.ExpectedSegments &&
            indexes.SequenceEqual(Enumerable.Range(0, attempt.ExpectedSegments));
        var now = DateTimeOffset.UtcNow;
        await db.NoteAsrAttempts.Where(x => x.Id == attemptId && x.OwnerId == owner &&
                x.FencingToken == attempt.FencingToken && x.State != "Succeeded")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CompletedSegments, indexes.Length)
                .SetProperty(x => x.FailedSegments, complete ? 0 : Math.Max(0, attempt.ExpectedSegments - indexes.Length))
                .SetProperty(x => x.State, complete ? "Succeeded" : "Failed")
                .SetProperty(x => x.LastErrorCode, complete ? null : "batch_partial_or_failed")
                .SetProperty(x => x.LastErrorMessage, complete ? null :
                    "Batch 未覆盖全部固定分片或供应商任务失败；已成功结果保留，未生成整篇成功")
                .SetProperty(x => x.UpdatedAt, now), ct);
        var state = await tasks.ReadAsync(owner, attempt.TaskId, ct);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await tasks.UpdateAsync(owner, attempt.TaskId, current =>
        {
            if (current.AttemptId != attempt.Id) return;
            foreach (var step in current.Steps)
            {
                var row = evidence.LastOrDefault(x => x.SegmentIndex == step.Index);
                if (row is not null)
                {
                    step.Current.Status = "Succeeded";
                    step.Current.EvidenceId = row.Id;
                    step.Current.ErrorMessage = null;
                }
                else if (!providerTerminalSuccess)
                {
                    step.Current.Status = "Failed";
                    step.Current.ErrorMessage = "Batch 未返回该固定分片的成功结果";
                }
            }
            current.StopRequested |= !complete;
            current.CompletionAcknowledged = complete;
        }, linked.Token);
        return new(attempt.ExpectedSegments, indexes.Length, complete);
    }

    private async Task RecoverTaskEvidenceAsync(Guid owner, NoteAsrState state, NoteAsrAttemptEntity attempt,
        CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await ApplyTaskEvidenceAsync(owner, state, attempt, linked.Token);
    }

    private async Task SyncAttemptCounterAsync(Guid owner, NoteAsrAttemptEntity attempt, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await SyncAttemptCounterCoreAsync(owner, attempt, linked.Token);
    }

    private async Task SyncAttemptCounterCoreAsync(Guid owner, NoteAsrAttemptEntity attempt, CancellationToken ct)
    {
        var count = await db.NoteAsrSegmentEvidence.AsNoTracking().Where(x => x.OwnerId == owner &&
                x.AttemptId == attempt.Id && x.FencingToken == attempt.FencingToken)
            .Select(x => x.SegmentIndex).Distinct().CountAsync(ct);
        await db.NoteAsrAttempts.Where(x => x.Id == attempt.Id && x.OwnerId == owner &&
                x.FencingToken == attempt.FencingToken && x.State != "Succeeded")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CompletedSegments, count)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    private async Task ApplyTaskEvidenceAsync(Guid owner, NoteAsrState state, NoteAsrAttemptEntity attempt,
        CancellationToken ct)
    {
        var evidence = await db.NoteAsrSegmentEvidence.AsNoTracking().Where(x => x.OwnerId == owner &&
            x.AttemptId == attempt.Id && x.FencingToken == attempt.FencingToken)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        if (evidence.Count == 0) return;
        await tasks.UpdateAsync(owner, state.Request.TaskId, current =>
        {
            foreach (var row in evidence.GroupBy(x => x.SegmentIndex).Select(x => x.Last()))
            {
                if (row.SegmentIndex < 0 || row.SegmentIndex >= current.Steps.Count) continue;
                var step = current.Steps[row.SegmentIndex];
                step.Current.EvidenceId = row.Id;
                step.Current.Status = "Succeeded";
                step.Current.ErrorMessage = null;
            }
        }, ct);
    }

    private Guid RequireOwner(Guid ownerId)
    {
        var owner = user.UserId ?? throw new DomainException(40101, "请先登录");
        if (owner != ownerId) throw new DomainException(40301, "不能写入其他账号的执行证据");
        return owner;
    }

    private static NoteAsrEvidenceWriteResult ExactOrConflict(NoteAsrSegmentEvidenceEntity row,
        string providerHandle, string resultSha, string? coverageJson) =>
        row.ProviderHandle == providerHandle.Trim() && row.ResultSha256 == resultSha &&
        string.Equals(row.CoverageJson, coverageJson, StringComparison.Ordinal)
            ? new(row.Id, true)
            : throw new DomainException(40901, "同一分片已有不同成功回执，原证据保留");

    private static void ValidateCoverage(string? coverage)
    {
        if (coverage is null) return;
        try
        {
            using var document = JsonDocument.Parse(coverage, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                throw new JsonException();
        }
        catch (JsonException) { throw new DomainException(40001, "分片覆盖范围不是有效 JSON"); }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsUniqueViolation(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" }) return true;
            if (current.GetType().Name == "SqliteException" &&
                current.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
