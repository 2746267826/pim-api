using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Ai.Asr;

public interface IAsrJobDispatcher
{
    bool IsAvailable { get; }
    string Enqueue(Guid owner, Guid clientRequestId);
}

public sealed class HangfireAsrJobDispatcher(IBackgroundJobClient jobs) : IAsrJobDispatcher
{
    public bool IsAvailable => true;

    public string Enqueue(Guid owner, Guid clientRequestId) =>
        jobs.Enqueue<AiAsrJob>(job => job.RunAsync(owner, clientRequestId, CancellationToken.None));
}

public sealed class UnavailableAsrJobDispatcher : IAsrJobDispatcher
{
    public bool IsAvailable => false;

    public string Enqueue(Guid owner, Guid clientRequestId) =>
        throw new InvalidOperationException("Durable ASR jobs require Hangfire storage.");
}

public sealed class AiAsrJob(AiAsrJobService service)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(Guid owner, Guid clientRequestId, CancellationToken ct) =>
        service.ProcessAsync(owner, clientRequestId, ct);
}

/// <summary>
/// Replays queue rows whose best-effort Hangfire enqueue was lost with a process/network failure.
/// The worker still claims each row with a database conditional update, so a duplicate queue item
/// cannot cause a second provider request.
/// </summary>
public sealed class AiAsrJobDispatchJob(PimDbContext db, IAsrJobDispatcher dispatcher,
    ILogger<AiAsrJobDispatchJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        if (!dispatcher.IsAvailable) return;
        await AiAsrJobRecovery.RecoverExpiredAsync(db, ct);
        var candidates = await db.AiAsrJobs.AsNoTracking()
            .Where(x => x.Status == "queued")
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new { x.OwnerId, x.ClientRequestId })
            .Take(200)
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            try
            {
                dispatcher.Enqueue(candidate.OwnerId, candidate.ClientRequestId);
            }
            catch (Exception error)
            {
                logger.LogWarning(error,
                    "Durable ASR job dispatch remains pending for {ClientRequestId}; the next scan will retry",
                    candidate.ClientRequestId);
            }
        }
    }
}
