using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.NoteTaskSuggestions.Entities;
using Pim.Module.NoteTaskSuggestions.Services;

namespace Pim.Module.NoteTaskSuggestions.Jobs;

internal sealed class NoteTaskSuggestionExtractionJob(
    PimDbContext db,
    INoteTaskSuggestionModelClient model,
    TimeProvider timeProvider,
    ILogger<NoteTaskSuggestionExtractionJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid owner, Guid extractionId, CancellationToken ct)
    {
        var extraction = await db.Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == extractionId && x.UserId == owner, ct);
        if (extraction is null ||
            extraction.Status is NoteTaskSuggestionExtractionStatuses.Succeeded or
                NoteTaskSuggestionExtractionStatuses.Failed)
            return;

        var now = timeProvider.GetUtcNow();
        var staleRunningBefore = now.AddMinutes(-5);
        var claimed = await db.Set<NoteTaskSuggestionExtractionEntity>()
            .Where(x => x.Id == extractionId && x.UserId == owner &&
                x.Revision == extraction.Revision &&
                (x.Status == NoteTaskSuggestionExtractionStatuses.Queued ||
                 (x.Status == NoteTaskSuggestionExtractionStatuses.Running &&
                  x.UpdatedAt < staleRunningBefore)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, NoteTaskSuggestionExtractionStatuses.Running)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.StartedAt, x => x.StartedAt ?? now)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Revision, Guid.NewGuid()), ct);
        if (claimed != 1)
            return;

        extraction = await db.Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == extractionId && x.UserId == owner, ct);
        if (extraction is null || extraction.Status != NoteTaskSuggestionExtractionStatuses.Running)
            return;

        try
        {
            var result = await model.ExtractAsync(owner, extraction, ct);
            if (result.Status == AiRequestStatus.Running)
                return;
            if (result.Status != AiRequestStatus.Succeeded || string.IsNullOrWhiteSpace(result.ResponseText))
            {
                await FailAsync(owner, extractionId, result.LogId,
                    "ai_" + result.Status.ToString().ToLowerInvariant(),
                    result.ErrorMessage ?? "文本模型未返回可用的任务建议；不会自动重发", ct);
                return;
            }

            IReadOnlyList<NoteTaskSuggestionDraft> drafts;
            try
            {
                drafts = NoteTaskSuggestionPrompt.Parse(result.ResponseText);
            }
            catch (InvalidDataException error)
            {
                await FailAsync(owner, extractionId, result.LogId, "model_output_invalid",
                    error.Message, ct);
                return;
            }

            await PersistSuccessAsync(owner, extractionId, result.LogId, drafts, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DomainException error)
        {
            await FailAsync(owner, extractionId, null,
                error.ErrorCode == 40901 ? "destination_changed" : "extraction_blocked",
                error.Message, ct);
        }
        catch (Exception error) when (
            error is DbUpdateException or IOException or InvalidDataException or
                System.Text.Json.JsonException)
        {
            logger.LogWarning(error,
                "Task suggestion extraction {ExtractionId} failed at a durable checkpoint", extractionId);
            await FailAsync(owner, extractionId, null, "checkpoint_failed",
                "任务建议提取检查点写入失败；不会自动重发，请核对后新建请求", ct);
        }
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task RunQueuedAsync(CancellationToken ct)
    {
        var pending = await db.Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .Where(x => x.Status == NoteTaskSuggestionExtractionStatuses.Queued ||
                        x.Status == NoteTaskSuggestionExtractionStatuses.Running)
            .OrderBy(x => x.CreatedAt)
            .Take(20)
            .Select(x => new { x.UserId, x.Id })
            .ToListAsync(ct);
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunAsync(item.UserId, item.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogWarning(error,
                    "Queued task suggestion extraction {ExtractionId} failed; later rows continue",
                    item.Id);
            }
        }
    }

    private async Task PersistSuccessAsync(
        Guid owner,
        Guid extractionId,
        Guid logId,
        IReadOnlyList<NoteTaskSuggestionDraft> drafts,
        CancellationToken ct)
    {
        await AdvisoryLock.RunAsync(db, NoteTaskSuggestionService.Key(extractionId), async () =>
        {
            var extraction = await db.Set<NoteTaskSuggestionExtractionEntity>()
                .SingleOrDefaultAsync(x => x.Id == extractionId && x.UserId == owner, ct)
                ?? throw new DomainException(40401, "任务建议提取记录不存在");
            if (extraction.Status == NoteTaskSuggestionExtractionStatuses.Succeeded)
                return 0;
            if (extraction.Status != NoteTaskSuggestionExtractionStatuses.Running)
                throw new DomainException(40901, "任务建议提取状态已改变");

            var existing = await db.Set<NoteTaskSuggestionEntity>()
                .Where(x => x.ExtractionId == extractionId && x.UserId == owner)
                .OrderBy(x => x.Ordinal)
                .ToListAsync(ct);
            if (existing.Count > 0)
            {
                extraction.SuggestionCount = existing.Count;
                extraction.Status = NoteTaskSuggestionExtractionStatuses.Succeeded;
                extraction.AiCallLogId = logId == Guid.Empty ? null : logId;
                extraction.FinishedAt = timeProvider.GetUtcNow();
                extraction.UpdatedAt = extraction.FinishedAt.Value;
                extraction.Revision = Guid.NewGuid();
                await db.SaveChangesAsync(ct);
                return existing.Count;
            }

            var now = timeProvider.GetUtcNow();
            var ordinal = 0;
            foreach (var draft in drafts)
            {
                db.Set<NoteTaskSuggestionEntity>().Add(new NoteTaskSuggestionEntity
                {
                    UserId = owner,
                    ExtractionId = extraction.Id,
                    Ordinal = ordinal++,
                    NoteId = extraction.NoteId,
                    ArtifactId = extraction.ArtifactId,
                    ArtifactVersionId = extraction.ArtifactVersionId,
                    SourceContentSha256 = extraction.SourceContentSha256,
                    SourceInputSha256 = extraction.SourceInputSha256,
                    SourceManifestSha256 = extraction.SourceManifestSha256,
                    SourceGenerationTaskId = extraction.SourceGenerationTaskId,
                    Title = draft.Title,
                    Description = draft.Description,
                    Priority = draft.Priority,
                    Due = draft.Due,
                    Status = NoteTaskSuggestionStatuses.Draft,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            extraction.SuggestionCount = drafts.Count;
            extraction.Status = NoteTaskSuggestionExtractionStatuses.Succeeded;
            extraction.AiCallLogId = logId == Guid.Empty ? null : logId;
            extraction.ErrorCode = null;
            extraction.ErrorMessage = null;
            extraction.FinishedAt = now;
            extraction.UpdatedAt = now;
            extraction.Revision = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
            return drafts.Count;
        }, ct);
    }

    private Task FailAsync(
        Guid owner, Guid extractionId, Guid? logId, string code, string message, CancellationToken ct)
        => AdvisoryLock.RunAsync(db, NoteTaskSuggestionService.Key(extractionId), async () =>
        {
            var extraction = await db.Set<NoteTaskSuggestionExtractionEntity>()
                .SingleOrDefaultAsync(x => x.Id == extractionId && x.UserId == owner, ct);
            if (extraction is null ||
                extraction.Status == NoteTaskSuggestionExtractionStatuses.Succeeded)
                return false;

            var now = timeProvider.GetUtcNow();
            extraction.Status = NoteTaskSuggestionExtractionStatuses.Failed;
            extraction.AiCallLogId = logId is { } id && id != Guid.Empty ? id : extraction.AiCallLogId;
            extraction.ErrorCode = Trim(code, 80);
            extraction.ErrorMessage = Trim(message, 2000);
            extraction.FinishedAt = now;
            extraction.UpdatedAt = now;
            extraction.Revision = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
