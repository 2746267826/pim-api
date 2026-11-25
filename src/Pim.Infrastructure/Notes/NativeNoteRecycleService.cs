using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
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
    ILogger<NativeNoteRecycleService>? logger = null)
{
    private const string AsrSourceType = "native-note-asr-task";
    private const string DerivedSourceType = "native-note-derived-task";

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

        await materials.MarkPurgeStartedAsync(owner, noteId, expectedDeletedAt, ct);

        var derivedTaskIds = derivedTasks.Select(task => task.Request.TaskId).ToArray();
        var sourceIds = asrTaskIds.Concat(derivedTaskIds)
            .Select(id => id.ToString("D"))
            .ToHashSet(StringComparer.Ordinal);

        var logs = sourceIds.Count == 0
            ? new List<AiRequestLogEntity>()
            : await db.AiRequestLogs
                .Where(log => log.UserId == owner
                    && sourceIds.Contains(log.SourceObjectId)
                    && (log.SourceObjectType == AsrSourceType || log.SourceObjectType == DerivedSourceType))
                .ToListAsync(ct);
        var logIds = logs.Select(log => log.Id).ToArray();

        var images = logIds.Length == 0
            ? new List<AiCallImageEntity>()
            : await db.AiCallImages.Where(row => logIds.Contains(row.LogId)).ToListAsync(ct);
        var audio = logIds.Length == 0
            ? new List<AiCallAudioEntity>()
            : await db.AiCallAudio.Where(row => logIds.Contains(row.LogId)).ToListAsync(ct);

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

        if (logIds.Length > 0)
        {
            var jobs = await db.AiAsrJobs
                .Where(row => row.OwnerId == owner && logIds.Contains(row.LogId))
                .ToListAsync(ct);
            var observations = await db.AiAsrTransportObservations
                .Where(row => logIds.Contains(row.LogId))
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
                ["aiCalls"] = logIds.Length.ToString(),
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
            logIds.Length,
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
