using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.NoteTaskSuggestions.DTOs;
using Pim.Module.NoteTaskSuggestions.Entities;
using Pim.Module.NoteTaskSuggestions.Jobs;

namespace Pim.Module.NoteTaskSuggestions.Services;

internal sealed class NoteTaskSuggestionService(
    PimDbContext db,
    ICurrentUserService currentUser,
    INoteTaskSuggestionSourceReader sourceReader,
    CalendarService calendar,
    TimeProvider timeProvider,
    ILogger<NoteTaskSuggestionService> logger,
    IBackgroundJobClient? jobs = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid Owner => currentUser.UserId ?? throw new DomainException(40101, "请先登录");

    public async Task<NoteTaskSuggestionExtractionDto> StartExtractionAsync(
        StartNoteTaskSuggestionExtractionRequest request, CancellationToken ct)
    {
        var owner = Owner;
        ValidateStartRequest(request, owner);
        var fingerprint = Fingerprint(request);
        var existing = await FindExtractionByRequestAsync(owner, request.RequestId, ct);
        if (existing is not null)
            return ReplayExtraction(existing, fingerprint);
        if (jobs is null)
            throw new DomainException(50301,
                "任务建议后台处理未配置；本次未创建提取记录，请稍后重试");

        var source = await sourceReader.ReadAsync(request.ArtifactId, request.VersionId, ct);
        if (source.NoteId != request.NoteId || source.ArtifactId != request.ArtifactId ||
            source.ArtifactVersionId != request.VersionId)
            throw new DomainException(40901, "任务建议来源与固定笔记版本不一致");
        if (!HashEquals(source.ContentSha256, request.ExpectedContentSha256))
            throw new DomainException(40901, "笔记版本内容已变化，请刷新后重新核对来源");
        if (source.ContentMarkdown.Length > NoteTaskSuggestionPrompt.MaxSourceCharacters)
            throw new DomainException(41301,
                $"笔记内容 {source.ContentMarkdown.Length} 字，超过任务提取当前单次上限 " +
                $"{NoteTaskSuggestionPrompt.MaxSourceCharacters} 字；不会静默截断");

        await ValidateTextProfileAsync(owner, request.Consent, ct);
        var now = timeProvider.GetUtcNow();
        var created = false;
        NoteTaskSuggestionExtractionEntity extraction;
        try
        {
            var outcome = await AdvisoryLock.RunAsync(db, Key(request.RequestId), async () =>
            {
                var before = await FindExtractionByRequestAsync(owner, request.RequestId, ct);
                if (before is not null) return (ReplayEntity(before, fingerprint), false);

                var entity = new NoteTaskSuggestionExtractionEntity
                {
                    UserId = owner,
                    RequestId = request.RequestId,
                    RequestFingerprint = fingerprint,
                    NoteId = source.NoteId,
                    ArtifactId = source.ArtifactId,
                    ArtifactVersionId = source.ArtifactVersionId,
                    SourceContentSha256 = NormalizeHash(source.ContentSha256),
                    SourceInputSha256 = NormalizeHash(source.InputSha256),
                    SourceManifestSha256 = NormalizeHash(source.ManifestSha256),
                    SourceGenerationTaskId = source.GenerationTaskId,
                    SourceContentMarkdown = source.ContentMarkdown,
                    ModelProfileId = request.Consent.ModelId,
                    ModelRevision = request.Consent.ModelRevision,
                    ModelEndpoint = request.Consent.Endpoint.Trim(),
                    PromptVersion = NoteTaskSuggestionPrompt.Version,
                    Status = NoteTaskSuggestionExtractionStatuses.Queued,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Set<NoteTaskSuggestionExtractionEntity>().Add(entity);
                await db.SaveChangesAsync(ct);
                return (entity, true);
            }, ct);
            extraction = outcome.Item1;
            created = outcome.Item2;
        }
        catch (DbUpdateException)
        {
            existing = await FindExtractionByRequestAsync(owner, request.RequestId, ct);
            if (existing is null) throw;
            extraction = ReplayEntity(existing, fingerprint);
            created = false;
        }

        if (created)
        {
            try
            {
                jobs.Enqueue<NoteTaskSuggestionExtractionJob>(
                    job => job.RunAsync(owner, extraction.Id, CancellationToken.None));
            }
            catch (Exception error)
            {
                // The durable Queued row is the source of truth; the recurring dispatcher will retry.
                logger.LogWarning(error,
                    "Task suggestion extraction {ExtractionId} stayed queued because Hangfire enqueue failed",
                    extraction.Id);
            }
        }

        return Map(extraction);
    }

    public async Task<NoteTaskSuggestionExtractionDto> GetExtractionAsync(Guid id, CancellationToken ct)
    {
        var owner = Owner;
        var entity = await db.Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ct)
            ?? throw new DomainException(40401, "任务建议提取记录不存在");
        return Map(entity);
    }

    public async Task<NoteTaskSuggestionListDto> ListAsync(
        Guid? noteId, NoteTaskSuggestionStatusDto? status, CancellationToken ct)
    {
        var owner = Owner;
        var query = db.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.UserId == owner);
        if (noteId is { } note)
            query = query.Where(x => x.NoteId == note);
        if (status is { } wanted)
            query = query.Where(x => x.Status == wanted.ToString());

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Ordinal)
            .Take(200)
            .ToListAsync(ct);
        return new(items.Count, items.Select(Map).ToArray());
    }

    public async Task<NoteTaskSuggestionDto> GetAsync(Guid id, CancellationToken ct)
    {
        var owner = Owner;
        var entity = await db.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ct)
            ?? throw new DomainException(40401, "任务建议不存在");
        return Map(entity);
    }

    public async Task<NoteTaskSuggestionDto> UpdateAsync(
        Guid id, UpdateNoteTaskSuggestionRequest request, CancellationToken ct)
    {
        var owner = Owner;
        var noteId = await RequireNoteIdAsync(owner, id, ct);
        return await AdvisoryLock.RunAsync(db, NoteKey(noteId), async () =>
        {
            var entity = await RequireSuggestionAsync(owner, id, ct);
            RequireDraft(entity);
            ValidateEditableFields(request, entity.Kind);
            if (entity.Revision != request.ExpectedRevision)
                throw new DomainException(40901, "任务建议已被修改，请刷新后重试");

            await ValidateReferencesAsync(owner, entity.Kind, request.CalendarId, request.TaskBookId, ct);
            entity.Title = request.Title.Trim();
            entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            entity.Priority = request.Priority;
            if (entity.Kind == NoteTaskSuggestionKinds.Task)
            {
                entity.Due = request.Due?.ToUniversalTime();
                entity.StartAt = null;
                entity.EndAt = null;
                entity.TimeZoneId = null;
            }
            else
            {
                entity.Due = null;
                entity.StartAt = request.Start?.ToUniversalTime();
                entity.EndAt = request.End?.ToUniversalTime();
                entity.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId)
                    ? null : request.TimeZoneId.Trim();
            }
            entity.TaskBookId = request.TaskBookId;
            entity.CalendarId = request.CalendarId;
            entity.Revision = Guid.NewGuid();
            entity.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Map(entity);
        }, ct);
    }

    public async Task<NoteTaskSuggestionDto> DismissAsync(
        Guid id, DismissNoteTaskSuggestionRequest request, CancellationToken ct)
    {
        var owner = Owner;
        var noteId = await RequireNoteIdAsync(owner, id, ct);
        return await AdvisoryLock.RunAsync(db, NoteKey(noteId), async () =>
        {
            var entity = await RequireSuggestionAsync(owner, id, ct);
            if (entity.Status == NoteTaskSuggestionStatuses.Dismissed)
                return Map(entity);
            RequireDraft(entity);
            if (entity.Revision != request.ExpectedRevision)
                throw new DomainException(40901, "任务建议已被修改，请刷新后重试");

            var now = timeProvider.GetUtcNow();
            entity.Status = NoteTaskSuggestionStatuses.Dismissed;
            entity.DismissedAt = now;
            entity.UpdatedAt = now;
            entity.Revision = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
            return Map(entity);
        }, ct);
    }

    public async Task<NoteTaskSuggestionDto> ConfirmAsync(
        Guid id, ConfirmNoteTaskSuggestionRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty)
            throw new DomainException(40001, "确认请求标识不能为空");

        var owner = Owner;
        var noteId = await RequireNoteIdAsync(owner, id, ct);
        return await ConfirmCoreAsync(owner, noteId, id, request, ct);
    }

    private async Task<NoteTaskSuggestionDto> ConfirmCoreAsync(
        Guid owner, Guid noteId, Guid id, ConfirmNoteTaskSuggestionRequest request, CancellationToken ct)
    {
        try
        {
            return await AdvisoryLock.RunAsync(db, NoteKey(noteId), async () =>
            {
                var entity = await RequireSuggestionAsync(owner, id, ct);

                if (entity.Status == NoteTaskSuggestionStatuses.Confirmed)
                {
                    if (entity.ConfirmRequestId == request.RequestId &&
                        await OriginalObjectExistsAsync(owner, entity, ct))
                    {
                        return Map(entity);
                    }
                    throw new DomainException(40901, "任务建议已经确认；原对象标识不可替换");
                }
                RequireDraft(entity);
                if (entity.Revision != request.ExpectedRevision)
                    throw new DomainException(40901, "任务建议已被修改，请刷新后重试");
                if (await db.Set<NoteTaskSuggestionEntity>().AnyAsync(x =>
                        x.UserId == owner && x.ConfirmRequestId == request.RequestId && x.Id != id, ct))
                    throw new DomainException(40901, "确认请求标识已绑定另一条任务建议，原结果保留");

                if (entity.Kind == NoteTaskSuggestionKinds.Event)
                    ValidateEventRange(entity);
                var fingerprint = ItemFingerprint(entity);
                var duplicate = await FindConfirmedDuplicateAsync(owner, entity, fingerprint, ct);
                if (duplicate is not null)
                    throw DuplicateSuggestion(duplicate);

                if (entity.Kind == NoteTaskSuggestionKinds.Task)
                {
                    var task = await calendar.CreateTaskAsync(new CreateTaskRequest(
                        entity.CalendarId,
                        entity.Title,
                        entity.Description,
                        entity.Priority,
                        null,
                        null,
                        entity.Due,
                        null,
                        null,
                        null,
                        entity.TaskBookId,
                        null), ct);

                    var storedTask = await db.Set<TaskEntity>()
                        .SingleAsync(x => x.Id == task.Id && x.UserId == owner, ct);
                    storedTask.Source = "note-task-suggestion";
                    storedTask.UpdatedAt = timeProvider.GetUtcNow();
                    entity.TaskId = task.Id;
                }
                else
                {
                    var created = await calendar.CreateEventAsync(new CreateEventRequest(
                        entity.CalendarId ?? Guid.Empty,
                        entity.Title,
                        entity.Description,
                        null,
                        entity.StartAt!.Value,
                        entity.EndAt!.Value,
                        null,
                        IsAllDay: false,
                        TimeZoneId: entity.TimeZoneId), ct);

                    var storedEvent = await db.Set<EventEntity>()
                        .Include(x => x.Calendar)
                        .SingleAsync(x => x.Id == created.Id && x.Calendar.UserId == owner, ct);
                    storedEvent.Source = "note-suggestion";
                    storedEvent.UpdatedAt = timeProvider.GetUtcNow();
                    entity.EventId = created.Id;
                    entity.CalendarId = created.CalendarId;
                }

                var now = timeProvider.GetUtcNow();
                entity.Status = NoteTaskSuggestionStatuses.Confirmed;
                entity.ConfirmRequestId = request.RequestId;
                entity.ItemFingerprint = fingerprint;
                entity.ConfirmedAt = now;
                entity.UpdatedAt = now;
                entity.Revision = Guid.NewGuid();
                await db.SaveChangesAsync(ct);
                return Map(entity);
            }, ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.Set<NoteTaskSuggestionEntity>().AsNoTracking().AnyAsync(x =>
                    x.UserId == owner && x.ConfirmRequestId == request.RequestId && x.Id != id, ct))
                throw new DomainException(40901, "确认请求标识已绑定另一条任务建议，原结果保留");
            var stored = await db.Set<NoteTaskSuggestionEntity>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ct);
            if (stored is { Status: NoteTaskSuggestionStatuses.Confirmed } &&
                stored.ConfirmRequestId == request.RequestId)
                return Map(stored);
            if (stored is not null)
            {
                var duplicate = await FindConfirmedDuplicateAsync(
                    owner, stored, ItemFingerprint(stored), ct);
                if (duplicate is not null && duplicate.Id != id)
                    throw DuplicateSuggestion(duplicate);
            }
            throw;
        }
    }

    internal static NoteTaskSuggestionDto Map(NoteTaskSuggestionEntity entity) =>
        new(
            entity.Id,
            entity.ExtractionId,
            entity.Ordinal,
            entity.NoteId,
            entity.ArtifactId,
            entity.ArtifactVersionId,
            entity.SourceContentSha256,
            entity.SourceInputSha256,
            entity.SourceManifestSha256,
            entity.SourceGenerationTaskId,
            entity.Title,
            Enum.Parse<NoteTaskSuggestionKindDto>(entity.Kind),
            entity.Description,
            entity.Priority,
            entity.Due,
            entity.StartAt,
            entity.EndAt,
            entity.TimeZoneId,
            entity.TaskBookId,
            entity.CalendarId,
            Enum.Parse<NoteTaskSuggestionStatusDto>(entity.Status),
            entity.Revision,
            entity.TaskId,
            entity.EventId,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.ConfirmedAt,
            entity.DismissedAt);

    internal static NoteTaskSuggestionExtractionDto Map(NoteTaskSuggestionExtractionEntity entity) =>
        new(
            entity.Id,
            entity.RequestId,
            entity.NoteId,
            entity.ArtifactId,
            entity.ArtifactVersionId,
            entity.SourceContentSha256,
            entity.SourceInputSha256,
            entity.SourceManifestSha256,
            entity.SourceGenerationTaskId,
            entity.ModelProfileId,
            entity.ModelRevision,
            entity.ModelEndpoint,
            entity.PromptVersion,
            Enum.Parse<NoteTaskSuggestionExtractionStatusDto>(entity.Status),
            entity.Revision,
            entity.AttemptCount,
            entity.SuggestionCount,
            entity.AiCallLogId,
            entity.ErrorCode,
            entity.ErrorMessage,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.StartedAt,
            entity.FinishedAt);

    private async Task<NoteTaskSuggestionEntity> RequireSuggestionAsync(
        Guid owner, Guid id, CancellationToken ct) =>
        await db.Set<NoteTaskSuggestionEntity>()
            .Include(x => x.Extraction)
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, ct)
        ?? throw new DomainException(40401, "任务建议不存在");

    private async Task<Guid> RequireNoteIdAsync(Guid owner, Guid id, CancellationToken ct) =>
        await db.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.Id == id && x.UserId == owner)
            .Select(x => (Guid?)x.NoteId)
            .SingleOrDefaultAsync(ct)
        ?? throw new DomainException(40401, "任务建议不存在");

    private async Task<NoteTaskSuggestionExtractionEntity?> FindExtractionByRequestAsync(
        Guid owner, Guid requestId, CancellationToken ct) =>
        await db.Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == owner && x.RequestId == requestId, ct);

    private static NoteTaskSuggestionExtractionDto ReplayExtraction(
        NoteTaskSuggestionExtractionEntity entity, string fingerprint) =>
        Map(ReplayEntity(entity, fingerprint));

    private static NoteTaskSuggestionExtractionEntity ReplayEntity(
        NoteTaskSuggestionExtractionEntity entity, string fingerprint)
    {
        if (!string.Equals(entity.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new DomainException(40901, "同一提取请求标识已绑定另一份来源或模型，原记录保留");
        return entity;
    }

    private static void ValidateStartRequest(
        StartNoteTaskSuggestionExtractionRequest request, Guid owner)
    {
        if (request.RequestId == Guid.Empty || request.NoteId == Guid.Empty ||
            request.ArtifactId == Guid.Empty || request.VersionId == Guid.Empty)
            throw new DomainException(40001, "提取请求或笔记版本标识无效");
        if (request.ExpectedUserId != owner)
            throw new DomainException(40301, "账号已改变，请重新核对来源和计费目标");
        if (!request.AcknowledgePossibleCharge || request.Consent?.Confirmed != true)
            throw new DomainException(40001, "请核对固定笔记来源、文本模型与目标地址，并明确确认可能产生费用");
        if (request.Consent.ModelId == Guid.Empty || request.Consent.ModelRevision == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Consent.Endpoint))
            throw new DomainException(40001, "文本模型同意信息不完整");
        if (!IsSha256(request.ExpectedContentSha256))
            throw new DomainException(40001, "预期来源 SHA-256 必须是 64 位十六进制");
    }

    private async Task ValidateTextProfileAsync(
        Guid owner, Pim.Core.Notes.NoteDerivedGenerationConsent consent, CancellationToken ct)
    {
        var runtime = await db.AiModelRuntime.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == AiModelRuntimeEntity.SingletonId, ct);
        var profile = await db.AiModelProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == consent.ModelId, ct);
        if (runtime is not { Enabled: true } || runtime.TextModelId != consent.ModelId ||
            profile is null || !profile.Enabled ||
            (profile.Capabilities & AiModelCapabilityFlags.Text) == 0 ||
            string.IsNullOrWhiteSpace(profile.ApiKeySecret))
            throw new DomainException(40901, "当前文本模型不可用，请先在 AI 设置中启用并保存凭据");
        if (profile.Revision != consent.ModelRevision ||
            !string.Equals(profile.BaseUrl, consent.Endpoint, StringComparison.Ordinal))
            throw new DomainException(40901, "文本模型或目标地址已变化，请刷新后重新确认费用与来源");
    }

    private async Task ValidateTaskReferencesAsync(
        Guid owner, Guid? calendarId, Guid? taskBookId, CancellationToken ct)
    {
        if (calendarId is { } calendarValue &&
            !await db.Set<CalendarEntity>().AnyAsync(x => x.Id == calendarValue && x.UserId == owner, ct))
            throw new DomainException(40001, "目标日历不存在");
        if (taskBookId is { } taskBookValue &&
            !await db.Set<TaskBookEntity>().AnyAsync(x => x.Id == taskBookValue && x.UserId == owner, ct))
            throw new DomainException(40001, "目标任务本不存在");
    }

    private async Task ValidateReferencesAsync(
        Guid owner, string kind, Guid? calendarId, Guid? taskBookId, CancellationToken ct)
    {
        if (kind == NoteTaskSuggestionKinds.Task)
        {
            await ValidateTaskReferencesAsync(owner, calendarId, taskBookId, ct);
            return;
        }

        if (taskBookId is not null)
            throw new DomainException(40001, "日程建议不能指定任务本");
        if (calendarId is not { } targetCalendar || targetCalendar == Guid.Empty)
            return;

        if (!await db.Set<CalendarEntity>().AsNoTracking()
                .AnyAsync(x => x.Id == targetCalendar && x.UserId == owner, ct))
            throw new DomainException(40001, "目标日历不存在");
    }

    private static void ValidateEditableFields(
        UpdateNoteTaskSuggestionRequest request, string kind)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 255)
            throw new DomainException(40001, "建议标题必须为 1-255 字");
        if (request.Description is { Length: > NoteTaskSuggestionPrompt.MaxDescriptionCharacters })
            throw new DomainException(40001,
                $"建议描述不能超过 {NoteTaskSuggestionPrompt.MaxDescriptionCharacters} 字");
        if (request.Priority is not (1 or 5 or 9))
            throw new DomainException(40001, "建议优先级只能是 1（低）、5（中）或 9（高）");
        if (request.TimeZoneId?.Trim().Length > 100)
            throw new DomainException(40001, "日程时区不能超过 100 字");

        if (kind == NoteTaskSuggestionKinds.Task)
        {
            if (request.Start.HasValue || request.End.HasValue)
                throw new DomainException(40001, "任务建议不能携带 start/end；需要日程时间时请改用事件建议");
            return;
        }

        if (request.Due.HasValue)
            throw new DomainException(40001, "日程建议必须用 start/end 表示时间，不能使用 due");
        if (request.Start.HasValue != request.End.HasValue)
            throw new DomainException(40001, "日程建议 start 和 end 必须同时提供或同时为空");
        if (request.Start is { } start && request.End is { } end && end <= start)
            throw new DomainException(40001, "日程结束时间必须晚于开始时间");
    }

    private static void ValidateEventRange(NoteTaskSuggestionEntity entity)
    {
        if (entity.StartAt is not { } start || entity.EndAt is not { } end)
            throw new DomainException(40001, "日程建议缺少开始或结束时间，请先补全后再确认");
        if (end <= start)
            throw new DomainException(40001, "日程结束时间必须晚于开始时间");
    }

    private async Task<bool> OriginalObjectExistsAsync(
        Guid owner, NoteTaskSuggestionEntity entity, CancellationToken ct)
    {
        if (entity.Kind == NoteTaskSuggestionKinds.Event)
            return entity.EventId is { } eventId &&
                await db.Set<EventEntity>().AsNoTracking()
                    .AnyAsync(x => x.Id == eventId && x.Calendar.UserId == owner, ct);
        return entity.TaskId is { } taskId &&
            await db.Set<TaskEntity>().AsNoTracking()
                .AnyAsync(x => x.Id == taskId && x.UserId == owner, ct);
    }

    private Task<NoteTaskSuggestionEntity?> FindConfirmedDuplicateAsync(
        Guid owner, NoteTaskSuggestionEntity entity, string fingerprint, CancellationToken ct) =>
        db.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.UserId == owner && x.NoteId == entity.NoteId &&
                x.Id != entity.Id && x.Status == NoteTaskSuggestionStatuses.Confirmed &&
                x.ItemFingerprint == fingerprint)
            .OrderBy(x => x.ConfirmedAt)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

    private static DomainException DuplicateSuggestion(NoteTaskSuggestionEntity original)
    {
        var target = original.Kind == NoteTaskSuggestionKinds.Event
            ? original.EventId
            : original.TaskId;
        return new DomainException(40901,
            "同一笔记中已确认相同的事项目标；未重复创建，原对象为 " +
            (target?.ToString() ?? original.Id.ToString()));
    }

    internal static string ItemFingerprint(NoteTaskSuggestionEntity entity)
    {
        var payload = string.Join('|',
            "v2",
            FingerprintPart(entity.UserId.ToString("D")),
            FingerprintPart(entity.NoteId.ToString("D")),
            FingerprintPart(entity.ArtifactId.ToString("D")),
            FingerprintPart(entity.ArtifactVersionId.ToString("D")),
            FingerprintPart(entity.SourceContentSha256),
            FingerprintPart(entity.SourceInputSha256),
            FingerprintPart(entity.SourceManifestSha256),
            FingerprintPart(entity.SourceGenerationTaskId?.ToString("D")),
            FingerprintPart(entity.Kind),
            FingerprintPart(entity.Title.Trim()),
            FingerprintPart(entity.Description?.Trim()),
            entity.Priority.ToString(CultureInfo.InvariantCulture),
            UnixSeconds(entity.Due),
            UnixSeconds(entity.StartAt),
            UnixSeconds(entity.EndAt),
            FingerprintPart(entity.TimeZoneId?.Trim()),
            FingerprintPart(entity.TaskBookId?.ToString("D")),
            FingerprintPart(entity.CalendarId?.ToString("D")));
        return Hash(payload);
    }

    private static string UnixSeconds(DateTimeOffset? value) =>
        value?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string FingerprintPart(string? value) =>
        value is null
            ? "-"
            : Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();

    private static void RequireDraft(NoteTaskSuggestionEntity entity)
    {
        if (entity.Status != NoteTaskSuggestionStatuses.Draft)
            throw new DomainException(40901, "只有待处理建议可以执行此操作");
    }

    private static string Fingerprint(StartNoteTaskSuggestionExtractionRequest request) =>
        Hash(JsonSerializer.Serialize(new
        {
            request.RequestId,
            request.ExpectedUserId,
            request.NoteId,
            request.ArtifactId,
            request.VersionId,
            expectedContentSha256 = NormalizeHash(request.ExpectedContentSha256),
            request.Consent,
            request.AcknowledgePossibleCharge,
            promptVersion = NoteTaskSuggestionPrompt.Version
        }, Json));

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string NormalizeHash(string value) => value.Trim().ToLowerInvariant();

    private static bool HashEquals(string actual, string expected) =>
        string.Equals(NormalizeHash(actual), NormalizeHash(expected), StringComparison.Ordinal);

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    internal static long NoteKey(Guid noteId) => Key(noteId);

    internal static long Key(Guid id) =>
        BitConverter.ToInt64(id.ToByteArray(), 0) ^ 0x4E4F544553554747L;
}
