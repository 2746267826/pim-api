using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notes;

public sealed class NoteAsrBatchPollOptions
{
    public int PageSize { get; set; } = 25;
    public int MaxPagesPerRun { get; set; } = 4;
    public TimeSpan PerJobTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan PollBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan AuditRepairBackoff { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan UnknownCreateGrace { get; set; } = TimeSpan.FromMinutes(5);

    internal NoteAsrBatchPollOptions Normalized() => new()
    {
        PageSize = Math.Clamp(PageSize, 1, 100),
        MaxPagesPerRun = Math.Clamp(MaxPagesPerRun, 1, 20),
        PerJobTimeout = Clamp(PerJobTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2)),
        PollBackoff = Clamp(PollBackoff, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1)),
        AuditRepairBackoff = Clamp(AuditRepairBackoff, TimeSpan.Zero, TimeSpan.FromDays(1)),
        UnknownCreateGrace = Clamp(UnknownCreateGrace, TimeSpan.FromMinutes(1), TimeSpan.FromDays(7))
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}

/// <summary>
/// Durable background poll for MiMo Batch results. The recurring scheduler owns invocation;
/// this job never reads browser state, never creates/uploads/lists batches and only GETs
/// already persisted batch ids under one finite budget per job.
/// </summary>
public sealed class NoteAsrBatchPollJob(
    NoteAsrBatchService service,
    PimDbContext db,
    IOptions<NoteAsrBatchPollOptions> options,
    ILogger<NoteAsrBatchPollJob> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var settings = options.Value.Normalized();
        var now = DateTimeOffset.UtcNow;
        await MarkStaleCreatingUnknownAsync(now, settings, ct);
        try
        {
            await RepairTerminalAuditsAsync(settings, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // A repair scan failure must not stop provider polling for this run.
            logger.LogWarning(error, "Batch terminal audit repair scan failed");
        }
        var seen = new HashSet<Guid>();
        for (var page = 0; page < settings.MaxPagesPerRun; page++)
        {
            var taken = await RunPageAsync(now, settings, seen, ct);
            if (taken < settings.PageSize)
                return;
        }
        logger.LogDebug("Batch poll reached the per-run page cap; remaining jobs wait for the next recurrence");
    }

    /// <summary>
    /// Terminal jobs stop being poll candidates once their result is imported. A DB failure while
    /// projecting that final state into <c>ai_request_logs</c> must still converge, otherwise a
    /// running audit row could suppress the connection token limit forever. Repair is bounded and
    /// uses only the persisted job/audit rows; it never touches the provider.
    /// </summary>
    internal async Task<int> RepairTerminalAuditsAsync(NoteAsrBatchPollOptions settings,
        CancellationToken ct)
    {
        settings = settings.Normalized();
        var repairDue = DateTimeOffset.UtcNow - settings.AuditRepairBackoff;
        var candidates = await db.NoteAsrBatchJobs.AsNoTracking()
            .Where(job => job.Status != "Deleted" &&
                          (job.OutputImportedAt != null ||
                           job.Status == "Completed" || job.Status == "Failed" ||
                           job.Status == "Expired" || job.Status == "Cancelled" ||
                           job.Status == "Unknown") &&
                          // The same row-level timestamp doubles as a durable repair lease for
                          // terminal jobs. It keeps one permanently failing row from occupying
                          // every scan page while still allowing the provider poller to retry
                          // after its shorter PollBackoff.
                          (job.LastPolledAt == null || job.LastPolledAt < repairDue) &&
                          // A terminal audit closed with the explicit unavailable marker is a
                          // converged dead end: the marker always reflects the persisted evidence.
                          // It is only re-opened when newer result evidence was imported after the
                          // marker was written (import committed, audit write failed). Without this
                          // test the same unrepairable row would be re-claimed every backoff window
                          // and starve the page.
                          !db.AiRequestLogs.Any(a =>
                              a.UserId == job.OwnerId && a.ClientRequestId == job.Id &&
                              a.Purpose == NoteAsrBatchService.AuditPurpose &&
                              a.ErrorCode == NoteAsrBatchService.BatchUsageUnavailableCode &&
                              (job.OutputImportedAt == null || a.FinishedAt == null ||
                               a.FinishedAt >= job.OutputImportedAt)) &&
                          !db.AiRequestLogs.Any(a =>
                              a.UserId == job.OwnerId && a.ClientRequestId == job.Id &&
                              a.Purpose == NoteAsrBatchService.AuditPurpose &&
                              a.Status == "blocked") &&
                          (!db.AiRequestLogs.Any(a =>
                               a.UserId == job.OwnerId && a.ClientRequestId == job.Id &&
                               a.Purpose == NoteAsrBatchService.AuditPurpose) ||
                           db.AiRequestLogs.Any(a =>
                               a.UserId == job.OwnerId && a.ClientRequestId == job.Id &&
                               a.Purpose == NoteAsrBatchService.AuditPurpose &&
                               ((a.Status != "succeeded" && a.Status != "failed" &&
                                 a.Status != "incomplete" && a.Status != "timed_out" &&
                                 a.Status != "cancelled") ||
                                (job.Status == "Completed" && a.Status != "succeeded") ||
                                (job.Status == "Failed" && a.Status != "failed") ||
                                (job.Status == "Cancelled" && a.Status != "cancelled") ||
                                ((job.Status == "Expired" || job.Status == "Unknown") &&
                                 a.Status != "incomplete" && a.Status != "timed_out" &&
                                 a.Status != "cancelled") ||
                                a.TotalTokens == null))))
            .OrderBy(job => job.LastPolledAt ?? DateTimeOffset.MinValue)
            .ThenBy(job => job.OutputImportedAt ?? job.UpdatedAt)
            .ThenBy(job => job.Id)
            .Take(settings.PageSize)
            .Select(job => new { job.OwnerId, job.Id })
            .ToListAsync(ct);
        var repaired = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                var claimedAt = DateTimeOffset.UtcNow;
                var claimed = await db.NoteAsrBatchJobs
                    .Where(job => job.Id == candidate.Id && job.OwnerId == candidate.OwnerId &&
                                  (job.LastPolledAt == null || job.LastPolledAt < repairDue))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(job => job.LastPolledAt, claimedAt), ct);
                if (claimed == 0)
                    continue;
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(settings.PerJobTimeout);
                if (await service.ReconcileTerminalAuditAsync(candidate.OwnerId, candidate.Id, budget.Token))
                    repaired++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Batch audit repair timed out for job {BatchJobId}", candidate.Id);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Batch audit repair failed for job {BatchJobId}", candidate.Id);
            }
            finally
            {
                // Reconcile shares this scoped context. A failed SaveChanges must not leave the
                // rejected audit row tracked for the next candidate's save.
                db.ChangeTracker.Clear();
            }
        }
        if (repaired > 0)
            logger.LogInformation("Repaired terminal audit rows for {Count} Batch jobs", repaired);
        return repaired;
    }

    internal async Task<int> RunPageAsync(DateTimeOffset now, NoteAsrBatchPollOptions settings, CancellationToken ct) =>
        await RunPageAsync(now, settings, new HashSet<Guid>(), ct);

    internal async Task<int> RunPageAsync(DateTimeOffset now, NoteAsrBatchPollOptions settings,
        HashSet<Guid> seen, CancellationToken ct)
    {
        settings = settings.Normalized();
        var pageSize = settings.PageSize;
        var due = now - settings.PollBackoff;
        var candidates = await db.NoteAsrBatchJobs.AsNoTracking()
            .Where(x => x.BatchId != null && x.Status != "Deleted" && x.OutputImportedAt == null &&
                        (x.LastPolledAt == null || x.LastPolledAt < due))
            .OrderBy(x => x.LastPolledAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .Take(pageSize)
            .Select(x => new { x.OwnerId, x.Id })
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Id))
                continue;
            // Claim a finite lease before any provider call. The conditioned CAS prevents two
            // recurring workers from GETting the same known id, and a failed/inactive owner is
            // still leased so it cannot starve later rows in this run or the next backoff window.
            var claimedAt = DateTimeOffset.UtcNow;
            var claimDue = claimedAt - settings.PollBackoff;
            var claimed = await db.NoteAsrBatchJobs
                .Where(x => x.Id == candidate.Id && x.OwnerId == candidate.OwnerId &&
                            x.BatchId != null && x.Status != "Deleted" && x.OutputImportedAt == null &&
                            (x.LastPolledAt == null || x.LastPolledAt < claimDue))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.LastPolledAt, claimedAt)
                    .SetProperty(x => x.UpdatedAt, claimedAt)
                    .SetProperty(x => x.Revision, Guid.NewGuid()), ct);
            if (claimed == 0)
                continue;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(settings.PerJobTimeout);
            try
            {
                await service.PollForOwnerAsync(candidate.OwnerId, candidate.Id, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Batch poll timed out for job {BatchJobId}", candidate.Id);
            }
            catch (DomainException error)
            {
                // Inactive owners and vanished rows stay durable and are retried/skipped later.
                logger.LogWarning(error, "Batch poll skipped job {BatchJobId}", candidate.Id);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Batch poll failed for job {BatchJobId}", candidate.Id);
            }
        }
        return candidates.Count;
    }

    internal async Task MarkStaleCreatingUnknownAsync(DateTimeOffset now, NoteAsrBatchPollOptions settings,
        CancellationToken ct)
    {
        var cutoff = now - settings.UnknownCreateGrace;
        var staleIds = await db.NoteAsrBatchJobs.AsNoTracking()
            .Where(x => x.BatchId == null && x.Status == "Creating" && x.UpdatedAt < cutoff)
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (staleIds.Count == 0) return;
        var updated = await db.NoteAsrBatchJobs
            .Where(x => x.BatchId == null && x.Status == "Creating" && x.UpdatedAt < cutoff)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "Unknown")
                .SetProperty(x => x.StatusRaw, (string?)null)
                .SetProperty(x => x.LastErrorCode, "batch_create_unknown")
                .SetProperty(x => x.LastErrorMessage,
                    "后台轮询未发现已核验 batch id；保持 UNKNOWN 等待人工核对，禁止自动重发")
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Revision, Guid.NewGuid()), ct);
        if (updated == 0) return;
        // The submit row is the only usage evidence for this Batch. Close it as incomplete so a
        // lost create response can never disappear from the connection usage ledger.
        await db.AiRequestLogs
            .Where(x => x.Purpose == NoteAsrBatchService.AuditPurpose &&
                        x.ClientRequestId != null && x.Status == "running" &&
                        db.NoteAsrBatchJobs.Any(job => job.Id == x.ClientRequestId.Value &&
                            job.BatchId == null && job.Status == "Unknown"))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "incomplete")
                .SetProperty(x => x.ErrorCode, "batch_create_unknown")
                .SetProperty(x => x.ErrorMessage,
                    "后台轮询未发现已核验 batch id；用量保持UNKNOWN，禁止自动重发")
                .SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.ExecutionExpiresAt, (DateTimeOffset?)null), ct);
    }
}
