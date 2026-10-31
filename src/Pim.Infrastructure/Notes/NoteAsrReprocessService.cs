using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Starts a new immutable task from a terminal source task. It deliberately never reconciles or
/// mutates the source, and delegates all route/attempt persistence to the existing start pipeline.
/// </summary>
public sealed class NoteAsrReprocessService(
    PimDbContext db,
    NoteAsrTaskStore tasks,
    NoteMaterialUploadStore materials,
    NoteAsrService noteAsr,
    ICurrentUserService user)
{
    private const int MaxReasonTextLength = 200;
    private const int MaxHistoryLimit = 50;
    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");

    public async Task<NoteAsrReprocessResultDto> ReprocessAsync(Guid sourceTaskId,
        ReprocessNoteAsrRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request is null || sourceTaskId == Guid.Empty || request.ClientTaskId == Guid.Empty ||
            request.ClientTaskId == sourceTaskId || request.ExpectedUserId == Guid.Empty)
            throw new DomainException(40001, "重处理任务或用户标识无效");
        if (request.ExpectedUserId != owner)
            throw new DomainException(40301, "账号已改变，请重新核对原任务");
        if (request.Route is null)
            throw new DomainException(40001, "请提交当前路由配置 revision 和确认信息");

        var reason = NormalizeReason(request.Reason);
        var existing = await TryReadAsync(owner, request.ClientTaskId, ct);
        if (existing is not null)
        {
            await EnsureReplayMatchesAsync(existing, sourceTaskId, reason, request, ct);
            var replayed = await noteAsr.StartAsync(existing.Request, ct);
            return new(replayed, sourceTaskId, reason);
        }

        var source = await tasks.ReadAsync(owner, sourceTaskId, ct);
        if (source.Revision != request.ExpectedRevision)
            throw new DomainException(40901, "原任务进度已变化，请刷新后重新确认");
        var sourceView = NoteAsrTaskStore.Describe(source);
        if (sourceView.Phase is not ("Succeeded" or "NeedsAttention" or "Stopped"))
            throw new DomainException(40901, "只能重处理已完成、失败或已停止的任务；运行中/排队任务不能复制");
        var version = await materials.GetAsync(owner, source.Request.VersionId, ct);
        if (version.Upload.NoteId != source.NoteId ||
            !string.Equals(NoteMaterialManifestValidator.Hash(version.InputJson), source.InputSha256, StringComparison.Ordinal) ||
            !string.Equals(NoteMaterialManifestValidator.Hash(version.ManifestJson), source.ManifestSha256, StringComparison.Ordinal))
            throw new DomainException(40901, "原任务绑定的版本、音频或材料清单已变化，不能重处理");

        var route = Clean(request.Route.Route) ?? Clean(source.Route) ??
            Clean(source.Request.Route?.Route) ?? NoteAsrRoute.CloudAsr;
        if (!NoteAsrRoute.Known(route))
            throw new DomainException(40001, "重处理执行位置无效");
        if (!request.Route.Confirmed)
            throw new DomainException(40001, "请明确确认本次重处理的执行位置");

        var executionMode = Clean(request.Route.ExecutionMode) ??
            Clean(source.ExecutionMode) ?? Clean(source.Request.Route?.ExecutionMode) ??
            NoteAsrExecutionMode.Realtime;
        if (!NoteAsrExecutionMode.Known(executionMode))
            throw new DomainException(40001, "重处理执行方式无效");

        var cloud = NoteAsrRoute.IsCloud(route);
        if (!cloud && request.Consent is not null)
            throw new DomainException(40001, "本机重处理不涉及云端外发，不需要云端确认");
        var consent = cloud ? request.Consent : null;
        if (cloud && consent?.Confirmed != true)
            throw new DomainException(40001, "云端重处理必须重新确认目标与音频外发");

        var start = new StartNoteAsrRequest(
            request.ClientTaskId,
            owner,
            source.Request.VersionId,
            consent,
            source.Request.Language,
            new NoteAsrRouteConsent(
                route,
                request.Route.ExpectedConfigRevision,
                request.Route.ExpectedConsentVersion ?? "",
                true,
                executionMode,
                request.Route.BatchScopeConfirmed),
            sourceTaskId,
            reason);

        var task = await noteAsr.StartAsync(start, ct);
        var created = await tasks.ReadAsync(owner, request.ClientTaskId, ct);
        if (created.ParentTaskId != sourceTaskId || created.Request.ParentTaskId != sourceTaskId ||
            !string.Equals(created.ReprocessReason, reason, StringComparison.Ordinal) ||
            !string.Equals(created.Request.ReprocessReason, reason, StringComparison.Ordinal))
            throw new InvalidDataException("重处理任务未持久化来源和原因");
        return new(task, sourceTaskId, reason);
    }

    public async Task<NoteAsrReprocessHistoryDto> HistoryAsync(Guid parentTaskId,
        Guid expectedUserId, int offset, int limit, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (parentTaskId == Guid.Empty || expectedUserId == Guid.Empty)
            throw new DomainException(40001, "历史查询标识无效");
        if (expectedUserId != owner) throw new DomainException(40301, "账号已改变，请重新核对");
        if (offset < 0 || limit is < 1 or > MaxHistoryLimit)
            throw new DomainException(40001, "历史分页范围无效，单页最多50条");
        _ = await tasks.ReadAsync(owner, parentTaskId, ct);

        var children = new List<NoteAsrState>();
        foreach (var (_, taskId) in tasks.Enumerate(owner))
        {
            var child = await tasks.ReadAsync(owner, taskId, ct);
            if (child.Request.ParentTaskId == parentTaskId || child.ParentTaskId == parentTaskId)
                children.Add(child);
        }

        var ordered = children.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Request.TaskId).ToArray();
        var items = ordered.Skip(offset).Take(limit).Select(state =>
        {
            var task = NoteAsrTaskStore.Describe(state);
            return new NoteAsrReprocessHistoryItemDto(
                parentTaskId,
                state.Request.TaskId,
                state.Revision,
                state.Request.VersionId,
                state.ReprocessReason ?? state.Request.ReprocessReason ?? "",
                task.Phase,
                state.Route ?? state.Request.Route?.Route ?? NoteAsrRoute.CloudAsr,
                state.ExecutionMode ?? state.Request.Route?.ExecutionMode ?? NoteAsrExecutionMode.Realtime,
                state.CreatedAt,
                state.UpdatedAt);
        }).ToArray();
        return new(parentTaskId, ordered.Length, offset, limit, items);
    }

    private async Task EnsureReplayMatchesAsync(NoteAsrState existing, Guid sourceTaskId,
        string reason, ReprocessNoteAsrRequest request, CancellationToken ct)
    {
        if (existing.ParentTaskId != sourceTaskId || existing.Request.ParentTaskId != sourceTaskId ||
            !string.Equals(existing.ReprocessReason, reason, StringComparison.Ordinal) ||
            !string.Equals(existing.Request.ReprocessReason, reason, StringComparison.Ordinal))
            throw new DomainException(40901, "客户端任务标识已绑定另一来源或原因，原任务保留");

        var route = existing.Request.Route
            ?? throw new DomainException(40901, "已有重处理任务缺少冻结路由，不能重放");
        if (Clean(request.Route.Route) is { } requestedRoute &&
            !string.Equals(route.Route, requestedRoute, StringComparison.Ordinal))
            throw new DomainException(40901, "客户端任务标识已绑定另一执行位置，原任务保留");
        if (request.Route.Route is null && !NoteAsrRoute.IsCloud(route.Route))
        {
            var source = await tasks.ReadAsync(existing.Owner, sourceTaskId, ct);
            if (source.Route is not null &&
                !string.Equals(route.Route, source.Route, StringComparison.Ordinal))
                throw new DomainException(40901, "客户端任务标识已绑定另一执行位置，原任务保留");
        }
        if (route.ConfigRevision != request.Route.ExpectedConfigRevision ||
            !string.Equals(route.ConsentVersion, request.Route.ExpectedConsentVersion ?? "", StringComparison.Ordinal))
            throw new DomainException(40901, "路由配置 revision 与已冻结任务不一致，原任务保留");
        if (request.Route.ExecutionMode is { } requestedMode &&
            !string.Equals(route.ExecutionMode, requestedMode.Trim(), StringComparison.Ordinal))
            throw new DomainException(40901, "客户端任务标识已绑定另一执行方式，原任务保留");
        if (request.Route.ExecutionMode is null &&
            !string.Equals(route.ExecutionMode,
                existing.ExecutionMode ?? NoteAsrExecutionMode.Realtime,
                StringComparison.Ordinal))
            throw new DomainException(40901, "客户端任务标识已绑定另一执行方式，原任务保留");
        if (!request.Route.Confirmed || route.Confirmed != request.Route.Confirmed ||
            route.BatchScopeConfirmed != request.Route.BatchScopeConfirmed)
            throw new DomainException(40901, "路由确认范围与已冻结任务不一致，原任务保留");
        if (NoteAsrRoute.IsCloud(route.Route))
        {
            if (request.Consent is null || request.Consent != existing.Request.Consent)
                throw new DomainException(40901, "客户端任务标识已绑定另一目标同意，原任务保留");
        }
        else if (request.Consent is not null)
        {
            throw new DomainException(40001, "本机重处理不涉及云端外发，不需要云端确认");
        }
    }

    private static string NormalizeReason(NoteAsrReprocessReason? reason)
    {
        if (reason is null) throw new DomainException(40001, "请选择重处理原因");
        var kind = reason.Kind?.Trim().ToLowerInvariant();
        if (!NoteAsrReprocessReasonKind.Known(kind))
            throw new DomainException(40001, "重处理原因只能是 manual 或 quality");
        if (reason.Text is { Length: > MaxReasonTextLength })
            throw new DomainException(40001, "重处理原因说明最多200个字符");

        var visible = new StringBuilder(reason.Text?.Length ?? 0);
        var lastWasSpace = false;
        foreach (var ch in reason.Text ?? "")
        {
            var value = char.IsControl(ch) ? ' ' : ch;
            if (char.IsWhiteSpace(value))
            {
                if (!lastWasSpace && visible.Length > 0) visible.Append(' ');
                lastWasSpace = true;
                continue;
            }
            visible.Append(value);
            lastWasSpace = false;
        }
        var text = visible.ToString().Trim();
        if (text.Length > MaxReasonTextLength)
            throw new DomainException(40001, "重处理原因说明最多200个字符");
        return text.Length == 0 ? kind! : $"{kind}: {text}";
    }

    private async Task<NoteAsrState?> TryReadAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        try { return await tasks.ReadAsync(owner, taskId, ct); }
        catch (DomainException error) when (error.ErrorCode == 40401) { return null; }
    }

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = Owner;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用，不能创建或查看ASR任务");
        return owner;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
