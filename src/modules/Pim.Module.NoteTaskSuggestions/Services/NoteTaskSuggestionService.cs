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

        if (created && jobs is not null)
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

    public Task<NoteTaskSuggestionDto> UpdateAsync(
        Guid id, UpdateNoteTaskSuggestionRequest request, CancellationToken ct)
    {
        ValidateEditableFields(request);
        return AdvisoryLock.RunAsync(db, Key(id), async () =>
        {
            var owner = Owner;
            var entity = await RequireSuggestionAsync(owner, id, ct);
            RequireDraft(entity);
            if (entity.Revision != request.ExpectedRevision)
                throw new DomainException(40901, "任务建议已被修改，请刷新后重试");

            await ValidateTaskReferencesAsync(owner, request.CalendarId, request.TaskBookId, ct);
            entity.Title = request.Title.Trim();
            entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            entity.Priority = request.Priority;
            entity.Due = request.Due?.ToUniversalTime();
            entity.TaskBookId = request.TaskBookId;
            entity.CalendarId = request.CalendarId;
            entity.Revision = Guid.NewGuid();
            entity.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Map(entity);
        }, ct);
    }

    public Task<NoteTaskSuggestionDto> DismissAsync(
        Guid id, DismissNoteTaskSuggestionRequest request, CancellationToken ct)
        => AdvisoryLock.RunAsync(db, Key(id), async () =>
        {
            var owner = Owner;
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

    public Task<NoteTaskSuggestionDto> ConfirmAsync(
        Guid id, ConfirmNoteTaskSuggestionRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty)
            throw new DomainException(40001, "确认请求标识不能为空");

        var owner = Owner;
        return ConfirmCoreAsync(owner, id, request, ct);
    }

    private async Task<NoteTaskSuggestionDto> ConfirmCoreAsync(
        Guid owner, Guid id, ConfirmNoteTaskSuggestionRequest request, CancellationToken ct)
    {
        try
        {
            return await AdvisoryLock.RunAsync(db, Key(id), async () =>
            {
                var entity = await RequireSuggestionAsync(owner, id, ct);

                if (entity.Status == NoteTaskSuggestionStatuses.Confirmed)
                {
                    if (entity.ConfirmRequestId == request.RequestId && entity.TaskId is { } existingTaskId &&
                        await db.Set<TaskEntity>().AnyAsync(x => x.Id == existingTaskId && x.UserId == owner, ct))
                    {
                        return Map(entity);
                    }
                    throw new DomainException(40901, "任务建议已经确认；原任务标识不可替换");
                }
                RequireDraft(entity);
                if (entity.Revision != request.ExpectedRevision)
                    throw new DomainException(40901, "任务建议已被修改，请刷新后重试");
                if (await db.Set<NoteTaskSuggestionEntity>().AnyAsync(x =>
                        x.UserId == owner && x.ConfirmRequestId == request.RequestId && x.Id != id, ct))
                    throw new DomainException(40901, "确认请求标识已绑定另一条任务建议，原结果保留");

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

                var now = timeProvider.GetUtcNow();
                entity.Status = NoteTaskSuggestionStatuses.Confirmed;
                entity.ConfirmRequestId = request.RequestId;
                entity.TaskId = task.Id;
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
            entity.Description,
            entity.Priority,
            entity.Due,
            entity.TaskBookId,
            entity.CalendarId,
            Enum.Parse<NoteTaskSuggestionStatusDto>(entity.Status),
            entity.Revision,
            entity.TaskId,
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

    private static void ValidateEditableFields(UpdateNoteTaskSuggestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 255)
            throw new DomainException(40001, "任务标题必须为 1-255 字");
        if (request.Description is { Length: > NoteTaskSuggestionPrompt.MaxDescriptionCharacters })
            throw new DomainException(40001,
                $"任务描述不能超过 {NoteTaskSuggestionPrompt.MaxDescriptionCharacters} 字");
        if (request.Priority is not (1 or 5 or 9))
            throw new DomainException(40001, "任务优先级只能是 1（低）、5（中）或 9（高）");
    }

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

    internal static long Key(Guid id) =>
        BitConverter.ToInt64(id.ToByteArray(), 0) ^ 0x4E4F544553554747L;
}
