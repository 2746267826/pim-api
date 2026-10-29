using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;

namespace Pim.Infrastructure.Notes;

public sealed class NoteAsrService(NoteAsrTaskStore tasks, NoteMaterialUploadStore materials,
    PimDbContext db, ICurrentUserService user, AiAsrPresetRegistry presets, IConfiguration configuration,
    ISecretProtector secrets)
{
    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");
    private bool BackgroundProcessingAvailable => !(bool.TryParse(configuration["DisableHangfire"], out var disabled) && disabled)
        && !string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection"));
    private NoteAsrTaskDto Describe(NoteAsrState state) => NoteAsrTaskStore.Describe(state) with
    { BackgroundProcessingAvailable = BackgroundProcessingAvailable };
    public async Task<NoteAsrTaskDto> StartAsync(StartNoteAsrRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.ExpectedUserId != owner) throw new DomainException(40301, "账号已改变，请重新核对材料和目标");
        if (request.TaskId == Guid.Empty || request.VersionId == Guid.Empty) throw new DomainException(40001, "任务或版本标识无效");
        // A lost HTTP response replays an already persisted intent even after configuration changes.
        try
        {
            var old = await tasks.ReadAsync(owner, request.TaskId, ct);
            var hash = NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (old.Fingerprint != hash) throw new DomainException(40901, "任务标识已绑定另一份输入，原任务保留");
            return Describe(old);
        }
        catch (DomainException error) when (error.ErrorCode == 40401) { }
        await ValidateDestinationAsync(request.Consent, request.Language, ct);
        var version = await materials.GetAsync(owner, request.VersionId, ct);
        return Describe(await tasks.CreateAsync(owner, request, version, NoteAsrPlan.Build(version), ct));
    }
    public async Task<IReadOnlyList<NoteAsrTaskDto>> ListAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var result = new List<NoteAsrTaskDto>();
        foreach (var (_, id) in tasks.Enumerate(owner)) result.Add(Describe(await tasks.ReadAsync(owner, id, ct)));
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }
    public async Task<NoteAsrSourcesDto> SourcesAsync(int offset, int limit, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await materials.ListAsrSourcesAsync(owner, offset, limit, ct);
    }
    public async Task<NoteAsrDestinationsDto> DestinationsAsync(CancellationToken ct)
    {
        await RequireActiveOwnerAsync(ct);
        if (!await db.AiModelRuntime.AsNoTracking().AnyAsync(x => x.Enabled, ct))
            return new(0, []);
        var profiles = await db.AiModelProfiles.AsNoTracking()
            .Where(x => x.Enabled && x.Capabilities == 4)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var destinations = profiles.Select(TryDescribe).Where(x => x is not null).Cast<NoteAsrDestinationDto>().ToArray();
        return new(destinations.Length, destinations);
    }
    public async Task<NoteAsrTaskDto> GetAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return Describe(await ReconcileAsync(owner, id, ct));
    }
    public async Task<NoteAsrSegmentsDto> SegmentsAsync(Guid id, int offset, int limit, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (offset < 0 || limit is < 1 or > 20) throw new DomainException(40001, "分片分页范围无效，单页最多20片");
        var state = await ReconcileAsync(owner, id, ct);
        var page = state.Steps.Skip(offset).Take(limit).ToArray();
        var logIds = page.Select(x => x.Current.LogId).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var logs = await db.AiRequestLogs.AsNoTracking().Where(x => x.UserId == owner && logIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ResponseText }).ToDictionaryAsync(x => x.Id, ct);
        return new(state.Steps.Count, offset, page.Select(x => new NoteAsrSegmentDto(x.Index, x.RecordingId, x.RecordingSegment,
            x.ChunkIndex, x.FileIndex, x.Path, x.Sha256, x.SourceOffset, x.RecordingOffset, x.TimelineOffset, x.Samples,
            x.Current.Status, x.Current.LogId is { } log && logs.TryGetValue(log, out var row) ? row.ResponseText : null,
            x.Attempts.Select(a => new NoteAsrAttemptDto(a.RequestId, a.LogId, a.Status, a.ErrorMessage)).ToArray())).ToArray());
    }
    public async Task<NoteAsrTaskDto> StopAsync(Guid id, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return Describe(await tasks.UpdateAsync(owner, id, state => state.StopRequested = true, ct));
    }
    public async Task<NoteAsrTaskDto> RetryAsync(Guid id, RetryNoteAsrRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.ExpectedUserId != owner) throw new DomainException(40301, "账号已改变，请重新核对");
        if (request.RetryId == Guid.Empty || request.Consent?.Confirmed != true || !request.AcknowledgePossibleDuplicateCharge ||
            request.SegmentIndexes is not { Count: > 0 and <= 5_000 } || request.SegmentIndexes.Distinct().Count() != request.SegmentIndexes.Count)
            throw new DomainException(40001, "请选择待恢复分片，核对原目标，并确认重试可能重复计费");
        var before = await ReconcileAsync(owner, id, ct);
        var fingerprint = NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (before.Retries.TryGetValue(request.RetryId, out var previous))
        {
            if (previous != fingerprint) throw new DomainException(40901, "恢复标识已绑定另一组分片，原恢复记录保留");
            return Describe(before);
        }
        if (request.Consent != before.Request.Consent) throw new DomainException(40901, "重试目标与固定任务不一致，请新建任务");
        await ValidateDestinationAsync(request.Consent, before.Request.Language, ct);
        _ = await materials.GetAsync(owner, before.Request.VersionId, ct);
        return Describe(await tasks.UpdateAsync(owner, id, state =>
        {
            if (state.Retries.TryGetValue(request.RetryId, out var existing))
            {
                if (existing != fingerprint) throw new DomainException(40901, "恢复标识已绑定另一组分片，原恢复记录保留");
                return;
            }
            if (state.Revision != request.ExpectedRevision) throw new DomainException(40901, "任务进度已变化，请刷新后核对");
            if (state.Retries.Count >= 50) throw new DomainException(41301, "本任务恢复次数已达50，原结果保留");
            foreach (var index in request.SegmentIndexes)
                if (index < 0 || index >= state.Steps.Count || state.Steps[index].Current.Status is "Succeeded" or "Running" ||
                    state.Steps[index].Attempts.Count >= 10) throw new DomainException(40901, "不能重跑成功/执行中分片，或已达单片10次上限");
            foreach (var index in request.SegmentIndexes)
                if (state.Steps[index].Current.Status != "Queued") state.Steps[index].Attempts.Add(new());
            state.Retries.Add(request.RetryId, fingerprint); state.StopRequested = false;
        }, ct));
    }
    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = Owner;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用，不能创建或查看ASR任务");
        return owner;
    }
    private async Task<NoteAsrState> ReconcileAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var state = await tasks.ReadAsync(owner, id, ct);
        var waiting = state.Steps.Where(x => x.Current.Status is "Unknown" or "Running").ToArray();
        if (waiting.Length == 0) return state;
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var requests = waiting.Select(x => x.Current.RequestId).ToArray();
        var receipts = await db.AiRequestLogs.AsNoTracking().Where(x => x.UserId == owner &&
            x.Purpose == "ai.asr-call" && x.SourceObjectId == id.ToString() && requests.Contains(x.ClientRequestId!.Value) && x.Status != "running").ToListAsync(ct);
        if (receipts.Count == 0) return state;
        return await tasks.UpdateAsync(owner, id, current =>
        {
            foreach (var step in current.Steps.Where(x => x.Current.Status is "Unknown" or "Running"))
            {
                var receipt = receipts.SingleOrDefault(x => x.ClientRequestId == step.Current.RequestId);
                if (receipt is null) continue;
                var result = AiCallAudit.Result(receipt, true);
                step.Current.LogId = result.LogId; step.Current.Status = result.Status.ToString(); step.Current.ErrorMessage = result.ErrorMessage;
            }
        }, ct);
    }
    private async Task ValidateDestinationAsync(AiAsrDestinationConsent? consent, string language, CancellationToken ct)
    {
        if (consent?.Confirmed != true) throw new DomainException(40001, "请明确同意将全部录音分片发送至所示ASR目标");
        var model = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == consent.ModelId, ct);
        if (model is null || !model.Enabled || model.Capabilities != 4 ||
            !await db.AiModelRuntime.AnyAsync(x => x.Enabled, ct)) throw new DomainException(40901, "ASR配置或SDK当前不可用");
        var preset = presets.Resolve(model.AsrPresetId); var descriptor = preset.Descriptor;
        preset.ValidateModel(model.Model);
        if (!CredentialAvailable(model.ApiKeySecret)) throw new DomainException(40901, "ASR凭据当前不可用，请管理员重新保存");
        if (consent.ModelRevision != model.Revision || consent.PresetId != descriptor.Id || consent.PresetVersion != descriptor.Version ||
            consent.Endpoint != AiAsrPresetRegistry.OperationEndpoint(preset.NormalizeEndpoint(model.BaseUrl), descriptor))
            throw new DomainException(40901, "ASR目标配置已变化，请刷新并重新确认");
        if (language is null || !descriptor.Languages.Contains(language) || !descriptor.Formats.Contains("wav") ||
            descriptor.MaxAudioBytes < 44 + NoteAsrPlan.SamplesPerCall * 2) throw new DomainException(40001, "当前预设不支持本次语言或一分钟WAV分片");
    }
    private NoteAsrDestinationDto? TryDescribe(AiModelProfileEntity profile)
    {
        try
        {
            var preset = presets.Resolve(profile.AsrPresetId); var descriptor = preset.Descriptor;
            preset.ValidateModel(profile.Model);
            if (!CredentialAvailable(profile.ApiKeySecret) || !descriptor.Formats.Contains("wav") ||
                descriptor.MaxAudioBytes < 44 + NoteAsrPlan.SamplesPerCall * 2) return null;
            var endpoint = AiAsrPresetRegistry.OperationEndpoint(preset.NormalizeEndpoint(profile.BaseUrl), descriptor);
            return new(profile.Id, profile.Revision, profile.Name, profile.Model, endpoint, descriptor.Id,
                descriptor.Version, descriptor.Languages, BackgroundProcessingAvailable);
        }
        catch (Exception error) when (error is DomainException or UriFormatException or ArgumentException)
        { return null; }
    }
    private bool CredentialAvailable(string protectedValue)
    {
        try { return !string.IsNullOrEmpty(secrets.Unprotect(protectedValue)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return false; }
    }
}
