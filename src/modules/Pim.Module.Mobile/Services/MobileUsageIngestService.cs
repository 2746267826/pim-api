using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileUsageIngestService
{
    /// <summary>
    /// 活跃处理租约。生产最慢请求实测约 11.3 分钟，15 分钟避免把仍在执行的请求重认领。
    /// created_at 保留首次创建时间，租约只存在于 error_json envelope。
    /// </summary>
    internal static readonly TimeSpan PendingBatchLease =
        MobileSyncBatchEnvelopeCodec.DefaultProcessingLease;
    internal static readonly TimeSpan MaxAttemptDuration = TimeSpan.FromMinutes(12);
    internal const int MaxProcessingAttempts = 5;

    internal static readonly TimeSpan MaxUploadWindow = TimeSpan.FromHours(25);
    internal const int MaxUploadItems = 1_500;

    /// <summary>汇总时长超过自身窗口时按窗口裁剪入库，并打上该标记（#240）。</summary>
    private const string DurationClampedFlags = "[\"duration-clamped\"]";
    private const string EmptyFlags = "[]";

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileSessionInterpreter _sessionInterpreter;
    private readonly TimeProvider _timeProvider;
    private readonly MobileAppCatalogOverrideService? _catalogOverrideService;
    private readonly MobileAnalyticsMaterializationService? _materializationService;

    public MobileUsageIngestService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileSessionInterpreter sessionInterpreter,
        TimeProvider timeProvider,
        MobileAppCatalogOverrideService? catalogOverrideService = null,
        MobileAnalyticsMaterializationService? materializationService = null)
    {
        _db = db;
        _currentUser = currentUser;
        _sessionInterpreter = sessionInterpreter;
        _timeProvider = timeProvider;
        _catalogOverrideService = catalogOverrideService;
        _materializationService = materializationService;
    }

    public async Task<MobileUsageIngestResult> IngestAsync(
        MobileUsageEventsUploadRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var strategy = _db.Database.CreateExecutionStrategy();
        var attemptState = new IngestAttemptState();
        using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptDeadline.CancelAfter(MaxAttemptDuration);
        try
        {
            return await strategy.ExecuteAsync(
                token => IngestAttemptAsync(userId, request, attemptState, token),
                attemptDeadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
            && attemptDeadline.IsCancellationRequested)
        {
            await RecordProcessingFailureAsync(
                userId,
                request,
                attemptState,
                new TimeoutException("Mobile sync batch exceeded the bounded processing duration."));
            throw new TimeoutException("Mobile sync batch exceeded the bounded processing duration.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is not MobileSyncBatchRequestValidationException
            && ex is not DomainException)
        {
            await RecordProcessingFailureAsync(userId, request, attemptState, ex);
            throw;
        }
    }

    private async Task<MobileUsageIngestResult> IngestAttemptAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        IngestAttemptState attemptState,
        CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        // 空载上传（无任何条目）不创建批次行（EPIC #254 S11 空转批次）：
        // 它没有可处理的数据，其窗口也不构成任何"覆盖"——批次行的覆盖语义只属于真实处理过的窗口。
        // 零计数结果保持与客户端的既有响应契约，客户端无需为此重试。
        if (request.Apps.Count == 0 && request.Events.Count == 0 && request.Summaries.Count == 0)
            return new MobileUsageIngestResult(request.BatchId, 0, 0, 0, 0, []);

        ValidateRequestBudget(request);
        var payloadSha256 = ComputePayloadSha256(request);
        var leaseId = Guid.NewGuid();
        var existingBatch = await FindBatchAsync(userId, request, ct);

        if (existingBatch is not null)
        {
            EnsurePayloadMatches(existingBatch, payloadSha256);
            if (MobileSyncBatchStatus.IsTerminal(existingBatch.Status))
                return BuildReplayResult(existingBatch, request);
        }

        var now = _timeProvider.GetUtcNow();
        MobileSyncBatchEntity batch;
        if (existingBatch is null)
        {
            var initialEnvelope = MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                [],
                payloadSha256,
                now.Add(PendingBatchLease),
                attemptCount: 1,
                leaseId: leaseId.ToString("N"));
            batch = new MobileSyncBatchEntity
            {
                UserId = userId,
                DeviceId = request.DeviceId,
                BatchId = request.BatchId,
                WindowStartUtc = request.WindowStartUtc,
                WindowEndUtc = request.WindowEndUtc,
                AcceptedCount = 0,
                RejectedCount = 0,
                SkippedCount = 0,
                FailedCount = 0,
                Status = MobileSyncBatchStatus.Pending,
                ErrorJson = initialEnvelope,
                CreatedAt = now,
                CompletedAtUtc = null
            };
            _db.Set<MobileSyncBatchEntity>().Add(batch);
            // 在写库之前就认领：执行策略重试时它才知道这条 pending 批次是自己的，可以接管重跑。
            attemptState.OwnedPendingBatchId = batch.Id;
            attemptState.LeaseId = leaseId;
            attemptState.AttemptCount = 1;

            // 先把 pending 行落库（独立提交，不参与下面的事务）：
            // 上传中断 / 进程崩溃留下的批次必须能被积压监控与质量面板看见（#243）。
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (!ct.IsCancellationRequested)
            {
                // 并发重投：另一个请求先插入了同一批次，回放它的结果。
                _db.ChangeTracker.Clear();
                var winner = await FindBatchAsync(userId, request, ct);
                if (winner is null)
                    throw;

                EnsurePayloadMatches(winner, payloadSha256);
                return BuildReplayResult(winner, request);
            }
        }
        else if (existingBatch.Id == attemptState.OwnedPendingBatchId)
        {
            // 本次请求自己的重试（执行策略重跑）：直接接管。
            batch = await RenewOwnedPendingBatchAsync(
                existingBatch,
                payloadSha256,
                now,
                leaseId,
                ct);
            attemptState.LeaseId = leaseId;
        }
        else if (await TryClaimStalePendingBatchAsync(
                     existingBatch,
                     payloadSha256,
                     RequestItemCount(request),
                     now,
                     leaseId,
                     ct) is { } claimed)
        {
            // 上一个处理者已经消失（租约过期），原子认领后幂等重跑。
            batch = claimed;
            attemptState.LeaseId = leaseId;
        }
        else
        {
            // 同一 batchId 仍在处理中：不得返回全 0 + 空 itemResults 的假成功确认。
            return BuildInFlightResult(existingBatch, request);
        }

        attemptState.AttemptCount = Math.Max(
            1,
            MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson).AttemptCount);

        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
                transaction = await _db.Database.BeginTransactionAsync(ct);

            var itemResults = new List<MobileIngestItemResult>();

            foreach (var app in request.Apps)
                itemResults.Add(await UpsertAppAsync(userId, request.DeviceId, app, now, ct));

            itemResults.AddRange(await AddEventsIfMissingAsync(userId, request, now, ct));

            foreach (var summary in request.Summaries)
                itemResults.Add(await UpsertSummaryAsync(userId, request.DeviceId, summary, now, ct));

            var result = BuildResult(batch.BatchId, itemResults);

            // 先落库再派生：会话重建读的是数据库快照，事件不先 flush 的话，
            // 本批自己的事件永远不会参与本次重建（只能等下一批，形成"永远落后一批"的会话缺口，#248）。
            await _db.SaveChangesAsync(ct);

            // 事件集不变 => 会话不变。补偿批重复上传同一窗口时（#248 的 102 个补偿批），
            // 这里直接跳过"整窗口删除 + 重建"，避免把同一份派生数据反复重写上百万行。
            // 例外：窗口比上一批更宽时，上一批按"窗口末端封口"的开放会话会被重新封口 ——
            // 事件没变但会话结束时间会变，这种窗口扩展必须重建（评审 #1）。
            if (HasNewUsageEvents(result) || await HasExtendableOpenSessionAsync(userId, request, ct))
            {
                await _sessionInterpreter.RebuildSessionsAsync(
                    userId,
                    request.DeviceId,
                    request.WindowStartUtc,
                    request.WindowEndUtc,
                    ct);
            }

            // 派生工作（会话重建 / 派生表标记 / 物化）先跑，成功之后才把批次推进到终态：
            // 中途失败时批次保持 pending（"上传中断"信号），而不是先宣告完成再回滚（#243）。
            await MarkAffectedAnalyticsStaleAsync(
                request,
                request.WindowStartUtc,
                request.WindowEndUtc,
                ct);

            // 把该窗口的派生分析数据落库（#247②）：块与聚合不再只存在于请求期间的在线计算里。
            // 只有本批真的写入了条目（事件/汇总/元数据）时才重算 —— 重复补偿批仍然跳过。
            if (_materializationService is not null && result.AcceptedCount > 0)
            {
                await _materializationService.MaterializeAsync(
                    userId,
                    request.DeviceId,
                    request.WindowStartUtc,
                    request.WindowEndUtc,
                    ct);
            }

            // accepted_count 反映该批全部被接受的条目（事件 / 元数据 / 汇总），
            // 而不是只有 usage-event（#243）。条件写同时做 lease fencing：
            // 旧 owner 在新请求接管后不能把半批事务提交为终态。
            await FinalizePendingBatchAsync(
                batch,
                attemptState,
                result,
                now,
                ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            attemptState.OwnedPendingBatchId = null;
            return result;
        }
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            await RollbackAndDisposeAsync(transaction);
            transaction = null;
            _db.ChangeTracker.Clear();

            // 只有当"另一路写入真的把这个批次跑完"时才算并发胜者；
            // 否则（例如事件唯一约束冲突）必须原样抛出，不能把失败伪装成"回放成功"。
            var persistedWinner = await FindBatchAsync(userId, request, ct);
            if (persistedWinner is null || !MobileSyncBatchStatus.IsTerminal(persistedWinner.Status))
                throw;

            return BuildReplayResult(persistedWinner, request);
        }
        catch
        {
            await RollbackAndDisposeAsync(transaction);
            transaction = null;
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<MobileSyncBatchEntity?> FindBatchAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        CancellationToken ct)
        => await _db.Set<MobileSyncBatchEntity>()
            .SingleOrDefaultAsync(b => b.UserId == userId
                && b.DeviceId == request.DeviceId
                && b.BatchId == request.BatchId, ct);

    /// <summary>
    /// 原子认领租约已过、且不在冷却期的 pending 批次。每次认领换新 LeaseId，
    /// 旧 owner 的完成/失败条件写会因此失败并回滚。
    /// </summary>
    private async Task<MobileSyncBatchEntity?> TryClaimStalePendingBatchAsync(
        MobileSyncBatchEntity batch,
        string payloadSha256,
        int requestItemCount,
        DateTimeOffset now,
        Guid leaseId,
        CancellationToken ct)
    {
        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
        if (envelope.RetryAfterUtc is { } retryAfter && retryAfter > now)
            return null;

        var leaseUntilUtc = MobileSyncBatchEnvelopeCodec.EffectiveLeaseUntil(
            batch.CreatedAt,
            envelope);
        if (leaseUntilUtc > now)
            return null;

        if (envelope.AttemptCount >= MaxProcessingAttempts)
        {
            await MarkAttemptsExhaustedAsync(
                batch,
                envelope,
                payloadSha256,
                requestItemCount,
                now,
                ct);
            throw new DomainException(
                MobileSyncErrorCodes.RetryExhausted,
                "同步批次处理次数已耗尽；本地记录保留，请使用新的批次标识重试");
        }

        var attemptCount = Math.Max(1, envelope.AttemptCount) + 1;
        var nextEnvelope = MobileSyncBatchEnvelopeCodec.Serialize(
            envelope.ItemResults,
            envelope.BatchErrors,
            envelope.PayloadSha256 ?? payloadSha256,
            now.Add(PendingBatchLease),
            attemptCount,
            envelope.FailureCode,
            leaseId.ToString("N"));

        if (!_db.Database.IsRelational())
        {
            batch.ErrorJson = nextEnvelope;
            await _db.SaveChangesAsync(ct);
            return batch;
        }

        FormattableString claim = $@"
            UPDATE mobile_sync_batches
               SET error_json = {nextEnvelope}::jsonb
             WHERE id = {batch.Id}
               AND status = {MobileSyncBatchStatus.Pending}
               AND CASE
                     WHEN error_json->>'LeaseUntilUtc' IS NOT NULL
                       THEN (error_json->>'LeaseUntilUtc')::timestamptz
                     WHEN COALESCE(error_json->>'LeaseId', '') = ''
                      AND COALESCE((error_json->>'AttemptCount')::int, 0) = 0
                       THEN created_at + {PendingBatchLease}
                     ELSE '-infinity'::timestamptz
                   END <= {now}
               AND COALESCE((error_json->>'RetryAfterUtc')::timestamptz, {now}) <= {now};";
        var claimed = await _db.Database.ExecuteSqlInterpolatedAsync(claim, ct);
        if (claimed == 0)
            return null;

        _db.ChangeTracker.Clear();
        return await _db.Set<MobileSyncBatchEntity>().SingleOrDefaultAsync(b => b.Id == batch.Id, ct);
    }

    private async Task MarkAttemptsExhaustedAsync(
        MobileSyncBatchEntity batch,
        MobileSyncBatchEnvelope envelope,
        string payloadSha256,
        int requestItemCount,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var failureCode = string.IsNullOrWhiteSpace(envelope.FailureCode)
            ? "processing-attempts-exhausted"
            : envelope.FailureCode;
        var exhaustedEnvelope = MobileSyncBatchEnvelopeCodec.Serialize(
            [],
            [failureCode],
            envelope.PayloadSha256 ?? payloadSha256,
            leaseUntilUtc: null,
            envelope.AttemptCount,
            failureCode,
            envelope.LeaseId);

        if (!_db.Database.IsRelational())
        {
            batch.Status = MobileSyncBatchStatus.Failed;
            batch.AcceptedCount = 0;
            batch.RejectedCount = 0;
            batch.SkippedCount = 0;
            batch.FailedCount = requestItemCount;
            batch.CompletedAtUtc = now;
            batch.ErrorJson = exhaustedEnvelope;
            await _db.SaveChangesAsync(ct);
            return;
        }

        FormattableString exhaust = $@"
            UPDATE mobile_sync_batches
               SET status = {MobileSyncBatchStatus.Failed},
                   accepted_count = 0,
                   rejected_count = 0,
                   skipped_count = 0,
                   failed_count = {requestItemCount},
                   error_json = {exhaustedEnvelope}::jsonb,
                   completed_at_utc = {now}
             WHERE id = {batch.Id}
               AND status = {MobileSyncBatchStatus.Pending}
               AND COALESCE(error_json->>'LeaseId', '') = {envelope.LeaseId ?? string.Empty};";
        await _db.Database.ExecuteSqlInterpolatedAsync(exhaust, ct);
    }

    private async Task<MobileSyncBatchEntity> RenewOwnedPendingBatchAsync(
        MobileSyncBatchEntity batch,
        string payloadSha256,
        DateTimeOffset now,
        Guid leaseId,
        CancellationToken ct)
    {
        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
        var previousLeaseId = envelope.LeaseId;
        var attemptCount = Math.Max(1, envelope.AttemptCount) + 1;
        var nextEnvelope = MobileSyncBatchEnvelopeCodec.Serialize(
            envelope.ItemResults,
            envelope.BatchErrors,
            envelope.PayloadSha256 ?? payloadSha256,
            now.Add(PendingBatchLease),
            attemptCount,
            envelope.FailureCode,
            leaseId.ToString("N"));

        if (!_db.Database.IsRelational())
        {
            batch.ErrorJson = nextEnvelope;
            await _db.SaveChangesAsync(ct);
            return batch;
        }

        FormattableString renew = $@"
            UPDATE mobile_sync_batches
               SET error_json = {nextEnvelope}::jsonb
             WHERE id = {batch.Id}
               AND status = {MobileSyncBatchStatus.Pending}
               AND COALESCE(error_json->>'LeaseId', '') = {previousLeaseId ?? string.Empty};";
        var renewed = await _db.Database.ExecuteSqlInterpolatedAsync(renew, ct);
        if (renewed == 0)
            throw new MobileSyncBatchLeaseLostException();

        _db.ChangeTracker.Clear();
        return await _db.Set<MobileSyncBatchEntity>().SingleAsync(b => b.Id == batch.Id, ct);
    }

    private async Task FinalizePendingBatchAsync(
        MobileSyncBatchEntity batch,
        IngestAttemptState attemptState,
        MobileUsageIngestResult result,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var leaseId = attemptState.LeaseId
            ?? throw new MobileSyncBatchLeaseLostException();
        var envelope = MobileSyncBatchEnvelopeCodec.Serialize(
            result.ItemResults,
            BuildBatchErrors(result.ItemResults),
            MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson).PayloadSha256,
            leaseUntilUtc: null,
            attemptState.AttemptCount,
            leaseId: leaseId.ToString("N"));
        var status = result.FailedCount > 0
            ? MobileSyncBatchStatus.Failed
            : MobileSyncBatchStatus.Completed;

        if (!_db.Database.IsRelational())
        {
            batch.AcceptedCount = result.AcceptedCount;
            batch.RejectedCount = result.RejectedCount;
            batch.SkippedCount = result.SkippedCount;
            batch.FailedCount = result.FailedCount;
            batch.Status = status;
            batch.CompletedAtUtc = now;
            batch.ErrorJson = envelope;
            await _db.SaveChangesAsync(ct);
            return;
        }

        FormattableString finalize = $@"
            UPDATE mobile_sync_batches
               SET accepted_count = {result.AcceptedCount},
                   rejected_count = {result.RejectedCount},
                   skipped_count = {result.SkippedCount},
                   failed_count = {result.FailedCount},
                   status = {status},
                   error_json = {envelope}::jsonb,
                   completed_at_utc = {now}
             WHERE id = {batch.Id}
               AND status = {MobileSyncBatchStatus.Pending}
               AND error_json->>'LeaseId' = {leaseId.ToString("N")};";
        var finalized = await _db.Database.ExecuteSqlInterpolatedAsync(finalize, ct);
        if (finalized == 0)
            throw new MobileSyncBatchLeaseLostException();
    }

    private async Task RecordProcessingFailureAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        IngestAttemptState attemptState,
        Exception exception)
    {
        if (attemptState.LeaseId is null)
            return;

        _db.ChangeTracker.Clear();
        var batch = await FindBatchAsync(userId, request, CancellationToken.None);
        if (batch is null || MobileSyncBatchStatus.IsTerminal(batch.Status))
            return;

        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
        if (!string.Equals(
                envelope.LeaseId,
                attemptState.LeaseId.Value.ToString("N"),
                StringComparison.Ordinal))
            return;

        var now = _timeProvider.GetUtcNow();
        var attemptCount = Math.Max(1, attemptState.AttemptCount);
        var exhausted = attemptCount >= MaxProcessingAttempts;
        var failureCode = ClassifyFailure(exception);
        var status = exhausted ? MobileSyncBatchStatus.Failed : MobileSyncBatchStatus.Pending;
        var failedCount = exhausted ? RequestItemCount(request) : 0;
        var nextEnvelope = MobileSyncBatchEnvelopeCodec.Serialize(
            [],
            [failureCode],
            envelope.PayloadSha256 ?? ComputePayloadSha256(request),
            leaseUntilUtc: null,
            attemptCount,
            failureCode,
            envelope.LeaseId,
            exhausted ? null : now.Add(RetryDelay(attemptCount)));

        if (!_db.Database.IsRelational())
        {
            batch.Status = status;
            batch.AcceptedCount = 0;
            batch.RejectedCount = 0;
            batch.SkippedCount = 0;
            batch.FailedCount = failedCount;
            batch.CompletedAtUtc = exhausted ? now : null;
            batch.ErrorJson = nextEnvelope;
            await _db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        FormattableString record = $@"
            UPDATE mobile_sync_batches
               SET status = {status},
                   accepted_count = 0,
                   rejected_count = 0,
                   skipped_count = 0,
                   failed_count = {failedCount},
                   error_json = {nextEnvelope}::jsonb,
                   completed_at_utc = {(exhausted ? now : (DateTimeOffset?)null)}
             WHERE id = {batch.Id}
               AND status = {MobileSyncBatchStatus.Pending}
               AND error_json->>'LeaseId' = {attemptState.LeaseId.Value.ToString("N")};";
        await _db.Database.ExecuteSqlInterpolatedAsync(record, CancellationToken.None);
    }

    private static void EnsurePayloadMatches(MobileSyncBatchEntity batch, string payloadSha256)
    {
        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
        if (envelope.PayloadSha256 is not null
            && !string.Equals(envelope.PayloadSha256, payloadSha256, StringComparison.Ordinal))
        {
            throw new DomainException(
                MobileSyncErrorCodes.PayloadConflict,
                "同一同步批次标识已绑定另一份输入；原批次保留，请使用新的批次标识重试");
        }
    }

    private static MobileUsageIngestResult BuildReplayResult(
        MobileSyncBatchEntity batch,
        MobileUsageEventsUploadRequest request)
    {
        if (!MobileSyncBatchStatus.IsTerminal(batch.Status))
            return BuildInFlightResult(batch, request);

        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
        if (string.Equals(batch.Status, MobileSyncBatchStatus.Failed, StringComparison.OrdinalIgnoreCase)
            && envelope.ItemResults.Count == 0)
        {
            var failureCode = string.IsNullOrWhiteSpace(envelope.FailureCode)
                ? "sync-batch-failed"
                : envelope.FailureCode;
            var errorCode = string.IsNullOrWhiteSpace(envelope.FailureCode)
                ? 40901
                : MobileSyncErrorCodes.RetryExhausted;
            throw new DomainException(
                errorCode,
                $"同步批次已终止（{failureCode}）；本地记录保留，请使用新的批次标识重试");
        }

        return BuildPersistedResult(batch);
    }

    private static MobileUsageIngestResult BuildInFlightResult(
        MobileSyncBatchEntity batch,
        MobileUsageEventsUploadRequest request)
        => new(
            batch.BatchId,
            0,
            0,
            0,
            RequestItemCount(request),
            []);

    private sealed class IngestAttemptState
    {
        /// <summary>本次请求自己插入的 pending 批次 Id（执行策略重试时用于接管）。</summary>
        public Guid? OwnedPendingBatchId { get; set; }

        public Guid? LeaseId { get; set; }

        public int AttemptCount { get; set; }
    }

    private async Task<MobileIngestItemResult> UpsertAppAsync(
        Guid userId,
        string deviceId,
        MobileAppMetadataDto app,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var clientItemKey = AppClientItemKey(app);
        var validation = ValidateApp(app);
        if (validation is not null)
            return Rejected(clientItemKey, "app-metadata", validation);

        var entity = _db.Set<MobileAppCatalogEntity>().Local
            .SingleOrDefault(a => a.UserId == userId
                && a.DeviceId == deviceId
                && a.PackageName == app.PackageName)
            ?? await _db.Set<MobileAppCatalogEntity>()
            .SingleOrDefaultAsync(a => a.UserId == userId
                && a.DeviceId == deviceId
                && a.PackageName == app.PackageName, ct);

        if (entity is not null && AppMatches(entity, app))
            return Item(clientItemKey, "app-metadata", "skipped", "duplicate", "Duplicate item.");

        if (entity is null)
        {
            entity = new MobileAppCatalogEntity
            {
                UserId = userId,
                DeviceId = deviceId,
                PackageName = app.PackageName,
                CreatedAt = now
            };
            _db.Set<MobileAppCatalogEntity>().Add(entity);
        }

        entity.DisplayName = app.DisplayName;
        entity.VersionName = app.VersionName;
        entity.VersionCode = app.VersionCode;
        entity.IsSystemApp = app.IsSystemApp;
        entity.Category = app.Category;
        entity.InstallerPackage = app.InstallerPackage;
        entity.FirstInstallTimeUtc = app.FirstInstallTimeUtc;
        entity.LastUpdateTimeUtc = app.LastUpdateTimeUtc;
        entity.RawJson = JsonOrDefault(app.RawJson);
        entity.UpdatedAt = now;
        return Item(clientItemKey, "app-metadata", "accepted", "accepted", "Accepted.");
    }

    private async Task<IReadOnlyList<MobileIngestItemResult>> AddEventsIfMissingAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (request.Events.Count == 0)
            return [];

        var validEvents = request.Events
            .Where(usageEvent => ValidateEvent(usageEvent) is null)
            .ToList();
        var knownKeys = new HashSet<EventKey>();

        if (validEvents.Count > 0)
        {
            const int chunkSize = 500;
            for (var offset = 0; offset < validEvents.Count; offset += chunkSize)
            {
                var chunk = validEvents.Skip(offset).Take(chunkSize).ToList();
                var chunkFirst = chunk.Min(e => e.EventTimestampUtc);
                var chunkLast = chunk.Max(e => e.EventTimestampUtc);
                var chunkPackages = chunk.Select(e => e.PackageName).Distinct(StringComparer.Ordinal).ToArray();
                var existingChunk = await _db.Set<MobileUsageEventEntity>()
                    .AsNoTracking()
                    .Where(e => e.UserId == userId
                        && e.DeviceId == request.DeviceId
                        && e.EventTimestampUtc >= chunkFirst
                        && e.EventTimestampUtc <= chunkLast
                        && chunkPackages.Contains(e.PackageName))
                    .Select(e => new { e.PackageName, e.EventType, e.EventTimestampUtc, e.ClassName })
                    .ToListAsync(ct);
                foreach (var e in existingChunk)
                    knownKeys.Add(new EventKey(e.PackageName, e.EventType, e.EventTimestampUtc.ToUniversalTime(), NormalizeClassName(e.ClassName)));
            }
        }

        var results = new List<MobileIngestItemResult>(request.Events.Count);
        foreach (var usageEvent in request.Events)
        {
            var clientItemKey = EventClientItemKey(usageEvent);
            var validation = ValidateEvent(usageEvent);
            if (validation is not null)
            {
                results.Add(Rejected(clientItemKey, "usage-event", validation));
                continue;
            }

            var key = EventKey.From(usageEvent);
            if (!knownKeys.Add(key))
            {
                results.Add(Item(
                    clientItemKey,
                    "usage-event",
                    "skipped",
                    "duplicate",
                    "Duplicate item."));
                continue;
            }

            _db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
            {
                UserId = userId,
                DeviceId = request.DeviceId,
                PackageName = usageEvent.PackageName,
                EventType = usageEvent.EventType,
                EventTimestampUtc = usageEvent.EventTimestampUtc,
                ClassName = NormalizeClassName(usageEvent.ClassName),
                SourceWindowStartUtc = request.WindowStartUtc,
                SourceWindowEndUtc = request.WindowEndUtc,
                CollectedAtUtc = usageEvent.CollectedAtUtc,
                RawJson = JsonOrDefault(usageEvent.RawJson),
                QualityFlagsJson = "[]",
                CreatedAt = now
            });
            results.Add(Item(
                clientItemKey,
                "usage-event",
                "accepted",
                "accepted",
                "Accepted."));
        }

        return results;
    }

    private async Task<MobileIngestItemResult> UpsertSummaryAsync(
        Guid userId,
        string deviceId,
        MobileUsageSummaryDto summary,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var clientItemKey = SummaryClientItemKey(summary);
        var validation = ValidateSummary(summary);
        if (validation is not null)
            return Rejected(clientItemKey, "usage-summary", validation);

        // 零时长 = 该包在这个窗口内没有前台使用，是合法输入而不是非法数据（#240）：
        // 整条拒绝会让汇总覆盖率从"全部包"退化为"有使用的包"，还会把批次打成异常。
        if (summary.TotalTimeVisibleMs <= 0)
            return Item(
                clientItemKey,
                "usage-summary",
                "skipped",
                "no-usage",
                "No foreground usage in this window.");

        var windowMs = (long)(summary.WindowEndUtc - summary.WindowStartUtc).TotalMilliseconds;
        var effectiveMs = Math.Min(summary.TotalTimeVisibleMs, windowMs);
        var qualityFlags = effectiveMs < summary.TotalTimeVisibleMs
            ? DurationClampedFlags
            : EmptyFlags;

        var entity = _db.Set<MobileUsageSummaryEntity>().Local
            .SingleOrDefault(s => s.UserId == userId
                && s.DeviceId == deviceId
                && s.PackageName == summary.PackageName
                && s.WindowStartUtc == summary.WindowStartUtc
                && s.WindowEndUtc == summary.WindowEndUtc
                && s.SourceKind == summary.SourceKind)
            ?? await _db.Set<MobileUsageSummaryEntity>()
            .SingleOrDefaultAsync(s => s.UserId == userId
                && s.DeviceId == deviceId
                && s.PackageName == summary.PackageName
                && s.WindowStartUtc == summary.WindowStartUtc
                && s.WindowEndUtc == summary.WindowEndUtc
                && s.SourceKind == summary.SourceKind, ct);

        if (entity is not null && SummaryMatches(entity, summary, effectiveMs, qualityFlags))
            return Item(clientItemKey, "usage-summary", "skipped", "duplicate", "Duplicate item.");

        if (entity is null)
        {
            entity = new MobileUsageSummaryEntity
            {
                UserId = userId,
                DeviceId = deviceId,
                PackageName = summary.PackageName,
                WindowStartUtc = summary.WindowStartUtc,
                WindowEndUtc = summary.WindowEndUtc,
                SourceKind = summary.SourceKind,
                CreatedAt = now
            };
            _db.Set<MobileUsageSummaryEntity>().Add(entity);
        }

        entity.TotalTimeVisibleMs = effectiveMs;
        entity.LastTimeUsedUtc = summary.LastTimeUsedUtc;
        entity.RawJson = JsonOrDefault(summary.RawJson);
        entity.QualityFlagsJson = qualityFlags;
        entity.UpdatedAt = now;
        return Item(clientItemKey, "usage-summary", "accepted", "accepted", "Accepted.");
    }

    private static string JsonOrDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;

    private static MobileUsageIngestResult BuildResult(
        string batchId,
        IReadOnlyList<MobileIngestItemResult> itemResults)
        => new(
            batchId,
            itemResults.Count(item => item.Outcome == "accepted"),
            itemResults.Count(item => item.Outcome == "skipped"),
            itemResults.Count(item => item.Outcome == "rejected"),
            itemResults.Count(item => item.Outcome == "failed"),
            itemResults);

    private static MobileUsageIngestResult BuildPersistedResult(MobileSyncBatchEntity batch)
    {
        if (MobileSyncBatchEnvelopeCodec.TryDeserialize(batch.ErrorJson, out var envelope)
            && envelope.ItemResults.Count > 0)
        {
            return BuildResult(batch.BatchId, envelope.ItemResults);
        }

        return new MobileUsageIngestResult(
            batch.BatchId,
            batch.AcceptedCount,
            batch.SkippedCount,
            batch.RejectedCount,
            batch.FailedCount,
            []);
    }

    internal static void ValidateRequestBudget(MobileUsageEventsUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Length > 128)
            throw new MobileSyncBatchRequestValidationException("同步设备标识无效");
        if (string.IsNullOrWhiteSpace(request.BatchId) || request.BatchId.Length > 128)
            throw new MobileSyncBatchRequestValidationException("同步批次标识无效");
        if (request.WindowEndUtc <= request.WindowStartUtc)
            throw new MobileSyncBatchRequestValidationException("同步窗口结束时间必须晚于开始时间");
        if (request.WindowEndUtc - request.WindowStartUtc > MaxUploadWindow)
        {
            throw new MobileSyncBatchRequestValidationException(
                $"同步窗口不得超过 {MaxUploadWindow.TotalHours} 小时；请分窗后重试，本地记录不得截断");
        }
        if (RequestItemCount(request) > MaxUploadItems)
        {
            throw new MobileSyncBatchRequestValidationException(
                $"单次同步条目不得超过 {MaxUploadItems} 条；请分批后重试，本地记录不得丢弃");
        }
        if (request.Events.Any(usageEvent =>
                usageEvent.EventTimestampUtc < request.WindowStartUtc
                || usageEvent.EventTimestampUtc >= request.WindowEndUtc))
        {
            throw new MobileSyncBatchRequestValidationException(
                "使用事件时间必须落在声明同步窗口内；请按真实时间分窗后重试，本地记录不得截断");
        }
        if (request.Summaries.Any(summary =>
                summary.WindowStartUtc < request.WindowStartUtc
                || summary.WindowEndUtc > request.WindowEndUtc))
        {
            throw new MobileSyncBatchRequestValidationException(
                "汇总窗口必须完整落在声明同步窗口内；请按真实跨度分窗后重试，本地记录不得截断");
        }
    }

    private static int RequestItemCount(MobileUsageEventsUploadRequest request)
        => checked(request.Apps.Count + request.Events.Count + request.Summaries.Count);

    private static TimeSpan RetryDelay(int attemptCount)
        => TimeSpan.FromMinutes(1 << Math.Clamp(attemptCount - 1, 0, 3));

    private static string ClassifyFailure(Exception exception)
    {
        var root = exception.GetBaseException();
        return root switch
        {
            TimeoutException => "processing-timeout",
            DbUpdateException when root.InnerException is Npgsql.PostgresException postgres
                => $"persistence-{postgres.SqlState}",
            Npgsql.PostgresException postgres => $"persistence-{postgres.SqlState}",
            OperationCanceledException => "processing-cancelled",
            _ => $"processing-{root.GetType().Name}"
        };
    }

    internal static string ComputePayloadSha256(MobileUsageEventsUploadRequest request)
    {
        var canonical = new
        {
            deviceId = request.DeviceId,
            clientBatchId = request.BatchId,
            sourceWindowStartUtc = UtcKey(request.WindowStartUtc),
            sourceWindowEndUtc = UtcKey(request.WindowEndUtc),
            apps = request.Apps
                .OrderBy(app => app.PackageName, StringComparer.Ordinal)
                .ThenBy(app => app.VersionCode)
                .ThenBy(app => app.VersionName, StringComparer.Ordinal)
                .ThenBy(app => app.ClientItemKey, StringComparer.Ordinal)
                .Select(app => new
                {
                    packageName = app.PackageName,
                    displayName = app.DisplayName,
                    versionName = app.VersionName,
                    versionCode = app.VersionCode,
                    isSystemApp = app.IsSystemApp,
                    categoryName = app.CategoryName,
                    installerPackageName = app.InstallerPackageName,
                    firstInstallTimeUtc = UtcKey(app.FirstInstallTimeUtc),
                    lastUpdateTimeUtc = UtcKey(app.LastUpdateTimeUtc),
                    rawJson = app.RawJson,
                    clientItemKey = app.ClientItemKey
                }),
            events = request.Events
                .OrderBy(item => item.PackageName, StringComparer.Ordinal)
                .ThenBy(item => item.EventTimestampUtc)
                .ThenBy(item => item.EventType, StringComparer.Ordinal)
                .ThenBy(item => item.ClassName, StringComparer.Ordinal)
                .ThenBy(item => item.CollectedAtUtc)
                .ThenBy(item => item.RawJson, StringComparer.Ordinal)
                .ThenBy(item => item.ClientItemKey, StringComparer.Ordinal)
                .Select(item => new
                {
                    packageName = item.PackageName,
                    eventType = item.EventType,
                    eventTimestampUtc = UtcKey(item.EventTimestampUtc),
                    className = item.ClassName,
                    collectedAtUtc = UtcKey(item.CollectedAtUtc),
                    rawJson = item.RawJson,
                    clientItemKey = item.ClientItemKey
                }),
            summaries = request.Summaries
                .OrderBy(item => item.PackageName, StringComparer.Ordinal)
                .ThenBy(item => item.WindowStartUtc)
                .ThenBy(item => item.WindowEndUtc)
                .ThenBy(item => item.SourceKind, StringComparer.Ordinal)
                .ThenBy(item => item.ClientItemKey, StringComparer.Ordinal)
                .Select(item => new
                {
                    packageName = item.PackageName,
                    windowStartUtc = UtcKey(item.WindowStartUtc),
                    windowEndUtc = UtcKey(item.WindowEndUtc),
                    totalTimeForegroundMs = item.TotalTimeForegroundMs,
                    lastTimeUsedUtc = UtcKey(item.LastTimeUsedUtc),
                    sourceKind = item.SourceKind,
                    rawJson = item.RawJson,
                    clientItemKey = item.ClientItemKey
                })
        };

        return Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)))
            .ToLowerInvariant();
    }

    private static async Task RollbackAndDisposeAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null)
            return;

        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    private static MobileIngestItemResult Item(
        string clientItemKey,
        string entityType,
        string outcome,
        string code,
        string message)
        => new(clientItemKey, entityType, outcome, code, message);

    /// <summary>
    /// 窗口内是否存在"按更早窗口末端封口、且会被本窗口延长"的开放会话。
    /// 这类会话只由窗口边界决定，事件集不变也会变，因此不能跳过重建。
    ///
    /// 注意：quality_flags_json 是 jsonb 列，字符串匹配必须在内存里做
    /// （服务端会生成 `jsonb ~~ unknown` 而报 42883）。
    /// </summary>
    private async Task<bool> HasExtendableOpenSessionAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        CancellationToken ct)
    {
        var candidates = await _db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(session => session.UserId == userId
                && session.DeviceId == request.DeviceId
                && session.StartUtc <= request.WindowEndUtc
                && session.EndUtc != null
                && session.EndUtc >= request.WindowStartUtc
                && session.EndUtc < request.WindowEndUtc)
            .Select(session => session.QualityFlagsJson)
            .ToListAsync(ct);

        return candidates.Any(flags => flags.Contains("open-ended", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 本批是否写入了新的使用事件。会话只由事件派生，因此没有新事件时无需重建会话（#248）。
    /// </summary>
    private static bool HasNewUsageEvents(MobileUsageIngestResult result)
        => result.ItemResults.Any(item =>
            string.Equals(item.EntityType, "usage-event", StringComparison.Ordinal)
            && string.Equals(item.Outcome, "accepted", StringComparison.Ordinal));

    private static MobileIngestItemResult Rejected(
        string clientItemKey,
        string entityType,
        ValidationError validation)
        => Item(clientItemKey, entityType, "rejected", validation.Code, validation.Message);

    private static string AppClientItemKey(MobileAppMetadataDto app)
        => ClientItemKey(
            app.ClientItemKey,
            $"{app.PackageName}@{app.VersionCode.ToString(CultureInfo.InvariantCulture)}");

    private static string EventClientItemKey(MobileUsageEventDto usageEvent)
        => ClientItemKey(
            usageEvent.ClientItemKey,
            $"event:{NaturalKeyHash(
                usageEvent.PackageName,
                usageEvent.EventType,
                UtcKey(usageEvent.EventTimestampUtc),
                NormalizeClassName(usageEvent.ClassName))}");

    private static string SummaryClientItemKey(MobileUsageSummaryDto summary)
        => ClientItemKey(
            summary.ClientItemKey,
            $"summary:{NaturalKeyHash(
                summary.PackageName,
                UtcKey(summary.WindowStartUtc),
                UtcKey(summary.WindowEndUtc),
                summary.SourceKind)}");

    private static string ClientItemKey(string? clientItemKey, string fallback)
        => string.IsNullOrWhiteSpace(clientItemKey) ? fallback : clientItemKey;

    private static string NaturalKeyHash(params string[] parts)
    {
        var canonical = new StringBuilder();
        foreach (var part in parts)
            canonical.Append(part.Length).Append(':').Append(part);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string UtcKey(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? UtcKey(DateTimeOffset? value)
        => value is null ? null : UtcKey(value.Value);

    private static ValidationError? ValidateApp(MobileAppMetadataDto app)
    {
        var packageValidation = ValidatePackageName(app.PackageName);
        if (packageValidation is not null)
            return packageValidation;
        if (string.IsNullOrWhiteSpace(app.DisplayName) || app.DisplayName.Length > 256)
            return new ValidationError("invalid-display-name", "Display name is required and must not exceed 256 characters.");
        if (app.VersionName?.Length > 128)
            return new ValidationError("invalid-version-name", "Version name must not exceed 128 characters.");
        if (app.VersionCode < 0)
            return new ValidationError("invalid-version-code", "Version code must not be negative.");
        if (app.CategoryName?.Length > 128)
            return new ValidationError("invalid-category-name", "Category name must not exceed 128 characters.");
        if (app.InstallerPackageName?.Length > 256)
            return new ValidationError(
                "invalid-installer-package",
                "Installer package name must not exceed 256 characters.");
        if (app.FirstInstallTimeUtc is not null
            && app.LastUpdateTimeUtc is not null
            && app.LastUpdateTimeUtc < app.FirstInstallTimeUtc)
            return new ValidationError("invalid-time", "Last update time must not precede first install time.");
        return ValidateJson(app.RawJson);
    }

    private static ValidationError? ValidateEvent(MobileUsageEventDto usageEvent)
    {
        var packageValidation = ValidatePackageName(usageEvent.PackageName);
        if (packageValidation is not null)
            return packageValidation;
        if (string.IsNullOrWhiteSpace(usageEvent.EventType) || usageEvent.EventType.Length > 64)
            return new ValidationError("invalid-event-type", "Event type is required and must not exceed 64 characters.");
        if (usageEvent.ClassName?.Length > 512)
            return new ValidationError("invalid-class-name", "Class name must not exceed 512 characters.");
        if (usageEvent.EventTimestampUtc == default || usageEvent.CollectedAtUtc == default)
            return new ValidationError("invalid-time", "Event and collection times are required.");
        return ValidateJson(usageEvent.RawJson);
    }

    private static ValidationError? ValidateSummary(MobileUsageSummaryDto summary)
    {
        var packageValidation = ValidatePackageName(summary.PackageName);
        if (packageValidation is not null)
            return packageValidation;
        if (summary.WindowStartUtc == default
            || summary.WindowEndUtc == default
            || summary.WindowEndUtc <= summary.WindowStartUtc)
            return new ValidationError("invalid-time", "Summary window end must follow its start.");
        if (summary.TotalTimeVisibleMs < 0)
            return new ValidationError("invalid-duration", "Foreground duration must not be negative.");
        if (string.IsNullOrWhiteSpace(summary.SourceKind) || summary.SourceKind.Length > 64)
            return new ValidationError("invalid-source-kind", "Source kind is required and must not exceed 64 characters.");
        return ValidateJson(summary.RawJson);
    }

    /// <summary>
    /// 批次级错误摘要（#243）：让"批次失败/有条目被拒"有可读原因，
    /// 而不是让调用方去展开上千条 ItemResults。
    /// </summary>
    private static List<string> BuildBatchErrors(IReadOnlyList<MobileIngestItemResult> itemResults)
    {
        var errors = new List<string>();

        var failed = itemResults.Where(item => item.Outcome == "failed").ToList();
        if (failed.Count > 0)
        {
            var reasons = failed
                .Select(item => item.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .Take(3);
            errors.Add($"failed: {failed.Count} item(s). {string.Join(" ", reasons)}".TrimEnd());
        }

        var rejectedGroups = itemResults
            .Where(item => item.Outcome == "rejected")
            .GroupBy(item => new { item.EntityType, item.Code })
            .OrderByDescending(group => group.Count());
        foreach (var group in rejectedGroups)
        {
            var sample = group
                .Select(item => item.Message)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            errors.Add(
                $"rejected: {group.Count()} {group.Key.EntityType} item(s) [{group.Key.Code}] {sample}".TrimEnd());
        }

        return errors;
    }

    private static ValidationError? ValidatePackageName(string packageName)
        => string.IsNullOrWhiteSpace(packageName) || packageName.Length > 256
            ? new ValidationError(
                "invalid-package-name",
                "Package name is required and must not exceed 256 characters.")
            : null;

    private static ValidationError? ValidateJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            using var _ = JsonDocument.Parse(value);
            return null;
        }
        catch (JsonException)
        {
            return new ValidationError("invalid-json", "Raw JSON must contain valid JSON.");
        }
    }

    private static bool AppMatches(MobileAppCatalogEntity entity, MobileAppMetadataDto app)
        => entity.DisplayName == app.DisplayName
            && entity.VersionName == app.VersionName
            && entity.VersionCode == app.VersionCode
            && entity.IsSystemApp == app.IsSystemApp
            && entity.Category == app.Category
            && entity.InstallerPackage == app.InstallerPackage
            && entity.FirstInstallTimeUtc == app.FirstInstallTimeUtc
            && entity.LastUpdateTimeUtc == app.LastUpdateTimeUtc
            && entity.RawJson == JsonOrDefault(app.RawJson);

    private static bool SummaryMatches(
        MobileUsageSummaryEntity entity,
        MobileUsageSummaryDto summary,
        long effectiveMs,
        string qualityFlags)
        => entity.TotalTimeVisibleMs == effectiveMs
            && entity.LastTimeUsedUtc == summary.LastTimeUsedUtc
            && entity.RawJson == JsonOrDefault(summary.RawJson)
            && entity.QualityFlagsJson == qualityFlags;

    private async Task MarkAffectedAnalyticsStaleAsync(
        MobileUsageEventsUploadRequest request,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct)
    {
        if (_catalogOverrideService is null)
            return;

        var packageNames = request.Events
            .Select(e => e.PackageName)
            .Concat(request.Summaries.Select(s => s.PackageName))
            .Concat(request.Apps.Select(a => a.PackageName))
            .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var packageName in packageNames)
            await _catalogOverrideService.MarkAnalyticsStaleAsync(packageName, rangeStartUtc, rangeEndUtc, ct);
    }

    private static string NormalizeClassName(string? value)
        => value ?? string.Empty;

    private sealed record EventKey(
        string PackageName,
        string EventType,
        DateTimeOffset EventTimestampUtc,
        string ClassName)
    {
        public static EventKey From(MobileUsageEventDto usageEvent)
            => new(
                usageEvent.PackageName,
                usageEvent.EventType,
                usageEvent.EventTimestampUtc.ToUniversalTime(),
                NormalizeClassName(usageEvent.ClassName));
    }

    private sealed record ValidationError(string Code, string Message);
}

public sealed class MobileSyncBatchRequestValidationException : DomainException
{
    public MobileSyncBatchRequestValidationException(string message)
        : base(40001, message)
    {
    }
}

public sealed class MobileSyncBatchLeaseLostException : DomainException
{
    public MobileSyncBatchLeaseLostException()
        : base(MobileSyncErrorCodes.LeaseLost, "同步批次已由另一请求接管；旧处理结果未提交")
    {
    }
}

public static class MobileSyncErrorCodes
{
    public const int PayloadConflict = 40902;
    public const int RetryExhausted = 40903;
    public const int LeaseLost = 40904;
}
