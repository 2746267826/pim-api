using System.Globalization;
using System.Text.Json;
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
    private const string NoteDomain = "note";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PurgeLease = TimeSpan.FromMinutes(5);
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
        => PurgeCoreAsync(owner, noteId, expectedDeletedAt, ct);

    private async Task<NativeNotePurgeResult> PurgeCoreAsync(Guid owner, Guid noteId,
        DateTimeOffset? expectedDeletedAt,
        CancellationToken ct)
    {
        var deleted = await materials.GetDeletedAsync(owner, noteId, ct);
        var generationAt = deleted?.DeletedAt ?? expectedDeletedAt;
        var existing = generationAt.HasValue
            ? await FindIntentAsync(owner, noteId, generationAt.Value, ct)
            : await FindLatestIntentAsync(owner, noteId, ct);

        if (deleted is null)
        {
            if (existing is null)
            {
                if (expectedDeletedAt.HasValue)
                    return new NativeNotePurgeResult(0, 0, 0, 0, 0, 0);
                try
                {
                    _ = await materials.GetCurrentAsync(owner, noteId, ct);
                }
                catch (DomainException error) when (error.ErrorCode == 40401)
                {
                    throw new DomainException(40401, "笔记不存在");
                }

                throw new DomainException(40901, "请先将笔记移入回收站，再永久删除");
            }

            if (existing.Phase == RecycleBinPurgePhases.Completed)
            {
                await materials.CompletePurgeAsync(owner, noteId, ct: ct);
                return NoWorkPurgeResult;
            }
        }
        else
        {
            EnsureExpectedDeletedAt(deleted.DeletedAt, expectedDeletedAt);
            if (existing is { Phase: RecycleBinPurgePhases.Completed })
            {
                await materials.CompletePurgeAsync(owner, noteId, deleted.DeletedAt, ct);
                return Result(existing);
            }
        }

        var anchor = deleted?.DeletedAt ?? expectedDeletedAt ?? existing?.ExpectedDeletedAt;
        if (!anchor.HasValue)
            throw new DomainException(40401, "笔记不存在");

        var intent = await ClaimIntentAsync(
            owner,
            noteId,
            anchor.Value,
            deleted?.Title ?? existing?.Title ?? "笔记",
            ct);
        if (intent.Phase == RecycleBinPurgePhases.Completed)
        {
            await materials.CompletePurgeAsync(owner, noteId, ct: ct);
            return NoWorkPurgeResult;
        }

        try
        {
            var effectiveExpected = deleted?.DeletedAt ?? expectedDeletedAt ?? intent.ExpectedDeletedAt;
            if (intent.Phase == RecycleBinPurgePhases.DbCommitted)
            {
                await materials.CompletePurgeAsync(owner, noteId, effectiveExpected, ct);
                await MarkCompletedAsync(intent, ct);
                return NoWorkPurgeResult;
            }

            var purgeStarted = deleted is not null
                && await materials.IsPurgeStartedAsync(owner, noteId, ct);
            var recovery = deleted is null || purgeStarted;

            // Discovery is best effort on retry: the first attempt may already have deleted
            // task files. The durable id sets in the intent are the cleanup handles that keep
            // the PostgreSQL rows discoverable in that case.
            var discoveredAsrTaskIds = await asr.ListForNoteAsync(owner, noteId, ct);
            var discoveredDerivedTasks = await derived.ListTasksAsync(owner, noteId, ct);
            var asrTaskIds = UnionIds(discoveredAsrTaskIds, ReadIds(intent.AsrTaskIdsJson));
            var derivedTaskIds = UnionIds(
                discoveredDerivedTasks.Select(task => task.Request.TaskId),
                ReadIds(intent.DerivedTaskIdsJson));
            var asrTaskIdSet = asrTaskIds.ToArray();
            var attempts = await db.NoteAsrAttempts
                .Where(row => row.OwnerId == owner
                    && (row.NoteId == noteId || asrTaskIdSet.Contains(row.TaskId)))
                .ToListAsync(ct);
            if (!recovery && attempts.Any(row => row.State == "Leased"
                    || (row.State == "Queued" && !row.StopRequested)))
                throw new DomainException(40901, "笔记的转写执行快照仍在进行，请等待任务结束或停止后重试永久删除");
            var attemptIds = attempts.Select(row => row.Id).ToArray();
            var now = DateTimeOffset.UtcNow;
            var leases = attemptIds.Length == 0
                ? new List<NoteAsrLeaseEntity>()
                : await db.NoteAsrLeases
                    .Where(row => row.OwnerId == owner && attemptIds.Contains(row.AttemptId))
                    .ToListAsync(ct);
            if (!recovery && leases.Any(row => row.Status == "Active" && row.ExpiresAt > now))
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
            if (!recovery && batchJobs.Any(row => !TerminalBatchStatuses.Contains(row.Status)))
                throw new DomainException(40901, "笔记的 Batch 任务仍在进行，请等待任务结束或取消后重试永久删除");

            if (!recovery)
            {
                foreach (var taskId in discoveredAsrTaskIds)
                {
                    if (NoteAsrTaskStore.Active(await asr.ReadAsync(owner, taskId, ct)))
                        throw new DomainException(40901, "笔记的转写任务正在执行，请等待任务结束或停止后重试永久删除");
                }
                if (discoveredDerivedTasks.Any(NoteDerivedStore.IsActive))
                    throw new DomainException(40901, "笔记的派生任务正在执行，请等待任务结束或停止后重试永久删除");
            }

            var sourceIds = asrTaskIds.Concat(derivedTaskIds)
                .Select(id => id.ToString("D"))
                .ToHashSet(StringComparer.Ordinal);

            var logIds = ReadIds(intent.AiLogIdsJson);
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

            // Persist inventory and every cleanup handle before any destructive step. If the
            // PostgreSQL commit below rolls back, the retry still knows which task/log rows
            // belonged to the note even though their files have already been deleted.
            var materialVersionCount = await materials.CountVersionsForNoteAsync(owner, noteId, ct);
            intent.AsrTaskIdsJson = WriteIds(asrTaskIds);
            intent.DerivedTaskIdsJson = WriteIds(derivedTaskIds);
            intent.AiLogIdsJson = WriteIds(logIds);
            intent.MaterialVersions = Math.Max(
                intent.MaterialVersions,
                materialVersionCount);
            intent.AsrTasks = Math.Max(intent.AsrTasks, asrTaskIds.Count);
            intent.DerivedTasks = Math.Max(intent.DerivedTasks, derivedTaskIds.Count);
            intent.AiCalls = Math.Max(intent.AiCalls, logIds.Count);
            intent.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            if (deleted is not null)
                await materials.MarkPurgeStartedAsync(owner, noteId, effectiveExpected, ct);
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

            // The material ledger keeps the tombstone until PostgreSQL commits below.
            var materialResult = await materials.PurgeDeletedAsync(
                owner, noteId, effectiveExpected, allowMissingTombstone: true, ct);
            var finalCounts = new NativeNotePurgeCounts(
                Math.Max(intent.MaterialVersions, materialResult.MaterialVersions),
                Math.Max(intent.AsrTasks, asrTaskIds.Count),
                Math.Max(intent.DerivedTasks, derivedTaskIds.Count),
                Math.Max(intent.AiCalls, logIdSet.Length),
                Math.Max(intent.AiImages, images.Count),
                Math.Max(intent.AiAudio, audio.Count));

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
                    ["materialVersions"] = finalCounts.MaterialVersions.ToString(),
                    ["asrTasks"] = finalCounts.AsrTasks.ToString(),
                    ["derivedTasks"] = finalCounts.DerivedTasks.ToString(),
                    ["asrAttempts"] = attempts.Count.ToString(),
                    ["asrLeases"] = leases.Count.ToString(),
                    ["asrAckReceipts"] = receipts.Count.ToString(),
                    ["asrSegmentEvidence"] = evidence.Count.ToString(),
                    ["asrBatchJobs"] = batchJobs.Count.ToString(),
                    ["aiCalls"] = finalCounts.AiCalls.ToString(),
                    ["aiImages"] = finalCounts.AiImages.ToString(),
                    ["aiAudio"] = finalCounts.AiAudio.ToString()
                },
                null,
                null));

            intent.Phase = RecycleBinPurgePhases.DbCommitted;
            intent.MaterialVersions = finalCounts.MaterialVersions;
            intent.AsrTasks = finalCounts.AsrTasks;
            intent.DerivedTasks = finalCounts.DerivedTasks;
            intent.AiCalls = finalCounts.AiCalls;
            intent.AiImages = finalCounts.AiImages;
            intent.AiAudio = finalCounts.AiAudio;
            intent.LeaseToken = null;
            intent.LeaseExpiresAt = null;
            intent.LastError = null;
            intent.UpdatedAt = DateTimeOffset.UtcNow;
            // One SaveChanges is the PostgreSQL commit: deleted rows, audit row and the
            // durable "database committed" phase either all land or all roll back.
            await db.SaveChangesAsync(ct);

            await materials.CompletePurgeAsync(owner, noteId, effectiveExpected, ct);
            await MarkCompletedAsync(intent, ct);

            logger?.LogInformation(
                "Native note permanently deleted: note {NoteId}, materials {MaterialVersions}, asr {AsrTasks}, derived {DerivedTasks}",
                noteId,
                finalCounts.MaterialVersions,
                finalCounts.AsrTasks,
                finalCounts.DerivedTasks);
            return Result(intent);
        }
        catch (Exception error)
        {
            // The failed attempt may have left tracked deletes in memory. A real request
            // would dispose them; clear them here so releasing the lease cannot commit a
            // partial purge.
            db.ChangeTracker.Clear();
            await ReleaseIntentAsync(intent.Id, error.Message, CancellationToken.None);
            throw;
        }
    }

    private Task<RecycleBinPurgeIntentEntity?> FindIntentAsync(
        Guid owner, Guid noteId, DateTimeOffset deletionAt, CancellationToken ct)
    {
        var generation = GenerationKey(deletionAt);
        return db.RecycleBinPurgeIntents.SingleOrDefaultAsync(row =>
            row.UserId == owner
            && row.Domain == NoteDomain
            && row.ItemId == noteId
            && row.Generation == generation, ct);
    }

    private Task<RecycleBinPurgeIntentEntity?> FindLatestIntentAsync(
        Guid owner, Guid noteId, CancellationToken ct) =>
        db.RecycleBinPurgeIntents
            .Where(row => row.UserId == owner
                && row.Domain == NoteDomain
                && row.ItemId == noteId)
            .OrderByDescending(row => row.CreatedAt)
            .FirstOrDefaultAsync(ct);

    private Task<RecycleBinPurgeIntentEntity> ClaimIntentAsync(
        Guid owner, Guid noteId, DateTimeOffset deletionAt, string title,
        CancellationToken ct)
    {
        var generation = GenerationKey(deletionAt);
        return AdvisoryLock.RunAsync(
            db,
            LockKey(owner, noteId),
            async () =>
            {
                var now = DateTimeOffset.UtcNow;
                var intent = await db.RecycleBinPurgeIntents.SingleOrDefaultAsync(row =>
                    row.UserId == owner
                    && row.Domain == NoteDomain
                    && row.ItemId == noteId
                    && row.Generation == generation, ct);
                if (intent is null)
                {
                    intent = new RecycleBinPurgeIntentEntity
                    {
                        UserId = owner,
                        Domain = NoteDomain,
                        ItemId = noteId,
                        Generation = generation,
                        ExpectedDeletedAt = deletionAt,
                        Title = title,
                        Phase = RecycleBinPurgePhases.Pending,
                        AttemptCount = 1,
                        LeaseToken = Guid.NewGuid(),
                        LeaseExpiresAt = now.Add(PurgeLease),
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    db.RecycleBinPurgeIntents.Add(intent);
                    await db.SaveChangesAsync(ct);
                    return intent;
                }

                if (intent.Phase == RecycleBinPurgePhases.Completed)
                    return intent;
                // A db_committed intent no longer owns destructive work: completion only
                // removes the ledger tombstone and marks the intent done. Let the retry take
                // the lease immediately instead of waiting for a crashed attempt to expire.
                if (intent.Phase != RecycleBinPurgePhases.DbCommitted
                    && intent.LeaseToken.HasValue
                    && intent.LeaseExpiresAt > now)
                    throw new DomainException(40901, "该笔记的永久清理正在进行，请稍后重试");

                if (intent.Phase != RecycleBinPurgePhases.DbCommitted)
                    intent.Phase = RecycleBinPurgePhases.Pending;
                intent.AttemptCount++;
                intent.LeaseToken = Guid.NewGuid();
                intent.LeaseExpiresAt = now.Add(PurgeLease);
                intent.UpdatedAt = now;
                intent.LastError = null;
                if (string.IsNullOrWhiteSpace(intent.Title)) intent.Title = title;
                intent.ExpectedDeletedAt ??= deletionAt;
                await db.SaveChangesAsync(ct);
                return intent;
            },
            ct);
    }

    private static HashSet<Guid> UnionIds(IEnumerable<Guid> first, IEnumerable<Guid> second)
    {
        var result = new HashSet<Guid>(first);
        result.UnionWith(second);
        return result;
    }

    private static HashSet<Guid> ReadIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json, Json) is { } ids
                ? [.. ids]
                : [];
        }
        catch (JsonException)
        {
            // Corrupt intent metadata must not make an otherwise recoverable purge
            // permanently undeletable. Current discovery still covers live files.
            return [];
        }
    }

    private static string WriteIds(IEnumerable<Guid> ids) =>
        JsonSerializer.Serialize(
            ids.Distinct().OrderBy(id => id).Select(id => id.ToString("D")).ToArray(),
            Json);

    private async Task MarkCompletedAsync(
        RecycleBinPurgeIntentEntity intent, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        intent.Phase = RecycleBinPurgePhases.Completed;
        intent.LeaseToken = null;
        intent.LeaseExpiresAt = null;
        intent.UpdatedAt = now;
        intent.CompletedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task ReleaseIntentAsync(
        Guid intentId, string error, CancellationToken ct)
    {
        try
        {
            var intent = await db.RecycleBinPurgeIntents
                .SingleOrDefaultAsync(row => row.Id == intentId, ct);
            if (intent is null || intent.Phase == RecycleBinPurgePhases.Completed)
                return;
            intent.LeaseToken = null;
            intent.LeaseExpiresAt = null;
            intent.LastError = error.Length > 2000 ? error[..2000] : error;
            intent.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception releaseError)
        {
            logger?.LogWarning(
                releaseError,
                "Unable to release native-note purge lease for intent {IntentId}",
                intentId);
        }
    }

    private static string GenerationKey(DateTimeOffset deletionAt) =>
        deletionAt.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture);

    private static NativeNotePurgeResult Result(RecycleBinPurgeIntentEntity intent) =>
        new(
            intent.MaterialVersions,
            intent.AsrTasks,
            intent.DerivedTasks,
            intent.AiCalls,
            intent.AiImages,
            intent.AiAudio);

    // A completed intent means the destructive work is already done. Reporting zero
    // work lets the unified service answer "already purged" without pretending this
    // request deleted anything again.
    private static readonly NativeNotePurgeResult NoWorkPurgeResult = new(0, 0, 0, 0, 0, 0);

    private sealed record NativeNotePurgeCounts(
        int MaterialVersions,
        int AsrTasks,
        int DerivedTasks,
        int AiCalls,
        int AiImages,
        int AiAudio);

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
