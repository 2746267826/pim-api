using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Re-validates the frozen route-config revision and consent version at the last possible moment
/// before a paid/provider side effect. A revision that no longer matches is never re-bound to the
/// newer policy: the attempt is revoked and the task is stopped, so an old consent cannot continue
/// to authorize new work.
/// </summary>
internal static class NoteAsrRouteConsentGuard
{
    internal const string RouteDriftCode = "route_consent_drift";
    internal const string NonCanonicalCode = "noncanonical_initial_task";
    internal const string RouteDriftMessage = "路由配置或同意版本已变化，请刷新后重新确认";
    internal const string NonCanonicalMessage =
        "该材料版本已有当前转写链；历史重复任务不会继续执行或重复计费";

    internal static async Task<bool> IsCurrentAsync(PimDbContext db, Guid owner,
        Guid configRevision, string? consentVersion, CancellationToken ct)
    {
        var expected = consentVersion ?? "";
        return await db.NoteAsrRouteConfigs.AsNoTracking().AnyAsync(x => x.OwnerId == owner &&
            x.Revision == configRevision && x.ConsentVersion == expected, ct);
    }

    internal static async Task RequireCurrentAsync(PimDbContext db, Guid owner,
        Guid configRevision, string? consentVersion, CancellationToken ct)
    {
        if (!await IsCurrentAsync(db, owner, configRevision, consentVersion, ct))
            throw new DomainException(40901, RouteDriftMessage);
    }

    internal static async Task RequireCurrentForCloudAttemptAsync(PimDbContext db, Guid owner,
        NoteAsrAttemptEntity attempt, CancellationToken ct)
    {
        if (!NoteAsrRoute.IsCloud(attempt.Route)) return;
        if (await IsCurrentAsync(db, owner, attempt.ConfigRevision, attempt.ConsentVersion, ct))
            return;
        await RevokeAttemptAsync(db, owner, attempt.Id, RouteDriftCode, RouteDriftMessage, ct);
        throw new DomainException(40901, RouteDriftMessage);
    }

    /// <summary>
    /// Revokes a stale attempt without letting it report success later. A verified success or an
    /// explicit cancellation is terminal and never rewritten; active leases are always expired.
    /// </summary>
    internal static async Task RevokeAttemptAsync(PimDbContext db, Guid owner, Guid attemptId,
        string code, string message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (db.Database.IsRelational())
        {
            await db.NoteAsrLeases
                .Where(x => x.OwnerId == owner && x.AttemptId == attemptId && x.Status == "Active")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Expired")
                    .SetProperty(x => x.EndedAt, now), ct);
            await db.NoteAsrAttempts
                .Where(x => x.Id == attemptId && x.OwnerId == owner &&
                    x.State != "Succeeded" && x.State != "Cancelled")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.StopRequested, true)
                    .SetProperty(x => x.State, "Failed")
                    .SetProperty(x => x.LastErrorCode, code)
                    .SetProperty(x => x.LastErrorMessage, message)
                    .SetProperty(x => x.UpdatedAt, now), ct);
            return;
        }

        // Explicit tracked branch for non-relational test hosts; production uses the two
        // single-statement updates above.
        var leases = await db.NoteAsrLeases.Where(x => x.OwnerId == owner &&
            x.AttemptId == attemptId && x.Status == "Active").ToListAsync(ct);
        foreach (var lease in leases)
        {
            lease.Status = "Expired";
            lease.EndedAt = now;
        }
        var attempt = await db.NoteAsrAttempts.SingleOrDefaultAsync(x =>
            x.Id == attemptId && x.OwnerId == owner, ct);
        if (attempt is not null && attempt.State is not ("Succeeded" or "Cancelled"))
        {
            attempt.StopRequested = true;
            attempt.State = "Failed";
            attempt.LastErrorCode = code;
            attempt.LastErrorMessage = message;
            attempt.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stops the task file for a revoked attempt. A bounded, caller-independent token keeps a stuck
    /// checkpoint file from holding an already-decided DB revocation hostage.
    /// </summary>
    internal static async Task StopTaskAsync(NoteAsrTaskStore tasks, Guid owner, Guid taskId,
        Guid? expectedAttemptId, string message, CancellationToken ct)
    {
        _ = ct;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var observed = await tasks.ReadAsync(owner, taskId, budget.Token);
            if (expectedAttemptId is { } expected && observed.AttemptId != expected) return;
            await tasks.UpdateAsync(owner, taskId, current =>
            {
                if (expectedAttemptId is { } bound && current.AttemptId != bound) return;
                current.StopRequested = true;
                foreach (var step in current.Steps.Where(step =>
                             step.Current.Status is "Queued" or "Running" or "Unknown"))
                {
                    step.Current.Status = "Blocked";
                    step.Current.ErrorMessage = message;
                }
            }, budget.Token);
        }
        catch (DomainException error) when (error.ErrorCode == 40401) { }
        catch (Exception error) when (error is IOException or OperationCanceledException
            or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidDataException) { }
    }
}
