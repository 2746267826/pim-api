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
    public TimeSpan UnknownCreateGrace { get; set; } = TimeSpan.FromMinutes(5);

    internal NoteAsrBatchPollOptions Normalized() => new()
    {
        PageSize = Math.Clamp(PageSize, 1, 100),
        MaxPagesPerRun = Math.Clamp(MaxPagesPerRun, 1, 20),
        PerJobTimeout = Clamp(PerJobTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2)),
        PollBackoff = Clamp(PollBackoff, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1)),
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
        var seen = new HashSet<Guid>();
        for (var page = 0; page < settings.MaxPagesPerRun; page++)
        {
            var taken = await RunPageAsync(now, settings, seen, ct);
            if (taken < settings.PageSize)
                return;
        }
        logger.LogDebug("Batch poll reached the per-run page cap; remaining jobs wait for the next recurrence");
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
        await db.NoteAsrBatchJobs
            .Where(x => x.BatchId == null && x.Status == "Creating" && x.UpdatedAt < cutoff)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "Unknown")
                .SetProperty(x => x.StatusRaw, (string?)null)
                .SetProperty(x => x.LastErrorCode, "batch_create_unknown")
                .SetProperty(x => x.LastErrorMessage,
                    "后台轮询未发现已核验 batch id；保持 UNKNOWN 等待人工核对，禁止自动重发")
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Revision, Guid.NewGuid()), ct);
    }
}
