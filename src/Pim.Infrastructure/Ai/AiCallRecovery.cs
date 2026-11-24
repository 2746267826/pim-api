using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

public static class AiCallRecovery
{
    public static async Task RecoverExpiredAsync(PimDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var query = db.AiRequestLogs.Where(x => x.Status == "running" && x.ExecutionExpiresAt < now);
        const string message = "请求执行期限已过，结果未确认；请先核对记录，重新调用会产生新的请求。";
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ai_request_logs
                SET status = 'incomplete',
                    error_code = 'outcome_unknown',
                    error_message = {message},
                    finished_at = execution_expires_at,
                    metadata_json = jsonb_set(
                        CASE WHEN jsonb_typeof(metadata_json) = 'object'
                            THEN metadata_json ELSE jsonb_build_object() END,
                        ARRAY['providerAttempted'],
                        'true'::jsonb,
                        true)
                WHERE status = 'running' AND execution_expires_at < {now}
                """, ct);
            return;
        }
        if (db.Database.IsRelational())
        {
            var rows = await query.ToListAsync(ct);
            foreach (var row in rows) Apply(row, now);
            if (rows.Count > 0) await db.SaveChangesAsync(ct);
            return;
        }
        else
        {
            var rows = await query.ToListAsync(ct);
            foreach (var row in rows)
                Apply(row, now);
            if (rows.Count > 0) await db.SaveChangesAsync(ct);
        }

        static void Apply(AiRequestLogEntity row, DateTimeOffset recoveredAt)
        {
            row.Status = "incomplete";
            row.ErrorCode = "outcome_unknown";
            row.ErrorMessage = message;
            row.FinishedAt = row.ExecutionExpiresAt ?? recoveredAt;
            row.MetadataJson = AiAsrJobService.SetProviderAttemptedMetadata(
                row.MetadataJson, true);
        }
    }
}

public sealed class AiTextCallGate
{
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly SemaphoreSlim _visionGate = new(2, 2);
    private readonly SemaphoreSlim _asrGate = new(2, 2);
    public bool TryEnter(bool vision = false, bool asr = false)
    {
        if (vision && asr) throw new ArgumentException("A call has one media kind");
        if (vision && !_visionGate.Wait(0)) return false;
        if (asr && !_asrGate.Wait(0)) return false;
        if (_gate.Wait(0)) return true;
        if (vision) _visionGate.Release();
        if (asr) _asrGate.Release();
        return false;
    }
    public void Exit(bool vision = false, bool asr = false) { _gate.Release(); if (vision) _visionGate.Release(); if (asr) _asrGate.Release(); }
}
