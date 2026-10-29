using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notes;

public sealed class NoteAsrDispatchJob(NoteAsrTaskStore tasks, IBackgroundJobClient jobs, ILogger<NoteAsrDispatchJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var (owner, id) in tasks.Enumerate())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await tasks.ReadAsync(owner, id, ct);
                if (!NoteAsrTaskStore.Active(state)) continue;
                using var lease = tasks.TryRunLease(owner, id);
                if (lease is not null) jobs.Enqueue<NoteAsrJob>(job => job.RunAsync(owner, id, CancellationToken.None));
            }
            catch (Exception error) when (error is IOException or DomainException or System.Text.Json.JsonException)
            { logger.LogWarning("An ASR checkpoint could not be dispatched ({ErrorType}); other tasks continue", error.GetType().Name); }
        }
    }
}

public sealed class NoteAsrJob(NoteAsrTaskStore tasks, NoteMaterialUploadStore materials,
    PimDbContext db, AiAsrCallService calls)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid owner, Guid id, CancellationToken ct)
    {
        using var lease = tasks.TryRunLease(owner, id);
        if (lease is null) return;
        // Bound each execution so recurring dispatch also services other tasks after a long backlog.
        for (var processed = 0; processed < 64; processed++)
        {
            ct.ThrowIfCancellationRequested();
            var state = await tasks.ReadAsync(owner, id, ct);
            var step = state.Steps.FirstOrDefault(x => x.Current.Status == "Running")
                ?? (!state.StopRequested ? state.Steps.FirstOrDefault(x => x.Current.Status == "Queued") : null);
            if (step is null) return;
            var requestId = step.Current.RequestId;
            await AiCallRecovery.RecoverExpiredAsync(db, ct);
            var receipt = await db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == owner && x.ClientRequestId == requestId, ct);
            if (receipt is not null)
            {
                if (receipt.Purpose != "ai.asr-call" || receipt.SourceObjectId != id.ToString())
                { await MarkAsync("Blocked", "请求标识已被另一条调用占用，请核对记录后明确恢复", true); return; }
                if (receipt.Status == "running") return;
                var recovered = AiCallAudit.Result(receipt, true);
                await RecordAsync(recovered);
                if (recovered.Status is AiRequestStatus.Running or AiRequestStatus.Blocked) return;
                continue;
            }
            if (state.StopRequested)
            {
                await MarkAsync("Cancelled", "任务已停止，此片没有发送记录", false); continue;
            }
            if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            { await MarkAsync("Blocked", "发起账号已停用，任务已停止", true); return; }
            await tasks.UpdateAsync(owner, id, current =>
            {
                if (!current.StopRequested) current.Steps[step.Index].Current.Status = "Running";
            }, ct);
            try
            {
                // Recheck deletion, immutable version and exact input fingerprints before every new send.
                var version = await materials.GetAsync(owner, state.Request.VersionId, ct);
                if (NoteMaterialManifestValidator.Hash(version.InputJson) != state.InputSha256 ||
                    NoteMaterialManifestValidator.Hash(version.ManifestJson) != state.ManifestSha256)
                    throw new DomainException(40901, "固定材料已改变，任务已停止");
                var bytes = await materials.ReadAudioSliceAsync(owner, state.Request.VersionId, step.FileIndex, step.SourceOffset, step.Samples, ct);
                // Stop can be requested while the material is verified; it does not wait for a provider call.
                if ((await tasks.ReadAsync(owner, id, ct)).StopRequested)
                { await MarkAsync("Cancelled", "任务已停止，此片未发送", false); return; }
                var request = new AiAsrCallRequest(requestId, owner, new($"segment-{step.Index + 1:D5}.wav", "wav", Convert.ToBase64String(bytes)),
                    state.Request.Consent, state.Request.Language, state.Request.Consent.ModelId);
                var result = await calls.CallForOwnerAsync(owner, request, id, ct);
                await RecordAsync(result);
                if (result.Status is AiRequestStatus.Running or AiRequestStatus.Blocked) return;
            }
            catch (OperationCanceledException) { throw; } // The next dispatch reconciles the same request ID; it never allocates another attempt.
            catch (DomainException error)
            {
                if (error.ErrorCode == 50302)
                {
                    // Busy is transient. Keep the same request ID queued for the next dispatch.
                    await MarkAsync("Queued", error.Message, false);
                    return;
                }
                await MarkAsync(error.ErrorCode == 50301 ? "Unknown" : "Blocked",
                    error.ErrorCode == 50301 ? "调用结果未确认保存，请核对调用记录后明确重试" : error.Message, true);
                return;
            }
            catch (Exception error) when (error is IOException or DbUpdateException)
            { await MarkAsync("Unknown", "材料或检查点读写中断，请核对调用记录；任务已停止", true); return; }

            async Task RecordAsync(AiTextCallResultDto result)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tasks.UpdateAsync(owner, id, current =>
                {
                    var attempt = current.Steps[step.Index].Current;
                    if (attempt.RequestId != requestId) throw new InvalidDataException("ASR attempt changed during execution");
                    attempt.LogId = result.LogId; attempt.Status = result.Status.ToString(); attempt.ErrorMessage = result.ErrorMessage;
                    current.StopRequested |= result.Status == AiRequestStatus.Blocked;
                }, budget.Token);
            }
            async Task MarkAsync(string status, string message, bool stop)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tasks.UpdateAsync(owner, id, current =>
                {
                    var attempt = current.Steps[step.Index].Current;
                    attempt.Status = status; attempt.ErrorMessage = message;
                    current.StopRequested |= stop;
                }, budget.Token);
            }
        }
    }
}
