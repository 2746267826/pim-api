using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Core.Operations;
using Pim.Core.Storage;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Domain service for the native-note part of the unified recycle bin. Soft deletion
/// only flips the note's ledger state; original materials, ASR checkpoints and derived
/// artifacts stay readable until an explicit permanent purge.
/// </summary>
public sealed class NativeNoteRecycleService(
    PimDbContext db,
    NoteMaterialUploadStore materials,
    NoteDerivedStore derived,
    NoteAsrTaskStore asr,
    IAuditLogService audit,
    IFileAttachmentStore? attachments = null,
    ILogger<NativeNoteRecycleService>? logger = null,
    NoteProcessingStore? processing = null,
    IEnumerable<INotePurgeContributor>? purgeContributors = null)
{
    private const string AsrSourceType = "native-note-asr-task";
    private const string DerivedSourceType = "native-note-derived-task";
    private static readonly string[] TerminalBatchStatuses =
        ["Deleted", "Completed", "Failed", "Expired", "Cancelled"];

    public Task<IReadOnlyList<NativeNoteDeletedItem>> ListDeletedAsync(Guid owner,
        CancellationToken ct = default) => materials.ListDeletedAsync(owner, ct);

    public Task<NativeNoteDeletedItem> SoftDeleteAsync(Guid owner, Guid noteId,
        CancellationToken ct = default)
        => AdvisoryLock.RunAsync(
            db,
            LockKey(owner, noteId),
            () => SoftDeleteCoreAsync(owner, noteId, ct),
            ct);

    private async Task<NativeNoteDeletedItem> SoftDeleteCoreAsync(Guid owner, Guid noteId,
        CancellationToken ct)
    {
        var deleted = await materials.SoftDeleteAsync(owner, noteId, ct);
        audit.Enqueue(new CreateAuditLogRequest(
            owner,
            AuditActorType.User,
            "recycle_bin.note.soft_delete",
            "native_note",
            noteId.ToString("D"),
            "recycle-bin",
            AuditResult.Success,
            null,
            null,
            null,
            new Dictionary<string, string>
            {
                ["retained"] = "materials,asr,derived"
            },
            null,
            null));
        await db.SaveChangesAsync(ct);
        return deleted;
    }

    public Task<NativeNoteRecycleResult> RestoreAsync(Guid owner, Guid noteId,
        DateTimeOffset? expectedDeletedAt = null,
        CancellationToken ct = default)
        => AdvisoryLock.RunAsync(
            db,
            LockKey(owner, noteId),
            () => RestoreCoreAsync(owner, noteId, expectedDeletedAt, ct),
            ct);

    private async Task<NativeNoteRecycleResult> RestoreCoreAsync(Guid owner, Guid noteId,
        DateTimeOffset? expectedDeletedAt,
        CancellationToken ct)
    {
        var deleted = await materials.GetDeletedAsync(owner, noteId, ct);
        if (deleted is null)
        {
            try
            {
                _ = await materials.GetCurrentAsync(owner, noteId, ct);
            }
            catch (DomainException error) when (error.ErrorCode == 40401)
            {
                throw new DomainException(40401, "笔记不存在或已永久删除");
            }

            return new NativeNoteRecycleResult(noteId, "笔记", false, "笔记已在笔记库中，无需重复恢复");
        }

        var restored = await materials.RestoreSoftDeletedAsync(owner, noteId, expectedDeletedAt, ct);
        if (!restored)
        {
            try
            {
                _ = await materials.GetCurrentAsync(owner, noteId, ct);
            }
            catch (DomainException error) when (error.ErrorCode == 40401)
            {
                throw new DomainException(40401, "笔记不存在或已永久删除");
            }
        }
        else
        {
            audit.Enqueue(new CreateAuditLogRequest(
                owner,
                AuditActorType.User,
                "recycle_bin.note.restore",
                "native_note",
                noteId.ToString("D"),
                "recycle-bin",
                AuditResult.Success,
                null,
                null,
                null,
                null,
                null,
                null));
            await db.SaveChangesAsync(ct);
        }

        return new NativeNoteRecycleResult(
            noteId,
            deleted.Title,
            restored,
            restored ? "笔记已恢复到笔记库" : "笔记已在笔记库中，无需重复恢复");
    }

    public Task<NativeNotePurgeResult> PurgeAsync(Guid owner, Guid noteId,
        DateTimeOffset? expectedDeletedAt = null,
        CancellationToken ct = default)
        => AdvisoryLock.RunAsync(
            db,
            LockKey(owner, noteId),
            () => PurgeCoreAsync(owner, noteId, expectedDeletedAt, ct),
            ct);

    private async Task<NativeNotePurgeResult> PurgeCoreAsync(Guid owner, Guid noteId,
        DateTimeOffset? expectedDeletedAt,
        CancellationToken ct)
    {
        var deleted = await materials.GetDeletedAsync(owner, noteId, ct);
        if (deleted is null)
        {
            try
            {
                _ = await materials.GetCurrentAsync(owner, noteId, ct);
            }
            catch (DomainException error) when (error.ErrorCode == 40401)
            {
                if (expectedDeletedAt.HasValue)
                    return new NativeNotePurgeResult(0, 0, 0, 0, 0, 0);
                throw new DomainException(40401, "笔记不存在");
            }

            throw new DomainException(40901, "请先将笔记移入回收站，再永久删除");
        }
        EnsureExpectedDeletedAt(deleted.DeletedAt, expectedDeletedAt);

        var asrTaskIds = await asr.ListForNoteAsync(owner, noteId, ct);
        foreach (var taskId in asrTaskIds)
        {
            if (NoteAsrTaskStore.Active(await asr.ReadAsync(owner, taskId, ct)))
                throw new DomainException(40901, "笔记的转写任务正在执行，请等待任务结束或停止后重试永久删除");
        }

        var derivedTasks = await derived.ListTasksAsync(owner, noteId, ct);
        if (derivedTasks.Any(NoteDerivedStore.IsActive))
            throw new DomainException(40901, "笔记的派生任务正在执行，请等待任务结束或停止后重试永久删除");

        var asrTaskIdSet = asrTaskIds.ToArray();
        var attempts = await db.NoteAsrAttempts
            .Where(row => row.OwnerId == owner
                && (row.NoteId == noteId || asrTaskIdSet.Contains(row.TaskId)))
            .ToListAsync(ct);
        if (attempts.Any(row => row.State == "Leased"
                || (row.State == "Queued" && !row.StopRequested)))
            throw new DomainException(40901, "笔记的转写执行快照仍在进行，请等待任务结束或停止后重试永久删除");
        var attemptIds = attempts.Select(row => row.Id).ToArray();
        var now = DateTimeOffset.UtcNow;
        var leases = attemptIds.Length == 0
            ? new List<NoteAsrLeaseEntity>()
            : await db.NoteAsrLeases
                .Where(row => row.OwnerId == owner && attemptIds.Contains(row.AttemptId))
                .ToListAsync(ct);
        if (leases.Any(row => row.Status == "Active" && row.ExpiresAt > now))
            throw new DomainException(40901, "笔记的转写租约仍有效，请等待任务结束或停止后重试永久删除");
        var receipts = attemptIds.Length == 0
            ? new List<NoteAsrAckReceiptEntity>()
            : await db.NoteAsrAckReceipts
                .Where(row => row.OwnerId == owner && attemptIds.Contains(row.AttemptId))
                .ToListAsync(ct);
        var evidence = attemptIds.Length == 0 && asrTaskIdSet.Length == 0
            ? new List<NoteAsrSegmentEvidenceEntity>()
            : await db.NoteAsrSegmentEvidence
                .Where(row => row.OwnerId == owner
                    && (attemptIds.Contains(row.AttemptId)
                        || asrTaskIdSet.Contains(row.TaskId)))
                .ToListAsync(ct);
        var batchJobs = await db.NoteAsrBatchJobs
            .Where(row => row.OwnerId == owner
                && (asrTaskIdSet.Contains(row.TaskId)
                    || (row.AttemptId.HasValue && attemptIds.Contains(row.AttemptId.Value))))
            .ToListAsync(ct);
        if (batchJobs.Any(row => !TerminalBatchStatuses.Contains(row.Status)))
            throw new DomainException(40901, "笔记的 Batch 任务仍在进行，请等待任务结束或取消后重试永久删除");

        var derivedTaskIds = derivedTasks.Select(task => task.Request.TaskId).ToArray();
        var sourceIds = asrTaskIds.Concat(derivedTaskIds)
            .Select(id => id.ToString("D"))
            .ToHashSet(StringComparer.Ordinal);

        var logIds = new HashSet<Guid>();
        if (sourceIds.Count > 0)
        {
            var sourceLogIds = await db.AiRequestLogs
                .Where(log => log.UserId == owner
                    && sourceIds.Contains(log.SourceObjectId)
                    && (log.SourceObjectType == AsrSourceType || log.SourceObjectType == DerivedSourceType))
                .Select(log => log.Id)
                .ToListAsync(ct);
            logIds.UnionWith(sourceLogIds);
        }
        if (purgeContributors is not null)
        {
            foreach (var contributor in purgeContributors)
                logIds.UnionWith((await contributor.StagePurgeAsync(owner, noteId, ct)).AiLogIds);
        }
        await materials.MarkPurgeStartedAsync(owner, noteId, expectedDeletedAt, ct);
        var logIdSet = logIds.ToArray();
        var logs = logIdSet.Length == 0
            ? new List<AiRequestLogEntity>()
            : await db.AiRequestLogs
                .Where(log => log.UserId == owner && logIdSet.Contains(log.Id))
                .ToListAsync(ct);
        var images = logIdSet.Length == 0
            ? new List<AiCallImageEntity>()
            : await db.AiCallImages.Where(row => logIdSet.Contains(row.LogId)).ToListAsync(ct);
        var audio = logIdSet.Length == 0
            ? new List<AiCallAudioEntity>()
            : await db.AiCallAudio.Where(row => logIdSet.Contains(row.LogId)).ToListAsync(ct);

        if (attachments is not null)
        {
            foreach (var row in images.Where(row => row.ObjectKey is not null && row.ProviderKey is not null))
            {
                await attachments.DeleteAsync(
                    owner,
                    new FileAttachmentRef(row.ObjectKey!, row.ProviderKey!, row.ProviderId),
                    ct);
            }
            foreach (var row in audio.Where(row => row.ObjectKey is not null && row.ProviderKey is not null))
            {
                await attachments.DeleteAsync(
                    owner,
                    new FileAttachmentRef(row.ObjectKey!, row.ProviderKey!, row.ProviderId),
                    ct);
            }
        }

        await asr.DeleteForNoteAsync(owner, noteId, ct);
        await derived.DeleteNoteAsync(owner, noteId, ct);
        if (processing is not null)
            await processing.DeleteForNoteAsync(owner, noteId, ct);

        if (attempts.Count > 0)
        {
            db.NoteAsrAckReceipts.RemoveRange(receipts);
            db.NoteAsrSegmentEvidence.RemoveRange(evidence);
            db.NoteAsrLeases.RemoveRange(leases);
            db.NoteAsrAttempts.RemoveRange(attempts);
        }
        if (batchJobs.Count > 0)
            db.NoteAsrBatchJobs.RemoveRange(batchJobs);

        if (logIdSet.Length > 0)
        {
            var jobs = await db.AiAsrJobs
                .Where(row => row.OwnerId == owner && logIdSet.Contains(row.LogId))
                .ToListAsync(ct);
            var observations = await db.AiAsrTransportObservations
                .Where(row => logIdSet.Contains(row.LogId))
                .ToListAsync(ct);
            db.AiAsrTransportObservations.RemoveRange(observations);
            db.AiAsrJobs.RemoveRange(jobs);
            db.AiCallImages.RemoveRange(images);
            db.AiCallAudio.RemoveRange(audio);
            db.AiRequestLogs.RemoveRange(logs);
        }

        // Ledger deletion is intentionally before the final database save: if the
        // material volume cannot be cleared, both the note and its call records remain
        // in a retryable state instead of claiming a purge that did not happen.
        var materialResult = await materials.PurgeDeletedAsync(owner, noteId, expectedDeletedAt, ct);
        audit.Enqueue(new CreateAuditLogRequest(
            owner,
            AuditActorType.System,
            "recycle_bin.note.purge",
            "native_note",
            noteId.ToString("D"),
            "recycle-bin",
            AuditResult.Success,
            null,
            null,
            null,
            new Dictionary<string, string>
            {
                ["materialVersions"] = materialResult.MaterialVersions.ToString(),
                ["asrTasks"] = asrTaskIds.Count.ToString(),
                ["derivedTasks"] = derivedTaskIds.Length.ToString(),
                ["asrAttempts"] = attempts.Count.ToString(),
                ["asrLeases"] = leases.Count.ToString(),
                ["asrAckReceipts"] = receipts.Count.ToString(),
                ["asrSegmentEvidence"] = evidence.Count.ToString(),
                ["asrBatchJobs"] = batchJobs.Count.ToString(),
                ["aiCalls"] = logIdSet.Length.ToString(),
                ["aiImages"] = images.Count.ToString(),
                ["aiAudio"] = audio.Count.ToString()
            },
            null,
            null));
        await db.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Native note permanently deleted: note {NoteId}, materials {MaterialVersions}, asr {AsrTasks}, derived {DerivedTasks}",
            noteId,
            materialResult.MaterialVersions,
            asrTaskIds.Count,
            derivedTaskIds.Length);

        return new NativeNotePurgeResult(
            materialResult.MaterialVersions,
            asrTaskIds.Count,
            derivedTaskIds.Length,
            logIdSet.Length,
            images.Count,
            audio.Count);
    }

    private static long LockKey(Guid owner, Guid noteId)
    {
        var ownerBytes = owner.ToByteArray();
        var noteBytes = noteId.ToByteArray();
        return BitConverter.ToInt64(ownerBytes, 0)
            ^ BitConverter.ToInt64(ownerBytes, 8)
            ^ BitConverter.ToInt64(noteBytes, 0)
            ^ BitConverter.ToInt64(noteBytes, 8);
    }

    private static void EnsureExpectedDeletedAt(DateTimeOffset actual, DateTimeOffset? expected)
    {
        if (expected.HasValue && expected.Value != actual)
            throw new DomainException(40901, "笔记状态已变化，请刷新回收站后重试");
    }
}
