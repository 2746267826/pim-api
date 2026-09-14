using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileUsageIngestService
{
    /// <summary>
    /// 允许"接管"未完成批次的租约时长：超过它说明上一个处理者已经不在了（进程崩溃 / 请求中断），
    /// 而不是并发重投。租约内同一 batchId 的重投直接回放已持久化结果（#243）。
    /// </summary>
    private static readonly TimeSpan PendingBatchLease = TimeSpan.FromMinutes(2);

    private const long MaxSummaryWindowMs = 8L * 60 * 60 * 1000;

    /// <summary>汇总时长超过窗口（或超过 8h 上限）时按窗口裁剪入库，并打上该标记（#240 / INV-M16）。</summary>
    private const string DurationClampedFlags = "[\"duration-clamped\"]";
    private const string EmptyFlags = "[]";

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileSessionInterpreter _sessionInterpreter;
    private readonly TimeProvider _timeProvider;
    private readonly MobileAppCatalogOverrideService? _catalogOverrideService;

    public MobileUsageIngestService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileSessionInterpreter sessionInterpreter,
        TimeProvider timeProvider,
        MobileAppCatalogOverrideService? catalogOverrideService = null)
    {
        _db = db;
        _currentUser = currentUser;
        _sessionInterpreter = sessionInterpreter;
        _timeProvider = timeProvider;
        _catalogOverrideService = catalogOverrideService;
    }

    public async Task<MobileUsageIngestResult> IngestAsync(
        MobileUsageEventsUploadRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var strategy = _db.Database.CreateExecutionStrategy();
        var attemptState = new IngestAttemptState();
        return await strategy.ExecuteAsync(
            token => IngestAttemptAsync(userId, request, attemptState, token),
            ct);
    }

    private async Task<MobileUsageIngestResult> IngestAttemptAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        IngestAttemptState attemptState,
        CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var existingBatch = await FindBatchAsync(userId, request, ct);

        if (existingBatch is not null && MobileSyncBatchStatus.IsTerminal(existingBatch.Status))
            return BuildPersistedResult(existingBatch);

        var now = _timeProvider.GetUtcNow();
        MobileSyncBatchEntity batch;
        if (existingBatch is null)
        {
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
                ErrorJson = "{}",
                CreatedAt = now,
                CompletedAtUtc = null
            };
            _db.Set<MobileSyncBatchEntity>().Add(batch);
            // 在写库之前就认领：执行策略重试时它才知道这条 pending 批次是自己的，可以接管重跑。
            attemptState.OwnedPendingBatchId = batch.Id;

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

                return BuildPersistedResult(winner);
            }
        }
        else if (existingBatch.Id == attemptState.OwnedPendingBatchId)
        {
            // 本次请求自己的重试（执行策略重跑）：直接接管。
            batch = existingBatch;
        }
        else if (await TryClaimStalePendingBatchAsync(existingBatch, now, ct) is { } claimed)
        {
            // 上一个处理者已经消失（租约过期），原子认领后幂等重跑。
            batch = claimed;
        }
        else
        {
            // 同一 batchId 仍在处理中：不重复处理，回放当前已持久化的结果。
            return BuildPersistedResult(existingBatch);
        }

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

            // 派生工作（会话重建 / 派生表标记）先跑，成功之后才把批次推进到终态：
            // 中途失败时批次保持 pending（"上传中断"信号），而不是先宣告完成再回滚（#243）。
            await _sessionInterpreter.RebuildSessionsAsync(
                userId,
                request.DeviceId,
                request.WindowStartUtc,
                request.WindowEndUtc,
                ct);
            await MarkAffectedAnalyticsStaleAsync(
                request,
                request.WindowStartUtc,
                request.WindowEndUtc,
                ct);

            // accepted_count 反映该批全部被接受的条目（事件 / 元数据 / 汇总），
            // 而不是只有 usage-event —— 否则 2298 个批次显示 accepted_count = 0（#243）。
            batch.AcceptedCount = result.AcceptedCount;
            batch.RejectedCount = result.RejectedCount;
            batch.SkippedCount = result.SkippedCount;
            batch.FailedCount = result.FailedCount;
            batch.Status = result.FailedCount > 0
                ? MobileSyncBatchStatus.Failed
                : MobileSyncBatchStatus.Completed;
            batch.CompletedAtUtc = now;
            batch.ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                result.ItemResults,
                BuildBatchErrors(result.ItemResults));

            await _db.SaveChangesAsync(ct);
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

            return BuildPersistedResult(persistedWinner);
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
    /// 原子认领一条"上一个处理者已经消失"的 pending 批次：把租约刷新到现在。
    /// 只有刷新成功（受影响行数 = 1）的请求才会继续处理，因此两个并发重投不会同时重跑同一批；
    /// 刷新也顺带让积压巡检与设备删除守卫看到"这次尝试是什么时候开始的"。
    /// 仍在租约内（或已被别的请求认领）时返回 null。
    /// </summary>
    private async Task<MobileSyncBatchEntity?> TryClaimStalePendingBatchAsync(
        MobileSyncBatchEntity batch,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var cutoff = now - PendingBatchLease;
        if (batch.CreatedAt > cutoff)
            return null;

        if (!_db.Database.IsRelational())
        {
            // 内存库没有并发写入，刷新租约即可。
            batch.CreatedAt = now;
            await _db.SaveChangesAsync(ct);
            return batch;
        }

        FormattableString claim = $@"
            UPDATE mobile_sync_batches
               SET created_at = {now}
             WHERE id = {batch.Id}
               AND status = {MobileSyncBatchStatus.Pending}
               AND created_at <= {cutoff};";
        var claimed = await _db.Database.ExecuteSqlInterpolatedAsync(claim, ct);
        if (claimed == 0)
            return null;

        _db.ChangeTracker.Clear();
        return await _db.Set<MobileSyncBatchEntity>().SingleOrDefaultAsync(b => b.Id == batch.Id, ct);
    }

    private sealed class IngestAttemptState
    {
        /// <summary>本次请求自己插入的 pending 批次 Id（执行策略重试时用于接管）。</summary>
        public Guid? OwnedPendingBatchId { get; set; }
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
        var effectiveMs = Math.Min(summary.TotalTimeVisibleMs, Math.Min(windowMs, MaxSummaryWindowMs));
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
        => MobileSyncBatchEnvelopeCodec.TryDeserialize(batch.ErrorJson, out var envelope)
            ? BuildResult(batch.BatchId, envelope.ItemResults)
            : new MobileUsageIngestResult(
                batch.BatchId,
                batch.AcceptedCount,
                batch.SkippedCount,
                batch.RejectedCount,
                batch.FailedCount,
                []);

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
