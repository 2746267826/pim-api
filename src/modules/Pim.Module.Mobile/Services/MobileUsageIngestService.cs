using Microsoft.EntityFrameworkCore;
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

    public MobileUsageIngestService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileSessionInterpreter sessionInterpreter,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _sessionInterpreter = sessionInterpreter;
        _timeProvider = timeProvider;
    }

    public async Task<MobileUsageIngestResult> IngestAsync(
        MobileUsageEventsUploadRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var existingBatch = await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(b => b.UserId == userId
                && b.DeviceId == request.DeviceId
                && b.BatchId == request.BatchId, ct);

        if (existingBatch is not null)
        {
            await _sessionInterpreter.RebuildSessionsAsync(
                userId,
                request.DeviceId,
                existingBatch.WindowStartUtc,
                existingBatch.WindowEndUtc,
                ct);
            return new MobileUsageIngestResult(existingBatch.BatchId, existingBatch.AcceptedCount, existingBatch.FailedCount);
        }

        var now = _timeProvider.GetUtcNow();
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
        _db.Set<MobileSyncBatchEntity>().Add(batch);

        foreach (var app in request.Apps)
            await UpsertAppAsync(userId, request.DeviceId, app, now, ct);

        var eventCounts = await AddEventsIfMissingAsync(userId, request, now, ct);
        batch.AcceptedCount = eventCounts.Accepted;

        foreach (var summary in request.Summaries)
            await UpsertSummaryAsync(userId, request.DeviceId, summary, now, ct);

        await _db.SaveChangesAsync(ct);
        await _sessionInterpreter.RebuildSessionsAsync(
            userId,
            request.DeviceId,
            request.WindowStartUtc,
            request.WindowEndUtc,
            ct);

        return new MobileUsageIngestResult(
            batch.BatchId,
            eventCounts.Accepted,
            eventCounts.Skipped,
            0,
            batch.FailedCount);
    }

    private async Task UpsertAppAsync(
        Guid userId,
        string deviceId,
        MobileAppMetadataDto app,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var entity = await _db.Set<MobileAppCatalogEntity>()
            .SingleOrDefaultAsync(a => a.UserId == userId
                && a.DeviceId == deviceId
                && a.PackageName == app.PackageName, ct);

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
    }

    private async Task<EventIngestCounts> AddEventsIfMissingAsync(
        Guid userId,
        MobileUsageEventsUploadRequest request,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (request.Events.Count == 0)
            return new EventIngestCounts(0, 0);

        var firstEventAt = request.Events.Min(e => e.EventTimestampUtc);
        var lastEventAt = request.Events.Max(e => e.EventTimestampUtc);
        var packageNames = request.Events
            .Select(e => e.PackageName)
            .Where(packageName => !string.IsNullOrWhiteSpace(packageName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var existingQuery = _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && e.DeviceId == request.DeviceId
                && e.EventTimestampUtc >= firstEventAt
                && e.EventTimestampUtc <= lastEventAt);
        if (packageNames.Length > 0)
            existingQuery = existingQuery.Where(e => packageNames.Contains(e.PackageName));

        var knownKeys = (await existingQuery
                .Select(e => new
                {
                    e.PackageName,
                    e.EventType,
                    e.EventTimestampUtc,
                    e.ClassName
                })
                .ToListAsync(ct))
            .Select(e => new EventKey(e.PackageName, e.EventType, e.EventTimestampUtc, NormalizeClassName(e.ClassName)))
            .ToHashSet();

        var accepted = 0;
        var skipped = 0;
        foreach (var usageEvent in request.Events)
        {
            var key = EventKey.From(usageEvent);
            if (!knownKeys.Add(key))
            {
                skipped++;
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
            accepted++;
        }

        return new EventIngestCounts(accepted, skipped);
    }

    private async Task UpsertSummaryAsync(
        Guid userId,
        string deviceId,
        MobileUsageSummaryDto summary,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var entity = await _db.Set<MobileUsageSummaryEntity>()
            .SingleOrDefaultAsync(s => s.UserId == userId
                && s.DeviceId == deviceId
                && s.PackageName == summary.PackageName
                && s.WindowStartUtc == summary.WindowStartUtc
                && s.WindowEndUtc == summary.WindowEndUtc
                && s.SourceKind == summary.SourceKind, ct);

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
    }

    private static string JsonOrDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;

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

    private sealed record EventIngestCounts(int Accepted, int Skipped);
}
