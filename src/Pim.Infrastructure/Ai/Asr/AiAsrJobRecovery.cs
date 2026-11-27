using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai.Asr;

public static class AiAsrJobRecovery
{
    private const string WorkerLostMessage =
        "ASR后台执行租约已过期，服务商结果未确认；请先核对调用记录，不要自动重发。";

    /// <summary>
    /// A restart must not relabel an interrupted provider call as a user cancellation. Running and
    /// cancelling jobs become an explicit unknown outcome and are never automatically replayed.
    /// </summary>
    public static async Task RecoverExpiredAsync(PimDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = db.AiAsrJobs.Where(x =>
            (x.Status == "preparing" || x.Status == "running" || x.Status == "cancelling") &&
            x.ExecutionExpiresAt != null && x.ExecutionExpiresAt < now);
        if (!db.Database.IsRelational())
        {
            var ids = await expired.AsNoTracking().Select(x => x.Id).Take(200).ToListAsync(ct);
            foreach (var id in ids)
            {
                AiAsrJobEntity? row = null;
                AiRequestLogEntity? log = null;
                try
                {
                    row = await db.AiAsrJobs.SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (row is null || !IsExpired(row, now)) continue;
                    log = await db.AiRequestLogs.SingleOrDefaultAsync(x => x.Id == row.LogId, ct);
                    // A single row closed by another recovery path must not abort the whole scan.
                    if (log is not null && !CanOverwriteAudit(log.Status)) continue;
                    Apply(row, now);
                    if (log is not null) ApplyLog(row, log, now);
                    await db.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Recovery is best-effort per row; one malformed or conflicted row must not
                    // starve every later expired job.
                }
                finally
                {
                    if (row is not null)
                        db.Entry(row).State = EntityState.Detached;
                    if (log is not null)
                        db.Entry(log).State = EntityState.Detached;
                }
            }
            return;
        }

        var candidates = await expired.AsNoTracking()
            .Select(x => new { x.Id, x.OwnerId, x.LogId })
            .Take(200).ToListAsync(ct);
        if (candidates.Count == 0) return;
        var strategy = db.Database.CreateExecutionStrategy();
        foreach (var candidate in candidates)
        {
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    // Keep each job and its audit row in one transaction, but do not let one
                    // conflicted row abort the whole recovery batch. A missing or already-terminal
                    // audit row rolls back only that candidate.
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var affected = await db.AiAsrJobs.FromSqlInterpolated($"""
                        UPDATE ai_asr_jobs
                        SET status = CASE
                                WHEN status = 'preparing' THEN 'failed'
                                WHEN status = 'cancelling' THEN 'cancelled'
                                ELSE 'incomplete'
                            END,
                            abort_source = CASE
                                WHEN status = 'preparing' THEN 'preparation_interrupted'
                                WHEN status = 'cancelling' THEN 'user_cancel_worker_lost'
                                ELSE 'worker_lost'
                            END,
                            outcome_known = (status = 'preparing'),
                            provider_attempted = CASE WHEN status = 'preparing' THEN false ELSE true END,
                            response_body_complete = false,
                            error_code = CASE
                                WHEN status = 'preparing' THEN 'preparation_interrupted'
                                WHEN status = 'cancelling' THEN 'user_cancel_outcome_unknown'
                                ELSE 'worker_lost_outcome_unknown'
                            END,
                            error_message = CASE
                                WHEN status = 'preparing' THEN 'ASR任务在接受过程中中断，未发送服务商'
                                WHEN status = 'cancelling' THEN {WorkerLostMessage}
                                ELSE {WorkerLostMessage}
                            END,
                            next_action = CASE
                                WHEN status = 'preparing' THEN '重新选择原始音频后明确新建请求'
                                ELSE '核对调用记录；如需再次调用，请明确新建请求'
                            END,
                            finished_at = {now},
                            execution_expires_at = NULL
                        WHERE id = {candidate.Id}
                          AND owner_id = {candidate.OwnerId}
                          AND status IN ('preparing', 'running', 'cancelling')
                          AND execution_expires_at IS NOT NULL
                          AND execution_expires_at < {now}
                        RETURNING *
                        """).AsNoTracking().ToListAsync(ct);
                    if (affected.Count != 1)
                    {
                        await transaction.RollbackAsync(ct);
                        return;
                    }
                    var row = affected[0];
                    var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE ai_request_logs
                        SET status = {row.Status},
                            error_code = {row.ErrorCode},
                            error_message = {row.ErrorMessage},
                            finished_at = {now},
                            execution_expires_at = NULL,
                            metadata_json = jsonb_set(
                                CASE WHEN jsonb_typeof(metadata_json) = 'object'
                                    THEN metadata_json ELSE jsonb_build_object() END,
                                ARRAY['providerAttempted'],
                                to_jsonb({row.ProviderAttempted}),
                                true)
                        WHERE id = {row.LogId}
                          AND user_id = {row.OwnerId}
                          -- AiCallRecovery may already have marked a legacy ASR log incomplete
                          -- before this job-scoped recovery ran. The job row above is the winner,
                          -- so the audit row must follow it instead of aborting the whole batch.
                          AND status IN ('preparing', 'running', 'cancelling', 'incomplete')
                        """, ct);
                    if (updated != 1)
                    {
                        await transaction.RollbackAsync(ct);
                        return;
                    }
                    await transaction.CommitAsync(ct);
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Continue with the remaining expired jobs. The candidate transaction above
                // rolls back independently, so no job/audit split is committed.
            }
        }
    }

    private static bool IsExpired(AiAsrJobEntity row, DateTimeOffset now) =>
        row.Status is "preparing" or "running" or "cancelling" &&
        row.ExecutionExpiresAt is { } expires && expires < now;

    private static bool CanOverwriteAudit(string status) =>
        status is "preparing" or "running" or "cancelling" or "incomplete";

    private static void Apply(AiAsrJobEntity row, DateTimeOffset now)
    {
        row.FinishedAt = now;
        row.ExecutionExpiresAt = null;
        if (row.Status == "preparing")
        {
            row.Status = "failed";
            row.AbortSource = "preparation_interrupted";
            row.OutcomeKnown = true;
            row.ProviderAttempted = false;
            row.ResponseBodyComplete = false;
            row.ErrorCode = "preparation_interrupted";
            row.ErrorMessage = "ASR任务在接受过程中中断，未发送服务商";
            row.NextAction = "重新选择原始音频后明确新建请求";
        }
        else if (row.Status == "cancelling")
        {
            row.Status = "cancelled";
            row.AbortSource = "user_cancel_worker_lost";
            row.OutcomeKnown = false;
            row.ProviderAttempted = true;
            row.ErrorCode = "user_cancel_outcome_unknown";
            row.ErrorMessage = WorkerLostMessage;
            row.NextAction = "核对调用记录；如需再次调用，请明确新建请求";
        }
        else
        {
            row.Status = "incomplete";
            row.AbortSource = "worker_lost";
            row.OutcomeKnown = false;
            row.ProviderAttempted = true;
            row.ErrorCode = "worker_lost_outcome_unknown";
            row.ErrorMessage = WorkerLostMessage;
            row.NextAction = "核对调用记录；如需再次调用，请明确新建请求";
        }
    }

    private static void ApplyLog(AiAsrJobEntity row, AiRequestLogEntity log, DateTimeOffset now)
    {
        log.Status = row.Status;
        log.ErrorCode = row.ErrorCode;
        log.ErrorMessage = row.ErrorMessage;
        log.FinishedAt = now;
        log.ExecutionExpiresAt = null;
        log.MetadataJson = AiAsrJobService.SetProviderAttemptedMetadata(
            log.MetadataJson, row.ProviderAttempted);
    }
}
