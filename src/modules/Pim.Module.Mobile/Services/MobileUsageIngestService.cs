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
        return await strategy.ExecuteAsync(
            token => IngestAttemptAsync(userId, request, token),
            ct);
    }

    private async Task<MobileUsageIngestResult> IngestAttemptAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var existingBatch = await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.UserId == userId
                && b.DeviceId == request.DeviceId
                && b.BatchId == request.BatchId, ct);

        if (existingBatch is not null)
            return BuildPersistedResult(existingBatch);

        IDbContextTransaction? transaction = null;
        try
        {
            if (_db.Database.IsRelational())
                transaction = await _db.Database.BeginTransactionAsync(ct);

            var now = _timeProvider.GetUtcNow();
            var itemResults = new List<MobileIngestItemResult>();
            var batchErrors = new List<string>();
            var batch = new MobileSyncBatchEntity
            {
                UserId = userId,
                DeviceId = request.DeviceId,
                BatchId = request.BatchId,
                WindowStartUtc = request.WindowStartUtc,
                WindowEndUtc = request.WindowEndUtc,
                AcceptedCount = 0,
                FailedCount = 0,
                Status = "completed",
                CreatedAt = now,
                CompletedAtUtc = now
            };

            foreach (var app in request.Apps)
                itemResults.Add(await UpsertAppAsync(userId, request.DeviceId, app, now, ct));

            itemResults.AddRange(await AddEventsIfMissingAsync(userId, request, now, ct));

            foreach (var summary in request.Summaries)
                itemResults.Add(await UpsertSummaryAsync(userId, request.DeviceId, summary, now, ct));

            var result = BuildResult(batch.BatchId, itemResults);
            batch.AcceptedCount = result.ItemResults.Count(item =>
                item.EntityType == "usage-event" && item.Outcome == "accepted");
            batch.FailedCount = result.FailedCount;
            batch.Status = result.RejectedCount > 0 || result.FailedCount > 0
                ? "completed-with-errors"
                : "completed";
            batch.ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                result.ItemResults,
                batchErrors);

            await _db.SaveChangesAsync(ct);
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

            _db.Set<MobileSyncBatchEntity>().Add(batch);
            await _db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return result;
        }
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            await RollbackAndDisposeAsync(transaction);
            transaction = null;
            _db.ChangeTracker.Clear();

            var persistedWinner = await _db.Set<MobileSyncBatchEntity>()
                .AsNoTracking()
                .SingleOrDefaultAsync(b => b.UserId == userId
                    && b.DeviceId == request.DeviceId
                    && b.BatchId == request.BatchId, ct);
            if (persistedWinner is null)
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
            var firstEventAt = validEvents.Min(e => e.EventTimestampUtc);
            var lastEventAt = validEvents.Max(e => e.EventTimestampUtc);
            var packageNames = validEvents
                .Select(e => e.PackageName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var existingQuery = _db.Set<MobileUsageEventEntity>()
                .AsNoTracking()
                .Where(e => e.UserId == userId
                    && e.DeviceId == request.DeviceId
                    && e.EventTimestampUtc >= firstEventAt
                    && e.EventTimestampUtc <= lastEventAt
                    && packageNames.Contains(e.PackageName));

            knownKeys = (await existingQuery
                    .Select(e => new
                    {
                        e.PackageName,
                        e.EventType,
                        e.EventTimestampUtc,
                        e.ClassName
                    })
                    .ToListAsync(ct))
                .Select(e => new EventKey(
                    e.PackageName,
                    e.EventType,
                    e.EventTimestampUtc,
                    NormalizeClassName(e.ClassName)))
                .ToHashSet();
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

        if (entity is not null && SummaryMatches(entity, summary))
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

        entity.TotalTimeVisibleMs = summary.TotalTimeVisibleMs;
        entity.LastTimeUsedUtc = summary.LastTimeUsedUtc;
        entity.RawJson = JsonOrDefault(summary.RawJson);
        entity.QualityFlagsJson = "[]";
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
                0,
                0,
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
        if (summary.TotalTimeForegroundMs < 0)
            return new ValidationError("invalid-duration", "Foreground duration must not be negative.");
        if (string.IsNullOrWhiteSpace(summary.SourceKind) || summary.SourceKind.Length > 64)
            return new ValidationError("invalid-source-kind", "Source kind is required and must not exceed 64 characters.");
        return ValidateJson(summary.RawJson);
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

    private static bool SummaryMatches(MobileUsageSummaryEntity entity, MobileUsageSummaryDto summary)
        => entity.TotalTimeVisibleMs == summary.TotalTimeVisibleMs
            && entity.LastTimeUsedUtc == summary.LastTimeUsedUtc
            && entity.RawJson == JsonOrDefault(summary.RawJson)
            && entity.QualityFlagsJson == "[]";

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
                usageEvent.EventTimestampUtc,
                NormalizeClassName(usageEvent.ClassName));
    }

    private sealed record ValidationError(string Code, string Message);
}
