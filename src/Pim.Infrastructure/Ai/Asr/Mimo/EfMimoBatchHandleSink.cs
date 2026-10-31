using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

/// <summary>Durable EF implementation: UNKNOWN and verified handles survive restarts.</summary>
public sealed class EfMimoBatchHandleSink(PimDbContext db) : IMimoBatchHandleSink
{
    public async Task SaveAsync(MimoBatchCreateOutcome outcome, MimoBatchIntent intent, CancellationToken _)
    {
        // The caller token is intentionally ignored, but the whole sink operation has one finite budget.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = budget.Token;
        if (!Guid.TryParse(intent.ProviderHandle, out var id))
            throw new InvalidOperationException("Batch provider handle must be the durable job id");
        if (!Guid.TryParse(intent.Segment, out var taskId))
            throw new InvalidOperationException("Batch intent task binding is invalid");
        var job = await db.NoteAsrBatchJobs.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException("Batch job row must exist before handle persistence");
        var expectedCount = JsonSerializer.Deserialize<string[]>(job.ExpectedCustomIdsJson)?.Length;
        if (job.OwnerId != intent.Owner || job.TaskId != taskId || job.AttemptId is null ||
            job.Model != intent.Model || job.BaseUrl != intent.BaseUrl || job.ProfileRevision == Guid.Empty ||
            job.CompletionWindow != intent.CompletionWindow || job.ConsentRef != intent.ConsentRef ||
            expectedCount is null or < 1)
            throw new InvalidOperationException("Batch job snapshot no longer matches its immutable intent");

        var attempt = await db.NoteAsrAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == job.AttemptId.Value && x.OwnerId == intent.Owner, ct)
            ?? throw new InvalidOperationException("Batch attempt binding is missing");
        if (attempt.TaskId != job.TaskId || attempt.VersionId.ToString("D") != intent.MaterialVersion ||
            attempt.FencingToken != intent.Attempt ||
            attempt.ModelId?.ToString("D") != intent.CredentialConfigId ||
            attempt.ModelRevision?.ToString("D") != intent.CredentialConfigRevision ||
            attempt.ModelRevision != job.ProfileRevision ||
            attempt.PresetId != MimoMultimodalPreset.PresetId ||
            attempt.ExpectedSegments != expectedCount ||
            !string.Equals(attempt.ConsentVersion, job.ConsentRef, StringComparison.Ordinal))
            throw new InvalidOperationException("Batch attempt no longer matches owner/task/version/segment intent");

        job.UpdatedAt = DateTimeOffset.UtcNow;
        switch (outcome)
        {
            case MimoBatchCreateOutcome.Known known:
                if (job.BatchId is not null &&
                    !string.Equals(job.BatchId, known.Batch.BatchId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Batch job already holds a different verified handle");
                job.BatchId = known.Batch.BatchId;
                job.InputFileId = known.Batch.InputFileId ?? job.InputFileId;
                job.OutputFileId = known.Batch.OutputFileId ?? job.OutputFileId;
                job.ErrorFileId = known.Batch.ErrorFileId ?? job.ErrorFileId;
                job.BatchExpiresAt = known.Batch.ExpiresAt ?? job.BatchExpiresAt;
                job.CompletedItems = Max(job.CompletedItems, known.Batch.Completed);
                job.FailedItems = Max(job.FailedItems, known.Batch.Failed);
                job.LastErrorCode = null;
                job.LastErrorMessage = null;
                if (!TerminalState(job))
                {
                    job.StatusRaw = known.Batch.StatusRaw;
                    job.Status = known.Batch.Status.ToString();
                }
                break;
            case MimoBatchCreateOutcome.Unknown unknown:
                if (string.IsNullOrWhiteSpace(job.BatchId) && !TerminalState(job))
                {
                    job.Status = "Unknown";
                    job.StatusRaw = null;
                    job.LastErrorCode = "batch_create_unknown";
                    job.LastErrorMessage = unknown.Reason;
                }
                break;
        }
        // A verified handle must survive client disconnect, while a hung DB cannot hold the request forever.
        NoteAsrBatchRevision.Bump(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer advanced the row between our load and save. Reload and only
            // fill a missing or identical verified handle; never replace a different handle
            // and never rewind a terminal status.
            await db.Entry(job).ReloadAsync(ct);
            if (outcome is MimoBatchCreateOutcome.Known retry)
            {
                if (job.BatchId is not null &&
                    !string.Equals(job.BatchId, retry.Batch.BatchId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Batch job already holds a different verified handle");
                var terminal = TerminalState(job);
                job.BatchId = retry.Batch.BatchId;
                job.InputFileId = retry.Batch.InputFileId ?? job.InputFileId;
                job.OutputFileId = retry.Batch.OutputFileId ?? job.OutputFileId;
                job.ErrorFileId = retry.Batch.ErrorFileId ?? job.ErrorFileId;
                job.BatchExpiresAt = retry.Batch.ExpiresAt ?? job.BatchExpiresAt;
                job.CompletedItems = Max(job.CompletedItems, retry.Batch.Completed);
                job.FailedItems = Max(job.FailedItems, retry.Batch.Failed);
                if (!terminal)
                {
                    job.StatusRaw = retry.Batch.StatusRaw;
                    job.Status = retry.Batch.Status.ToString();
                }
                job.UpdatedAt = DateTimeOffset.UtcNow;
                NoteAsrBatchRevision.Bump(job);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private static bool TerminalState(Pim.Infrastructure.Data.Entities.NoteAsrBatchJobEntity job) =>
        job.Status is "Deleted" or "Completed" or "Failed" or "Expired" or "Cancelled" ||
        MimoBatchStatus.IsTerminal(MimoBatchStatus.Normalize(job.StatusRaw));

    private static int? Max(int? left, int? right) =>
        right is null ? left : left is null ? right : Math.Max(left.Value, right.Value);
}
