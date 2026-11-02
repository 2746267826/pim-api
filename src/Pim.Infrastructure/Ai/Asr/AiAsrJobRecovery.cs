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
            var rows = await expired.ToListAsync(ct);
            if (rows.Count == 0) return;
            foreach (var row in rows)
            {
                Apply(row, now);
                var log = await db.AiRequestLogs.SingleOrDefaultAsync(x => x.Id == row.LogId, ct);
                if (log is not null) ApplyLog(row, log, now);
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        var candidates = await expired.AsNoTracking()
            .Select(x => new { x.Id, x.OwnerId, x.LogId })
            .Take(200).ToListAsync(ct);
        if (candidates.Count == 0) return;
        var candidateIds = candidates.Select(x => x.Id).ToArray();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Keep the conditional update and its audit update in one transaction. The UPDATE's
            // RETURNING rows are the exact jobs recovered in this attempt; no second candidate
            // read can race between selection and mutation. A completion or cancel that wins the
            // row lock first therefore cannot leave the job and audit row split.
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
                    provider_attempted = CASE WHEN status = 'preparing' THEN false ELSE provider_attempted END,
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
                WHERE id = ANY({candidateIds})
                  AND status IN ('preparing', 'running', 'cancelling')
                  AND execution_expires_at IS NOT NULL
                  AND execution_expires_at < {now}
                RETURNING *
                """).AsNoTracking().ToListAsync(ct);
            if (affected.Count > 0)
            {
                foreach (var row in affected)
                {
                    var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE ai_request_logs
                        SET status = {row.Status},
                            error_code = {row.ErrorCode},
                            error_message = {row.ErrorMessage},
                            finished_at = {now},
                            execution_expires_at = NULL
                        WHERE id = {row.LogId}
                          AND user_id = {row.OwnerId}
                          AND status IN ('preparing', 'running', 'cancelling')
                        """, ct);
                    if (updated != 1)
                        throw new InvalidOperationException(
                            "ASR recovery lost its owner-bound audit row; refusing a split state");
                }
            }
            await transaction.CommitAsync(ct);
        });
    }

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
            row.ErrorCode = "user_cancel_outcome_unknown";
            row.ErrorMessage = WorkerLostMessage;
            row.NextAction = "核对调用记录；如需再次调用，请明确新建请求";
        }
        else
        {
            row.Status = "incomplete";
            row.AbortSource = "worker_lost";
            row.OutcomeKnown = false;
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
    }
}
