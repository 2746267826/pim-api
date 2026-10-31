using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;

namespace Pim.Infrastructure.Notes;

public sealed class NoteAsrRoutingOptions
{
    public int NodeHeartbeatTimeoutSeconds { get; set; } = 90;
    public int MaxLeaseSeconds { get; set; } = 600;
    public int NodeScanLimit { get; set; } = 50;
}

public sealed record NoteAsrSegmentContent(byte[] Bytes, string Sha256, string SourceSha256, Guid OwnerId,
    Guid NodeId, Guid LeaseId, Guid AttemptId, Guid VersionId, long FencingToken, int SegmentIndex,
    int SourceFileIndex, long SourceOffsetSamples, long Samples, string ContentType = "audio/wav");

/// <summary>
/// Durable #516 phase-1 routing and pull-claim authority. It never executes a model or provider call.
/// </summary>
public sealed class NoteAsrRoutingService(PimDbContext db, ICurrentUserService user, NoteMaterialUploadStore materials,
    AiAsrPresetRegistry presets, NoteAsrTaskStore legacyTasks, INoteAsrEvidenceSink evidence,
    ISecretProtector secrets, IOptions<NoteAsrRoutingOptions> options)
{
    private Guid Owner => user.UserId ?? throw new DomainException(40101, "请先登录");
    private NoteAsrRoutingOptions Options => options.Value;

    public async Task<NoteAsrRouteConfigDto> GetConfigAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var config = await db.NoteAsrRouteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == owner, ct);
        return config is null ? new(Guid.Empty, NoteAsrRoute.Local, null, null, false, "", DateTimeOffset.MinValue, null)
            : Describe(config);
    }

    public async Task<NoteAsrRouteConfigDto> UpdateConfigAsync(UpdateNoteAsrRouteConfigRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var route = request.DefaultRoute is null ? NoteAsrRoute.Local : request.DefaultRoute.Trim();
        if (request.LocalModelId is { Length: > 200 } || request.LocalModelRevision is { Length: > 128 })
            throw new DomainException(40001, "本机模型标识或版本过长");
        if (!NoteAsrRoute.Known(route) || route == NoteAsrRoute.Local && string.IsNullOrWhiteSpace(request.LocalModelId) ||
            route != NoteAsrRoute.Local && request.CloudModelId is null)
            throw new DomainException(40001, "默认执行位置无效或未选择对应模型");
        if (request.ConsentVersion is { Length: > 64 }) throw new DomainException(40001, "同意版本过长");
        if (request.CloudModelId is { } cloudId) await ValidateCloudDestinationAsync(cloudId, ct);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var current = await db.NoteAsrRouteConfigs.SingleOrDefaultAsync(x => x.OwnerId == owner, ct);
        if ((current?.Revision ?? Guid.Empty) != request.ExpectedRevision)
            throw new DomainException(40901, "路由配置已变化，请刷新后重新确认");
        if (current is null)
        {
            current = new NoteAsrRouteConfigEntity { OwnerId = owner };
            db.NoteAsrRouteConfigs.Add(current);
        }
        current.Revision = Guid.NewGuid(); current.DefaultRoute = route;
        current.LocalModelId = string.IsNullOrWhiteSpace(request.LocalModelId) ? null : request.LocalModelId.Trim();
        current.LocalModelRevision = string.IsNullOrWhiteSpace(request.LocalModelRevision) ? null : request.LocalModelRevision.Trim();
        current.CloudModelId = request.CloudModelId;
        current.ConsentVersion = request.ConsentVersion ?? ""; current.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new DomainException(40901, "路由配置已被并发修改，请刷新后重试"); }
        catch (DbUpdateException) when (current is not null && db.Entry(current).State == EntityState.Added)
        { throw new DomainException(40901, "路由配置已被并发创建，请刷新后重试"); }
        if (tx is not null) await tx.CommitAsync(ct);
        return Describe(current);
    }

    public async Task<IReadOnlyList<NoteAsrNodeDto>> ListNodesAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var nodes = await db.NoteAsrNodes.AsNoTracking().Where(x => x.OwnerId == owner)
            .OrderBy(x => x.NodeId).Take(200).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return nodes.Select(x => Describe(x, now)).ToArray();
    }

    /// <summary>Only the current account may register, list, heartbeat or claim through its node identity.</summary>
    public async Task<NoteAsrNodeDto> RegisterNodeAsync(RegisterNoteAsrNodeRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.NodeId == Guid.Empty || string.IsNullOrWhiteSpace(request.NodeIdExternal) ||
            request.NodeIdExternal.Length > 128 || string.IsNullOrWhiteSpace(request.ModelId) ||
            request.ModelId.Length > 200 || request.ModelRevision is null || request.ModelRevision.Length > 128 ||
            !ValidCapabilities(request.Capabilities) || !ValidResourceSnapshot(request.ResourceSnapshotJson))
            throw new DomainException(40001, "节点标识、模型或能力清单无效");
        if (await db.NoteAsrNodes.AnyAsync(x => x.Id == request.NodeId && x.OwnerId != owner, ct))
            throw new DomainException(40301, "节点不属于当前账号");
        var node = await db.NoteAsrNodes.SingleOrDefaultAsync(x => x.Id == request.NodeId, ct);
        if (node is null)
        {
            node = new NoteAsrNodeEntity { Id = request.NodeId, OwnerId = owner };
            db.NoteAsrNodes.Add(node);
        }
        node.NodeId = request.NodeIdExternal.Trim(); node.Enabled = request.Enabled;
        node.ModelId = request.ModelId.Trim(); node.ModelRevision = request.ModelRevision.Trim();
        node.ModelVerified = request.ModelVerified; node.CapabilitiesJson = JsonSerializer.Serialize(request.Capabilities);
        node.RuntimeId = CleanOptional(request.RuntimeId, 128); node.RuntimeVersion = CleanOptional(request.RuntimeVersion, 128);
        node.ResourceSnapshotJson = request.ResourceSnapshotJson ?? "{}"; node.SampledAt = request.SampledAt;
        node.LastHeartbeatAt = DateTimeOffset.UtcNow; node.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Describe(node, DateTimeOffset.UtcNow);
    }

    public async Task<NoteAsrNodeDto> HeartbeatAsync(Guid nodeId, NoteAsrNodeHeartbeatRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var node = await OwnedNodeAsync(owner, nodeId, ct);
        if (request.NodeId != nodeId || string.IsNullOrWhiteSpace(request.ModelId) ||
            string.IsNullOrWhiteSpace(request.ModelRevision) || !ValidCapabilities(request.Capabilities) ||
            !ValidResourceSnapshot(request.ResourceSnapshotJson))
            throw new DomainException(40001, "心跳节点、模型或能力清单无效");
        node.ModelId = request.ModelId.Trim(); node.ModelRevision = request.ModelRevision.Trim();
        node.ModelVerified = request.ModelVerified; node.CapabilitiesJson = JsonSerializer.Serialize(request.Capabilities);
        node.RuntimeId = CleanOptional(request.RuntimeId, 128); node.RuntimeVersion = CleanOptional(request.RuntimeVersion, 128);
        node.ResourceSnapshotJson = request.ResourceSnapshotJson ?? "{}"; node.SampledAt = request.SampledAt;
        node.LastHeartbeatAt = DateTimeOffset.UtcNow; node.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Describe(node, DateTimeOffset.UtcNow);
    }

    public async Task<NoteAsrNodeDto> WithdrawNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var node = await OwnedNodeAsync(owner, nodeId, ct);
        var now = DateTimeOffset.UtcNow;
        node.Enabled = false;
        node.UpdatedAt = now;
        var leases = await db.NoteAsrLeases.AsNoTracking().Where(x => x.OwnerId == owner && x.NodeId == nodeId &&
            x.Status == "Active").OrderByDescending(x => x.FencingToken).Take(100)
            .Select(x => new { x.Id, x.AttemptId, x.LeaseToken, x.FencingToken }).ToListAsync(ct);
        foreach (var lease in leases)
        {
            var leaseAffected = await db.NoteAsrLeases
                .Where(x => x.Id == lease.Id && x.OwnerId == owner && x.AttemptId == lease.AttemptId &&
                    x.LeaseToken == lease.LeaseToken && x.FencingToken == lease.FencingToken && x.Status == "Active")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Expired")
                    .SetProperty(x => x.EndedAt, now), ct);
            if (leaseAffected == 0) continue;
            await db.NoteAsrAttempts
                .Where(x => x.Id == lease.AttemptId && x.OwnerId == owner && x.State == "Leased" &&
                    x.FencingToken == lease.FencingToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.State, "Unknown")
                    .SetProperty(x => x.LastErrorCode, "node_withdrawn")
                    .SetProperty(x => x.LastErrorMessage, "节点已撤回，未证明本机已停止推理；旧租约仅保留核对")
                    .SetProperty(x => x.UpdatedAt, now), ct);
        }
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return Describe(node, now);
    }

    public async Task<NoteAsrClaimDto> ClaimAsync(ClaimNoteAsrTaskRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 ||
            request.LeaseSeconds is < 15 or > 3600)
            throw new DomainException(40001, "领取标识或租约时长无效");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var node = await OwnedNodeAsync(owner, request.NodeId, ct);
        var now = DateTimeOffset.UtcNow;
        if (!NodeClaimable(node, now)) return new(null, "unavailable", null);
        // Replays must return the same live lease and fencing token, not mint a second lease.
        var replay = await TryReplayClaimAsync(owner, node.Id, request.IdempotencyKey, now, ct);
        if (replay is not null) return replay;
        var expired = await ExpireStaleNodeLeasesAsync(owner, node.Id, now, ct);
        var attempt = await NextLocalAttemptAsync(owner, node, now, ct);
        if (attempt is null)
        {
            await tx.CommitAsync(ct);
            return new(null, expired > 0 ? "lease_expired" : "empty", null);
        }
        var leaseSeconds = Math.Min(request.LeaseSeconds, Options.MaxLeaseSeconds);
        // Single-winner CAS: only one concurrent claimant can move Queued -> Leased and bump the fence.
        var won = await db.NoteAsrAttempts
            .Where(x => x.Id == attempt.Id && x.OwnerId == owner && x.State == "Queued" && !x.StopRequested)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, "Leased")
                .SetProperty(x => x.FencingToken, x => x.FencingToken + 1)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (won == 0)
        {
            // Another transaction won (possibly the identical idempotent request). Never mint a second lease.
            await tx.RollbackAsync(ct);
            return await TryReplayClaimAsync(owner, node.Id, request.IdempotencyKey, now, ct)
                ?? new(null, "contended", null);
        }
        var fencing = await db.NoteAsrAttempts.AsNoTracking()
            .Where(x => x.Id == attempt.Id && x.OwnerId == owner).Select(x => x.FencingToken).SingleAsync(ct);
        var newLease = new NoteAsrLeaseEntity { OwnerId = owner, AttemptId = attempt.Id, NodeId = node.Id,
            LeaseToken = Guid.NewGuid(), FencingToken = fencing, ExpiresAt = now.AddSeconds(leaseSeconds) };
        db.NoteAsrLeases.Add(newLease);
        db.NoteAsrAckReceipts.Add(new NoteAsrAckReceiptEntity { OwnerId = owner, AttemptId = attempt.Id,
            LeaseId = newLease.Id, FencingToken = fencing, Kind = "claim", Result = "claimed",
            ProviderHandle = request.IdempotencyKey });
        try
        {
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Partial unique indexes protect one Active lease per attempt/node and one claim per idempotency key.
            await tx.RollbackAsync(ct);
            return await TryReplayClaimAsync(owner, node.Id, request.IdempotencyKey, now, ct)
                ?? throw new DomainException(40901, "领取已被并发请求占用，请按同一幂等标识重试");
        }
        return await ClaimAsync(owner, newLease, ct);
    }

    private async Task<NoteAsrClaimDto?> TryReplayClaimAsync(Guid owner, Guid nodeId, string idempotencyKey,
        DateTimeOffset now, CancellationToken ct)
    {
        var replay = await db.NoteAsrAckReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == owner &&
            x.Kind == "claim" && x.ProviderHandle == idempotencyKey, ct);
        if (replay is null) return null;
        var oldLease = await db.NoteAsrLeases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == replay.LeaseId && x.OwnerId == owner, ct);
        if (oldLease is not null && oldLease.Status == "Active" && oldLease.ExpiresAt > now && oldLease.NodeId == nodeId)
            return await ClaimAsync(owner, oldLease, ct);
        return new(null, "replayed", null);
    }

    private async Task<NoteAsrClaimDto> ClaimAsync(Guid owner, NoteAsrLeaseEntity lease, CancellationToken ct)
    {
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == lease.AttemptId && x.OwnerId == owner, ct);
        var state = await legacyTasks.ReadAsync(owner, attempt.TaskId, ct);
        if (state.Request.VersionId != attempt.VersionId ||
            state.InputSha256 != attempt.InputSha256)
            throw new DomainException(40901, "attempt 固定材料与任务快照不一致，不能下发执行");
        if (state.Steps.Count != attempt.ExpectedSegments)
            throw new DomainException(40901, "attempt 分片覆盖与任务快照不一致，不能下发执行");
        var segments = state.Steps.Select(x => new NoteAsrSegmentInputDto(x.Index, x.RecordingId,
            x.RecordingSegment, x.ChunkIndex, x.FileIndex, x.Sha256, x.SourceOffset, x.Samples,
            $"/api/v1/native-notes/asr-routing/leases/{lease.Id:D}/segments/{x.Index}/content")).ToArray();
        var execution = new NoteAsrExecutionDto(attempt.Id, owner, attempt.TaskId, attempt.VersionId, attempt.NoteId,
            attempt.InputSha256, attempt.Route, attempt.Language, "", attempt.ConfigRevision, attempt.ConsentVersion,
            attempt.LocalModelId, attempt.LocalModelRevision, attempt.ExpectedSegments, segments, DateTimeOffset.UtcNow);
        return new(lease.Id, "claimed", await Describe(db, owner, lease, ct), execution);
    }

    public async Task<NoteAsrSegmentContent> ReadSegmentInputAsync(Guid leaseId, int segmentIndex,
        Guid leaseToken, long fencingToken, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var lease = await ValidLeaseAsync(owner, leaseId, leaseToken, fencingToken, ct);
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lease.AttemptId &&
            x.OwnerId == owner && x.State == "Leased" && x.FencingToken == fencingToken && !x.StopRequested, ct)
            ?? throw new DomainException(40901, "attempt 已不接受固定输入读取");
        var state = await legacyTasks.ReadAsync(owner, attempt.TaskId, ct);
        if (state.Request.VersionId != attempt.VersionId ||
            state.InputSha256 != attempt.InputSha256 ||
            state.Steps.Count != attempt.ExpectedSegments ||
            segmentIndex < 0 || segmentIndex >= state.Steps.Count)
            throw new DomainException(40901, "固定输入快照不匹配或分片不存在");
        var step = state.Steps[segmentIndex];
        var bytes = await materials.ReadAudioSliceAsync(owner, attempt.VersionId, step.FileIndex,
            step.SourceOffset, step.Samples, ct);
        var sliceSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(bytes, sliceSha, step.Sha256, owner, lease.NodeId, lease.Id, lease.AttemptId,
            attempt.VersionId, lease.FencingToken, segmentIndex, step.FileIndex, step.SourceOffset, step.Samples);
    }

    /// <summary>
    /// GET repairs a projection that failed after the ACK transaction had already committed. It reads
    /// only exact attempt/fence receipts and real evidence, so repair cannot replay an ACK or invent a result.
    /// </summary>
    internal async Task<NoteAsrState> RepairAcknowledgedTaskAsync(Guid owner, NoteAsrState state,
        CancellationToken ct)
    {
        // A GET repair is bounded independently: the caller's request token may be None and a stuck
        // checkpoint filesystem lock must not hold an already committed ACK hostage.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        ct = linked.Token;
        if (state.AttemptId is not { } attemptId) return state;
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId &&
            x.OwnerId == owner && x.TaskId == state.Request.TaskId, ct);
        if (attempt is null) return state;
        var receipts = await db.NoteAsrAckReceipts.AsNoTracking().Where(x => x.OwnerId == owner &&
                x.AttemptId == attemptId && x.FencingToken == attempt.FencingToken)
            .OrderBy(x => x.ReceivedAt).ToListAsync(ct);
        if (receipts.Count == 0) return state;
        var terminal = receipts.LastOrDefault(x => x.Kind is "complete" or "fail");
        var segments = receipts.Where(x => x.Kind == "segment")
            .GroupBy(x => x.SegmentIndex).ToDictionary(x => x.Key, x => x.Last());
        var proof = (await db.NoteAsrSegmentEvidence.AsNoTracking().Where(x => x.OwnerId == owner &&
                x.AttemptId == attemptId && x.FencingToken == attempt.FencingToken &&
                x.TaskId == state.Request.TaskId && x.VersionId == state.Request.VersionId)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct))
            .GroupBy(x => x.SegmentIndex).ToDictionary(x => x.Key, x => x.Last());
        var successful = segments.Where(x => x.Value.Result == "Succeeded" &&
                proof.TryGetValue(x.Key, out var row) && row.ResultText.Length > 0)
            .Select(x => x.Key).ToHashSet();
        var complete = terminal is { Kind: "complete", Result: "Succeeded" } &&
            attempt.State == "Succeeded" && successful.Count == attempt.ExpectedSegments &&
            Enumerable.Range(0, attempt.ExpectedSegments).All(successful.Contains);
        var stopped = terminal?.Kind == "fail" || attempt.State is "Failed" or "Unknown";
        if (state.Steps.All(x => x.Current.Status is "Succeeded" or "Failed") &&
            state.CompletionAcknowledged == complete && state.StopRequested == (stopped && !complete)) return state;
        return await legacyTasks.UpdateAsync(owner, state.Request.TaskId, current =>
        {
            if (current.AttemptId != attempt.Id) return;
            foreach (var (index, receipt) in segments)
            {
                if (index < 0 || index >= current.Steps.Count) continue;
                var step = current.Steps[index].Current;
                // ACK text is a claim until its exact attempt/fence evidence exists. Never mark Succeeded
                // from a receipt alone; a non-success terminal remains visible and is never overwritten.
                step.Status = receipt.Result == "Succeeded" && !proof.ContainsKey(index)
                    ? "Unknown" : receipt.Result;
                step.ErrorMessage = receipt.ErrorMessage;
                if (receipt.Result == "Succeeded" && proof.TryGetValue(index, out var row))
                {
                    step.ErrorMessage = null;
                    step.EvidenceId = row.Id;
                }
            }
            current.StopRequested = stopped && !complete;
            current.CompletionAcknowledged = complete;
        }, ct);
    }

    /// <summary>Stops one executable attempt; a worker sees the flag on renew and must acknowledge the stop.</summary>
    public async Task<NoteAsrAttemptDetailDto> StopAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var attempt = await db.NoteAsrAttempts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "转写 attempt 不存在");
        if (attempt.State is "Succeeded" or "Failed" or "Cancelled")
            return DescribeAttempt(attempt);
        var now = DateTimeOffset.UtcNow;
        var affected = await db.NoteAsrAttempts
            .Where(x => x.Id == attemptId && x.OwnerId == owner &&
                x.State != "Succeeded" && x.State != "Failed" && x.State != "Cancelled")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.StopRequested, true)
                .SetProperty(x => x.State, x => x.State == "Leased" ? "Leased" : "Unknown")
                .SetProperty(x => x.LastErrorCode, "stop_requested")
                .SetProperty(x => x.LastErrorMessage,
                    "已请求停止；执行端须通过续租读取停止标记并回执，未确认前不视为已停止")
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (affected == 0) throw new DomainException(40901, "attempt 已终态，停止请求未覆盖原结果");
        return DescribeAttempt(await db.NoteAsrAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == attemptId && x.OwnerId == owner, ct));
    }

    public async Task<NoteAsrLeaseDto> RenewLeaseAsync(Guid leaseId, NoteAsrLeaseHeartbeatRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var lease = await db.NoteAsrLeases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == leaseId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "租约不存在");
        if (lease.LeaseToken != request.LeaseToken || lease.FencingToken != request.FencingToken)
            throw new DomainException(40901, "租约、attempt 或 fence 不匹配");
        var now = DateTimeOffset.UtcNow;
        var newExpiry = now.AddSeconds(Options.NodeHeartbeatTimeoutSeconds);
        var renewed = await db.NoteAsrLeases
            .Where(x => x.Id == leaseId && x.OwnerId == owner && x.LeaseToken == request.LeaseToken &&
                x.FencingToken == request.FencingToken && x.Status == "Active" && x.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExpiresAt, newExpiry), ct);
        if (renewed == 0)
            throw new DomainException(40901, "租约已失效，不能续租或复活");
        var node = await OwnedNodeAsync(owner, lease.NodeId, ct);
        node.LastHeartbeatAt = now;
        await db.SaveChangesAsync(ct);
        var current = await db.NoteAsrLeases.AsNoTracking().SingleAsync(x => x.Id == leaseId && x.OwnerId == owner, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return await Describe(db, owner, current, ct);
    }

    /// <summary>Repeated identical acknowledgements are harmless but never complete or mutate twice.</summary>
    public async Task<NoteAsrAckDto> AcknowledgeAsync(Guid attemptId, NoteAsrAckRequest request, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (request.Kind is not ("segment" or "complete" or "fail") || request.Result is not ("Succeeded" or "Failed") ||
            request.SegmentIndex < 0 || request.ErrorCode?.Length > 64 || request.ErrorMessage?.Length > 2000 ||
            request.ProviderHandle?.Length > 256 || request.ResultText?.Length > 2_000_000 ||
            request.CoverageJson?.Length > 100_000 ||
            request.Kind == "complete" && request.Result != "Succeeded" || request.Kind == "fail" && request.Result != "Failed")
            throw new DomainException(40001, "回执类型、结果或分片无效");
        if (request.Kind == "segment" && request.Result == "Succeeded" &&
            (string.IsNullOrWhiteSpace(request.ProviderHandle) || string.IsNullOrWhiteSpace(request.ResultText)))
            throw new DomainException(40001, "成功分片必须同时提交受鉴权文本和 provider handle");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lease = await LockLeaseForAckAsync(owner, attemptId, request.LeaseId, ct)
            ?? throw new DomainException(40401, "租约不存在");
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId && x.OwnerId == owner, ct);
        // Identity is checked before duplicate handling: another lease/attempt/fence is never a replay.
        if (lease.LeaseToken != request.LeaseToken || lease.FencingToken != request.FencingToken ||
            attempt.FencingToken != request.FencingToken)
            throw new DomainException(40901, "租约、attempt 或 fence 不匹配，结果未写入");
        if (await IsExactReceiptAsync(owner, attemptId, lease.Id, request, ct))
        {
            await tx.CommitAsync(ct);
            await ApplyAckToTaskAsync(owner, attempt, request, ct);
            return new(lease.Id, "duplicate", true, lease.FencingToken);
        }
        if (await db.NoteAsrAckReceipts.AnyAsync(x => x.OwnerId == owner && x.AttemptId == attemptId &&
            x.LeaseId == lease.Id && x.FencingToken == request.FencingToken && x.Kind == request.Kind &&
            x.SegmentIndex == request.SegmentIndex, ct))
            throw new DomainException(40901, "同一分片/终态已有不同内容回执，未覆盖");
        var stoppedFail = attempt.StopRequested && request.Kind == "fail" && request.Result == "Failed";
        if (attempt.State != "Leased" || attempt.StopRequested && !stoppedFail)
            throw new DomainException(40901, "attempt 当前状态不接受回执");
        if (request.SegmentIndex >= attempt.ExpectedSegments)
            throw new DomainException(40901, "分片不属于该 attempt");
        var now = DateTimeOffset.UtcNow;
        if (lease.Status != "Active" || lease.ExpiresAt <= now)
            throw new DomainException(40901, "租约已失效或过期，回执不能覆盖新结果");
        if (request.Kind == "complete")
        {
            var succeeded = await db.NoteAsrAckReceipts.AsNoTracking().Where(x => x.OwnerId == owner &&
                x.AttemptId == attemptId && x.FencingToken == request.FencingToken && x.Kind == "segment" &&
                x.Result == "Succeeded" && x.ResultText != null && x.ProviderHandle != null)
                .Select(x => x.SegmentIndex).Distinct().ToListAsync(ct);
            var missing = Enumerable.Range(0, attempt.ExpectedSegments).Except(succeeded).Take(5).ToArray();
            if (missing.Length > 0)
                throw new DomainException(40901, $"尚缺分片 {string.Join(",", missing)} 的成功结果，不能完成；已有结果保留");
        }
        var receipt = new NoteAsrAckReceiptEntity { OwnerId = owner, AttemptId = attemptId,
            LeaseId = lease.Id, FencingToken = request.FencingToken, Kind = request.Kind, Result = request.Result,
            SegmentIndex = request.SegmentIndex, ProviderHandle = request.ProviderHandle,
            ResultText = request.ResultText, CoverageJson = request.CoverageJson,
            ErrorCode = request.ErrorCode, ErrorMessage = request.ErrorMessage, ReceivedAt = now };
        db.NoteAsrAckReceipts.Add(receipt);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            await tx.RollbackAsync(ct);
            db.Entry(receipt).State = EntityState.Detached;
            if (await IsExactReceiptAsync(owner, attemptId, lease.Id, request, ct))
            {
                await ApplyAckToTaskAsync(owner, attempt, request, ct);
                return new(lease.Id, "duplicate", true, lease.FencingToken);
            }
            throw new DomainException(40901, "同一分片/终态已有不同内容回执，未覆盖");
        }
        if (request.Kind == "segment" && request.Result == "Succeeded")
            _ = await evidence.WriteAsync(owner, attemptId, request.SegmentIndex, "local",
                request.ProviderHandle!, request.ResultText!, request.CoverageJson, false, ct);
        // CAS on the exact fencing token: a late/old acknowledgement can never mutate a newer attempt.
        var terminalState = request.Kind == "complete" ? "Succeeded" : request.Kind == "fail" ? "Failed" : "Leased";
        var successDelta = request.Kind == "segment" && request.Result == "Succeeded" ? 1 : 0;
        var failDelta = request.Kind == "segment" && request.Result == "Failed" ? 1 : 0;
        var affected = await db.NoteAsrAttempts
            .Where(x => x.Id == attemptId && x.OwnerId == owner && x.State == "Leased" &&
                (!x.StopRequested || request.Kind == "fail") &&
                x.FencingToken == request.FencingToken && db.NoteAsrLeases.Any(l => l.Id == lease.Id &&
                    l.OwnerId == owner && l.AttemptId == attemptId && l.LeaseToken == request.LeaseToken &&
                    l.FencingToken == request.FencingToken && l.Status == "Active" && l.ExpiresAt > now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CompletedSegments, x => x.CompletedSegments + successDelta)
                .SetProperty(x => x.FailedSegments, x => x.FailedSegments + failDelta)
                .SetProperty(x => x.State, x => request.Kind == "segment" ? x.State : terminalState)
                .SetProperty(x => x.LastErrorCode, request.ErrorCode)
                .SetProperty(x => x.LastErrorMessage, request.ErrorMessage)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (affected == 0)
        {
            await tx.RollbackAsync(ct);
            throw new DomainException(40901, "回执与当前 attempt/fence 不一致，结果未写入");
        }
        if (request.Kind is "complete" or "fail")
        {
            var leaseState = request.Result == "Succeeded" ? "Completed" : "Failed";
            var leaseAffected = await db.NoteAsrLeases
                .Where(x => x.Id == lease.Id && x.OwnerId == owner && x.Status == "Active" &&
                    x.LeaseToken == request.LeaseToken && x.FencingToken == request.FencingToken &&
                    x.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, leaseState)
                    .SetProperty(x => x.EndedAt, now), ct);
            if (leaseAffected == 0)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException(40901, "租约已被并发终结，回执未重复完成");
            }
        }
        await tx.CommitAsync(ct);
        await ApplyAckToTaskAsync(owner, attempt, request, ct);
        return new(lease.Id, "accepted", false, lease.FencingToken);
    }

    private async Task ApplyAckToTaskAsync(Guid owner, NoteAsrAttemptEntity attempt,
        NoteAsrAckRequest request, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await legacyTasks.UpdateAsync(owner, attempt.TaskId, state =>
        {
            // An exact replay from an older attempt may repair only its own task snapshot.
            if (state.AttemptId != attempt.Id) return;
            if (request.Kind == "segment" && request.SegmentIndex < state.Steps.Count)
            {
                var current = state.Steps[request.SegmentIndex].Current;
                current.Status = request.Result;
                current.ErrorMessage = request.ErrorMessage;
                if (request.Result == "Succeeded")
                    current.EvidenceId = db.NoteAsrSegmentEvidence.AsNoTracking()
                        .Where(x => x.OwnerId == owner && x.AttemptId == attempt.Id &&
                            x.FencingToken == request.FencingToken && x.SegmentIndex == request.SegmentIndex &&
                            x.ReceiptKind == "local")
                        .Select(x => (Guid?)x.Id).SingleOrDefault();
            }
            else if (request.Kind == "complete")
            {
                state.CompletionAcknowledged = true;
                state.StopRequested = false;
            }
            else if (request.Kind == "fail")
            {
                state.StopRequested = true;
                state.CompletionAcknowledged = false;
            }
        }, linked.Token);
    }

    private async Task<NoteAsrLeaseEntity?> LockLeaseForAckAsync(Guid owner, Guid attemptId, Guid leaseId,
        CancellationToken ct)
    {
        if (!db.Database.IsRelational())
            return await db.NoteAsrLeases.SingleOrDefaultAsync(x => x.Id == leaseId && x.OwnerId == owner &&
                x.AttemptId == attemptId, ct);
        return await db.NoteAsrLeases.FromSqlInterpolated($"""
            SELECT * FROM note_asr_leases
            WHERE id = {leaseId} AND owner_id = {owner} AND attempt_id = {attemptId}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
    }

    private async Task<bool> IsExactReceiptAsync(Guid owner, Guid attemptId, Guid leaseId,
        NoteAsrAckRequest request, CancellationToken ct) =>
        await db.NoteAsrAckReceipts.AsNoTracking().AnyAsync(x => x.OwnerId == owner && x.AttemptId == attemptId &&
            x.LeaseId == leaseId && x.FencingToken == request.FencingToken && x.Kind == request.Kind &&
            x.SegmentIndex == request.SegmentIndex && x.Result == request.Result &&
            x.ProviderHandle == request.ProviderHandle && x.ResultText == request.ResultText &&
            x.CoverageJson == request.CoverageJson && x.ErrorCode == request.ErrorCode &&
            x.ErrorMessage == request.ErrorMessage, ct);

    private static bool IsUniqueViolation(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" }) return true;
            if (current.GetType().Name == "SqliteException" &&
                current.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public async Task<NoteAsrAttemptDetailDto> GetAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "转写 attempt 不存在");
        return DescribeAttempt(attempt);
    }

    public async Task<IReadOnlyList<NoteAsrAttemptDetailDto>> ListAttemptsAsync(Guid taskId, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return (await db.NoteAsrAttempts.AsNoTracking().Where(x => x.OwnerId == owner && x.TaskId == taskId)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct)).Select(DescribeAttempt).ToArray();
    }

    public async Task<IReadOnlyList<NoteAsrAttemptDetailDto>> ListRecentAttemptsAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return (await db.NoteAsrAttempts.AsNoTracking().Where(x => x.OwnerId == owner)
            .OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct)).Select(DescribeAttempt).ToArray();
    }

    /// <summary>
    /// Persists an immutable route/attempt snapshot from an existing legacy NoteAsr task.
    /// A null request keeps the legacy dedicated-ASR destination untouched.
    /// </summary>
    public async Task<NoteAsrAttemptDetailDto> CreateAttemptAsync(Guid taskId, Guid versionId,
        int expectedSegments, string language, NoteAsrRouteConsent? routeConsent, CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        if (taskId == Guid.Empty || versionId == Guid.Empty || expectedSegments < 1 || expectedSegments > 5000 ||
            language is null || language.Length > 32) throw new DomainException(40001, "attempt 输入无效");
        var task = await legacyTasks.ReadAsync(owner, taskId, ct);
        var version = await materials.GetAsync(owner, versionId, ct);
        var inputSha = NoteMaterialManifestValidator.Hash(version.InputJson);
        if (task.Request.VersionId != versionId || task.NoteId != version.Upload.NoteId ||
            task.InputSha256 != inputSha || task.Steps.Count != expectedSegments ||
            !string.Equals(task.Request.Language, language, StringComparison.Ordinal))
            throw new DomainException(40901, "attempt 必须绑定当前账号已有任务的同版本、同材料、同分片数和语言");
        if (await db.NoteAsrAttempts.AnyAsync(x => x.OwnerId == owner && x.TaskId == taskId, ct))
            throw new DomainException(40901, "该任务已有执行快照；重跑须使用新任务标识");
        var attempt = await CreateAttemptCoreAsync(owner, taskId, versionId, expectedSegments, language, routeConsent, ct);
        return DescribeAttempt(attempt);
    }

    internal async Task<NoteAsrAttemptEntity> CreateAttemptCoreAsync(Guid owner, Guid taskId, Guid versionId,
        int expectedSegments, string language, NoteAsrRouteConsent? routeConsent, CancellationToken ct)
    {
        var config = routeConsent is null
            ? await db.NoteAsrRouteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == owner, ct)
            : await db.NoteAsrRouteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == owner && x.Revision == routeConsent.ConfigRevision, ct);
        var route = routeConsent?.Route;
        // A null consent is the legacy dedicated-ASR snapshot. It must never be reinterpreted using current config.
        route ??= NoteAsrRoute.CloudAsr;
        if (!NoteAsrRoute.Known(route)) throw new DomainException(40001, "执行位置无效");
        var executionMode = routeConsent?.ExecutionMode ?? NoteAsrExecutionMode.Realtime;
        if (!NoteAsrExecutionMode.Known(executionMode) ||
            executionMode == NoteAsrExecutionMode.Batch && route != NoteAsrRoute.CloudMultimodal)
            throw new DomainException(40001, "执行方式无效；Batch 仅支持 MiMo 多模态云端任务");
        if (executionMode == NoteAsrExecutionMode.Batch &&
            (routeConsent is null || !routeConsent.Confirmed || !routeConsent.BatchScopeConfirmed))
            throw new DomainException(40001, "Batch attempt 必须冻结整批范围确认，不能用后续提交临时替代");
        if (routeConsent is not null && NoteAsrRoute.IsCloud(route) && !routeConsent.Confirmed)
            throw new DomainException(40001, "请明确确认云端音频外发、长等待与可能计费");
        if (routeConsent is not null && NoteAsrRoute.IsCloud(route) && config is null)
            throw new DomainException(40901, "云端路由配置已变化，请刷新后重新确认");
        var version = await materials.GetAsync(owner, versionId, ct);
        var task = await legacyTasks.ReadAsync(owner, taskId, ct);
        var inputSha = NoteMaterialManifestValidator.Hash(version.InputJson);
        if (task.Request.VersionId != versionId || task.NoteId != version.Upload.NoteId ||
            task.InputSha256 != inputSha || task.Steps.Count != expectedSegments ||
            !string.Equals(task.Request.Language, language, StringComparison.Ordinal))
            throw new DomainException(40901, "任务快照与版本、材料、分片数或语言不一致");
        var attempt = new NoteAsrAttemptEntity { Id = Guid.NewGuid(), OwnerId = owner, TaskId = taskId,
            NoteId = version.Upload.NoteId, VersionId = versionId,
            InputSha256 = inputSha,
            Route = route, ConfigRevision = config?.Revision ?? routeConsent?.ConfigRevision ?? Guid.Empty,
            ConsentVersion = routeConsent?.ConsentVersion ?? config?.ConsentVersion ?? "",
            ExecutionMode = executionMode, Language = language, ExpectedSegments = expectedSegments };
        if (route == NoteAsrRoute.Local)
        {
            if (config?.LocalModelId is not { } localModelId || config.DefaultRoute != NoteAsrRoute.Local)
                throw new DomainException(40901, "本机执行位置尚未配置模型");
            // The local model lives on registered Windows nodes; it is not a cloud profile and needs no key.
            attempt.LocalModelId = localModelId;
            attempt.LocalModelRevision = config.LocalModelRevision;
        }
        else
        {
            var modelId = config?.CloudModelId;
            if (modelId is null && route == NoteAsrRoute.CloudAsr)
            {
                var legacy = await db.AiModelProfiles.AsNoTracking().Where(x => x.Enabled &&
                    (x.Capabilities & AiModelCapabilityFlags.Transcription) != 0)
                    .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
                modelId = legacy?.Id;
            }
            if (modelId is not { } id) throw new DomainException(40901, "云端执行位置没有可用模型配置");
            var model = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Enabled, ct)
                ?? throw new DomainException(40901, "云端模型配置已变化");
            var preset = presets.Resolve(model.AsrPresetId);
            var descriptor = preset.Descriptor;
            if (route == NoteAsrRoute.CloudMultimodal && descriptor.Id != "mimo-multimodal")
                throw new DomainException(40901, "多模态执行位置尚未配置 MiMo 多模态预设");
            attempt.ModelId = model.Id; attempt.ModelRevision = model.Revision; attempt.PresetId = descriptor.Id;
            attempt.PresetVersion = descriptor.Version; attempt.Model = model.Model;
            attempt.Endpoint = AiAsrPresetRegistry.OperationEndpoint(preset.NormalizeEndpoint(model.BaseUrl), descriptor);
            var promptTemplate = AiAsrPromptTemplate.Normalize(model.AsrPrompt);
            var prompt = promptTemplate.Snapshot(language);
            attempt.PromptTemplate = promptTemplate.Text;
            attempt.PromptTemplateSha256 = promptTemplate.Sha256;
            attempt.PromptTemplateRevision = promptTemplate.Revision;
            attempt.PromptRendered = prompt.Text;
            attempt.PromptSha256 = prompt.Sha256;
            attempt.PromptRevision = prompt.Revision;
        }
        db.NoteAsrAttempts.Add(attempt);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            // Concurrent Start/replay may have won the owner+task unique constraint with the same snapshot.
            db.Entry(attempt).State = EntityState.Detached;
            var existing = await db.NoteAsrAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OwnerId == owner && x.TaskId == taskId, ct);
            if (existing is null ||
                existing.VersionId != versionId || existing.InputSha256 != attempt.InputSha256 ||
                existing.Route != route || existing.ConfigRevision != attempt.ConfigRevision ||
                existing.ExecutionMode != executionMode ||
                existing.ExpectedSegments != expectedSegments || existing.Language != language)
                throw new DomainException(40901, "该任务已有不同执行快照，未覆盖");
            return existing;
        }
        return attempt;
    }

    private async Task<NoteAsrAttemptEntity?> NextLocalAttemptAsync(Guid owner, NoteAsrNodeEntity node, DateTimeOffset now, CancellationToken ct)
    {
        var busy = await db.NoteAsrLeases.AnyAsync(x => x.OwnerId == owner && x.Status == "Active" && x.ExpiresAt > now, ct);
        if (busy) return null;
        // Apply the node model/revision predicate in SQL before taking the oldest row. A poisoned
        // first row must not permanently starve a later compatible task.
        var legacy = await db.NoteAsrAttempts.AsNoTracking().Where(x => x.OwnerId == owner &&
                x.Route == NoteAsrRoute.Local && x.State == "Queued" && !x.StopRequested &&
                (x.ExecutionMode == null || x.ExecutionMode == NoteAsrExecutionMode.Realtime) &&
                (x.LocalModelId == null || x.LocalModelId == node.ModelId) &&
                (x.LocalModelRevision == null || x.LocalModelRevision == node.ModelRevision))
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (legacy is not null) return legacy;
        // Legacy tasks without a persisted local attempt are historical dedicated cloud-ASR work. They are
        // deliberately not migrated by current config; only an explicit new reprocess may create a local attempt.
        return null;
    }

    /// <summary>
    /// Expires only this node's stale Active leases before selecting another queued attempt. The old
    /// attempt becomes UNKNOWN and is never automatically re-queued; a distinct queued attempt remains claimable.
    /// </summary>
    private async Task<int> ExpireStaleNodeLeasesAsync(Guid owner, Guid nodeId, DateTimeOffset now, CancellationToken ct)
    {
        var stale = await db.NoteAsrLeases.AsNoTracking().Where(x => x.OwnerId == owner && x.NodeId == nodeId &&
            x.Status == "Active" && x.ExpiresAt <= now).OrderByDescending(x => x.FencingToken).Take(16)
            .Select(x => new { x.Id, x.AttemptId, x.LeaseToken, x.FencingToken }).ToListAsync(ct);
        var expired = 0;
        foreach (var lease in stale)
        {
            var leaseAffected = await db.NoteAsrLeases
                .Where(x => x.Id == lease.Id && x.OwnerId == owner && x.NodeId == nodeId &&
                    x.LeaseToken == lease.LeaseToken && x.FencingToken == lease.FencingToken &&
                    x.Status == "Active" && x.ExpiresAt <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Expired")
                    .SetProperty(x => x.EndedAt, now), ct);
            if (leaseAffected == 0) continue;
            expired++;
            await db.NoteAsrAttempts
                .Where(x => x.Id == lease.AttemptId && x.OwnerId == owner && x.State == "Leased" &&
                    x.FencingToken == lease.FencingToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.State, "Unknown")
                    .SetProperty(x => x.LastErrorCode, "lease_expired")
                    .SetProperty(x => x.LastErrorMessage, "租约过期，未证明本机已停止推理；等待核对后再明确恢复")
                    .SetProperty(x => x.UpdatedAt, now), ct);
        }
        return expired;
    }

    private async Task<NoteAsrNodeEntity> OwnedNodeAsync(Guid owner, Guid nodeId, CancellationToken ct) =>
        await db.NoteAsrNodes.SingleOrDefaultAsync(x => x.Id == nodeId && x.OwnerId == owner, ct)
        ?? throw new DomainException(40401, "ASR 节点不存在");

    private async Task<NoteAsrLeaseEntity> ValidLeaseAsync(Guid owner, Guid leaseId, Guid token, long fencing, CancellationToken ct)
    {
        var lease = await db.NoteAsrLeases.SingleOrDefaultAsync(x => x.Id == leaseId && x.OwnerId == owner, ct)
            ?? throw new DomainException(40401, "租约不存在");
        if (lease.LeaseToken != token || lease.FencingToken != fencing || lease.Status != "Active" || lease.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new DomainException(40901, "租约已失效");
        return lease;
    }

    /// <summary>Shared cloud destinations reuse the existing ASR checks: enabled, transcription flag, credential and preset.</summary>
    private async Task ValidateCloudDestinationAsync(Guid profileId, CancellationToken ct)
    {
        var profile = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profileId, ct)
            ?? throw new DomainException(40901, "云端 ASR 模型配置不存在");
        if (!profile.Enabled || (profile.Capabilities & AiModelCapabilityFlags.Transcription) == 0)
            throw new DomainException(40901, "云端 ASR 模型配置不可用或未开启语音识别能力");
        if (!CredentialAvailable(profile.ApiKeySecret)) throw new DomainException(40901, "ASR凭据当前不可用，请管理员重新保存");
        try { presets.Resolve(profile.AsrPresetId).ValidateModel(profile.Model); }
        catch (DomainException) { throw; }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
        { throw new DomainException(40901, "ASR预设当前不可用"); }
    }

    private bool NodeClaimable(NoteAsrNodeEntity node, DateTimeOffset now) =>
        node.Enabled && node.ModelVerified && !string.IsNullOrWhiteSpace(node.ModelId) &&
        node.LastHeartbeatAt > now.AddSeconds(-Options.NodeHeartbeatTimeoutSeconds);

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = Owner;
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用");
        return owner;
    }

    private static bool ValidCapabilities(IReadOnlyList<string>? values) =>
        values is not null && values.Count <= 16 && values.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 64);

    private static bool ValidResourceSnapshot(string? value)
    {
        if (value is null) return true;
        if (value.Length > 16_384) return false;
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static string? CleanOptional(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max && !value.Any(char.IsControl)
            ? value.Trim() : throw new DomainException(40001, "节点运行字段无效");

    private bool CredentialAvailable(string protectedValue)
    {
        try { return !string.IsNullOrEmpty(secrets.Unprotect(protectedValue)); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException) { return false; }
    }

    private static NoteAsrRouteConfigDto Describe(NoteAsrRouteConfigEntity x) =>
        new(x.Revision, x.DefaultRoute, x.LocalModelId, x.LocalModelRevision, x.CloudModelId is not null,
            x.ConsentVersion, x.UpdatedAt, x.CloudModelId);

    private NoteAsrNodeDto Describe(NoteAsrNodeEntity x, DateTimeOffset now)
    {
        IReadOnlyList<string> capabilities;
        try { capabilities = JsonSerializer.Deserialize<string[]>(x.CapabilitiesJson) ?? []; }
        catch (JsonException) { capabilities = []; }
        return new(x.Id, x.NodeId, x.Enabled, x.ModelId, x.ModelRevision, x.ModelVerified, capabilities,
            x.LastHeartbeatAt, NodeClaimable(x, now), x.CreatedAt, x.RuntimeId, x.RuntimeVersion,
            x.ResourceSnapshotJson, x.SampledAt);
    }

    private static async Task<NoteAsrLeaseDto> Describe(PimDbContext db, Guid owner, NoteAsrLeaseEntity x, CancellationToken ct)
    {
        var attempt = await db.NoteAsrAttempts.AsNoTracking().Where(a => a.Id == x.AttemptId && a.OwnerId == owner)
            .Select(a => new { a.Route, a.StopRequested }).SingleAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return new(x.Id, x.LeaseToken, x.AttemptId, x.FencingToken, x.ExpiresAt, attempt.Route, owner, now,
            StopRequested: attempt.StopRequested);
    }

    private static NoteAsrAttemptDetailDto DescribeAttempt(NoteAsrAttemptEntity x) =>
        new(x.Id, x.OwnerId, x.TaskId, x.NoteId, x.VersionId, x.InputSha256, x.Route, x.State, x.StopRequested,
            x.ConfigRevision, x.ConsentVersion, x.ModelId, x.ModelRevision, x.PresetId, x.PresetVersion, x.Model,
            x.Endpoint, x.PromptTemplateRevision, x.PromptRevision, x.ExecutionMode, x.ProviderHandle,
            x.LocalModelId, x.LocalModelRevision, x.Language,
            x.FencingToken, x.ExpectedSegments, x.CompletedSegments, x.FailedSegments, x.LastErrorCode,
            x.LastErrorMessage, x.CreatedAt, x.UpdatedAt);
}
