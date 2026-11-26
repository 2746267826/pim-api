using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Caching;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// P05 历史位置人工纠正。原始采集事实（设备、时间、精度、provider、速度等）保持不变；
/// 只允许改经纬度、排除/恢复、补点和永久删除，并记录轻量修订与全局审计。
/// </summary>
public sealed class MobileLocationCorrectionService
{
    private const int MaxBatchItems = 500;
    private const int CoordinateScale = 7;
    private const int AccuracyScale = 2;
    private const int MaxReasonLength = 500;
    private const string ResourceType = "mobile-location-point";
    private const string AuditSource = "web";

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileLocationQueryService _queryService;
    private readonly MobileLocationAggregationService _aggregationService;
    private readonly IMobileLocationRecalculationService _recalculationService;
    private readonly IAuditLogService _auditLog;
    private readonly IAggregateResultCache _cache;
    private readonly TimeProvider _timeProvider;

    public MobileLocationCorrectionService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileLocationQueryService queryService,
        MobileLocationAggregationService aggregationService,
        IMobileLocationRecalculationService recalculationService,
        IAuditLogService auditLog,
        IAggregateResultCache cache,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _queryService = queryService;
        _aggregationService = aggregationService;
        _recalculationService = recalculationService;
        _auditLog = auditLog;
        _cache = cache;
        _timeProvider = timeProvider;
    }

    public async Task<MobileManagedLocationPointPageDto> GetPointsAsync(
        MobileManagedLocationQueryRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var context = _queryService.Normalize(new MobileLocationQueryRequest(
            request.RangeStartUtc,
            request.RangeEndUtc,
            request.Timezone,
            request.DeviceId,
            PageSize: request.PageSize,
            Cursor: request.Cursor));

        var cursor = ResolveCursor(context.Cursor);
        var query = _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(point => point.UserId == userId
                && point.RecordedAtUtc >= context.Range.RangeStartUtc
                && point.RecordedAtUtc < context.Range.RangeEndUtc);
        if (!request.IncludeExcluded)
            query = query.Where(point => !point.IsExcluded);
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            query = query.Where(point => point.DeviceId == context.DeviceId);

        if (!request.IncludeExcluded && !string.IsNullOrWhiteSpace(request.SegmentId))
        {
            var boundary = await MobileLocationSegmentBoundaries.ResolveAsync(
                _db,
                userId,
                request.SegmentId.Trim(),
                context,
                ct);
            if (boundary is null)
                return new MobileManagedLocationPointPageDto([], null, false);

            query = MobileLocationSegmentBoundaries.Apply(
                query,
                boundary,
                context.MaxAccuracyMeters,
                context.IncludeRejected);
        }

        if (cursor is { Kind: MobileLocationCursorKind.LegacyPointId } legacy)
        {
            var recordedAtUtc = await _db.Set<MobileLocationPointEntity>()
                .AsNoTracking()
                .Where(point => point.UserId == userId && point.Id == legacy.Id)
                .Select(point => (DateTimeOffset?)point.RecordedAtUtc)
                .SingleOrDefaultAsync(ct);
            if (recordedAtUtc is null)
                throw new DomainException(6208, "分页游标已失效。");
            cursor = legacy with { Kind = MobileLocationCursorKind.Encoded, RecordedAtUtc = recordedAtUtc.Value };
        }

        if (cursor is { Kind: MobileLocationCursorKind.Encoded } encoded)
            query = MobileLocationCursor.Apply(query, encoded);

        var page = await query
            .OrderBy(point => point.RecordedAtUtc)
            .ThenBy(point => point.Id)
            .Take(context.PageSize + 1)
            .ToListAsync(ct);
        var hasMore = page.Count > context.PageSize;
        if (hasMore)
            page.RemoveAt(page.Count - 1);
        var nextCursor = hasMore && page.Count > 0
            ? MobileLocationCursor.Encode(page[^1].RecordedAtUtc, page[^1].Id)
            : null;
        return new MobileManagedLocationPointPageDto(page.Select(MapPoint).ToList(), nextCursor, hasMore);
    }

    public Task<MobileLocationPointMutationResult> CorrectAsync(
        Guid pointId,
        MobileLocationPointCorrectionRequest request,
        CancellationToken ct = default)
        => ExecuteMutationAsync(
            token => CorrectAttemptAsync(pointId, request, token),
            ct);

    private async Task<MobileLocationPointMutationResult> CorrectAttemptAsync(
        Guid pointId,
        MobileLocationPointCorrectionRequest request,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        ValidateCoordinates(request.Latitude, request.Longitude);
        var reason = NormalizeReason(request.Reason);
        var latitude = Coordinate(request.Latitude);
        var longitude = Coordinate(request.Longitude);
        var point = await LoadPointAsync(userId, pointId, ct);
        var now = _timeProvider.GetUtcNow();
        var coordsMatch = point.Latitude == latitude && point.Longitude == longitude;
        var exclusionMatch = request.Excluded is null || request.Excluded.Value == point.IsExcluded;

        // 网络失败后客户端会带旧 revision 重试；如果目标状态已经落库，按幂等成功返回，
        // 而不是把“已成功但丢回执”的请求误报成冲突。
        if (coordsMatch && exclusionMatch)
            return new MobileLocationPointMutationResult(MapPoint(point), point.UpdatedAt ?? now);

        EnsureRevision(point, request.Revision);

        var originalLatitude = point.OriginalLatitude;
        var originalLongitude = point.OriginalLongitude;
        if (originalLatitude is null || originalLongitude is null)
        {
            // 第一次人工纠正前才把当前采集坐标固化为原始值；后续再次纠正仍能恢复到最初采集值。
            originalLatitude = point.Latitude;
            originalLongitude = point.Longitude;
        }

        var coordsChanged = !coordsMatch;
        var excludedChanged = request.Excluded is not null && request.Excluded.Value != point.IsExcluded;
        if (!coordsChanged && !excludedChanged)
            return new MobileLocationPointMutationResult(MapPoint(point), point.UpdatedAt ?? now);

        var revisionBefore = point.Revision;
        var excludedBefore = point.IsExcluded;
        if (coordsChanged)
        {
            point.Latitude = latitude;
            point.Longitude = longitude;
        }
        if (excludedChanged)
            ApplyExclusion(point, request.Excluded!.Value, userId, now);

        point.OriginalLatitude = originalLatitude;
        point.OriginalLongitude = originalLongitude;
        point.Revision++;
        point.UpdatedAt = now;

        _db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = userId,
            PointId = point.Id,
            DeviceId = point.DeviceId,
            Operation = coordsChanged && excludedChanged
                ? (request.Excluded!.Value ? "correct-exclude" : "correct-include")
                : coordsChanged
                    ? "correct"
                    : (request.Excluded!.Value ? "exclude" : "include"),
            RevisionBefore = revisionBefore,
            RevisionAfter = point.Revision,
            OriginalLatitude = originalLatitude,
            OriginalLongitude = originalLongitude,
            NewLatitude = point.Latitude,
            NewLongitude = point.Longitude,
            ExcludedBefore = excludedBefore,
            ExcludedAfter = point.IsExcluded,
            Reason = reason,
            ActorUserId = userId,
            CreatedAt = now
        });
        EnqueueAudit(userId, "mobile.location.correct", point.Id, new Dictionary<string, string>
        {
            ["deviceId"] = point.DeviceId,
            ["revisionBefore"] = revisionBefore.ToString(CultureInfo.InvariantCulture),
            ["revisionAfter"] = point.Revision.ToString(CultureInfo.InvariantCulture),
            ["excluded"] = point.IsExcluded.ToString(CultureInfo.InvariantCulture)
        });

        await SavePointMutationAsync(ct);
        var recomputedAtUtc = await RecalculateAsync(
            [LocalDayScope(point.DeviceId, point.RecordedAtUtc)],
            ct);
        return new MobileLocationPointMutationResult(MapPoint(point), recomputedAtUtc);
    }

    public Task<MobileLocationPointMutationResult> RestoreAsync(
        Guid pointId,
        MobileLocationPointRestoreRequest request,
        CancellationToken ct = default)
        => ExecuteMutationAsync(
            token => RestoreAttemptAsync(pointId, request, token),
            ct);

    private async Task<MobileLocationPointMutationResult> RestoreAttemptAsync(
        Guid pointId,
        MobileLocationPointRestoreRequest request,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var reason = NormalizeReason(request.Reason);
        var point = await LoadPointAsync(userId, pointId, ct);
        if (point.OriginalLatitude is null || point.OriginalLongitude is null)
            throw new DomainException(6206, "这个定位点没有可恢复的原始坐标。");

        var now = _timeProvider.GetUtcNow();
        var targetLatitude = point.OriginalLatitude.Value;
        var targetLongitude = point.OriginalLongitude.Value;
        if (point.Latitude == targetLatitude && point.Longitude == targetLongitude)
            return new MobileLocationPointMutationResult(MapPoint(point), point.UpdatedAt ?? now);

        EnsureRevision(point, request.Revision);

        var revisionBefore = point.Revision;
        point.Latitude = targetLatitude;
        point.Longitude = targetLongitude;
        point.Revision++;
        point.UpdatedAt = now;
        _db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = userId,
            PointId = point.Id,
            DeviceId = point.DeviceId,
            Operation = "restore",
            RevisionBefore = revisionBefore,
            RevisionAfter = point.Revision,
            OriginalLatitude = point.OriginalLatitude,
            OriginalLongitude = point.OriginalLongitude,
            NewLatitude = point.Latitude,
            NewLongitude = point.Longitude,
            ExcludedBefore = point.IsExcluded,
            ExcludedAfter = point.IsExcluded,
            Reason = reason,
            ActorUserId = userId,
            CreatedAt = now
        });
        EnqueueAudit(userId, "mobile.location.restore", point.Id, new Dictionary<string, string>
        {
            ["deviceId"] = point.DeviceId,
            ["revisionBefore"] = revisionBefore.ToString(CultureInfo.InvariantCulture),
            ["revisionAfter"] = point.Revision.ToString(CultureInfo.InvariantCulture)
        });

        await SavePointMutationAsync(ct);
        var recomputedAtUtc = await RecalculateAsync(
            [LocalDayScope(point.DeviceId, point.RecordedAtUtc)],
            ct);
        return new MobileLocationPointMutationResult(MapPoint(point), recomputedAtUtc);
    }

    public Task<MobileLocationBatchMutationResult> SetExclusionAsync(
        MobileLocationPointExclusionRequest request,
        CancellationToken ct = default)
        => ExecuteMutationAsync(
            token => SetExclusionAttemptAsync(request, token),
            ct);

    private async Task<MobileLocationBatchMutationResult> SetExclusionAttemptAsync(
        MobileLocationPointExclusionRequest request,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var reason = NormalizeReason(request.Reason);
        var items = request.Items ?? [];
        if (items.Count == 0)
            throw new DomainException(6208, "请选择至少一个定位点。");
        if (items.Count > MaxBatchItems)
            throw new DomainException(6208, $"一次最多处理 {MaxBatchItems} 个定位点。");
        if (items.Select(item => item.PointId).Distinct().Count() != items.Count)
            throw new DomainException(6208, "定位点不能重复选择。");

        var pointIds = items.Select(item => item.PointId).ToArray();
        var points = await _db.Set<MobileLocationPointEntity>()
            .Where(point => point.UserId == userId && pointIds.Contains(point.Id))
            .ToDictionaryAsync(point => point.Id, ct);
        if (points.Count != pointIds.Length)
            throw new DomainException(40401, "定位点不存在。");

        var now = _timeProvider.GetUtcNow();
        var results = new List<MobileLocationBatchItemResult>(items.Count);
        var changed = new List<MobileLocationPointEntity>();
        foreach (var item in items)
        {
            var point = points[item.PointId];
            if (point.IsExcluded == request.Excluded)
            {
                results.Add(new MobileLocationBatchItemResult(point.Id, "unchanged", "Already in the requested state."));
                continue;
            }

            EnsureRevision(point, item.Revision);
            var revisionBefore = point.Revision;
            var excludedBefore = point.IsExcluded;
            ApplyExclusion(point, request.Excluded, userId, now);
            point.Revision++;
            point.UpdatedAt = now;
            _db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
            {
                UserId = userId,
                PointId = point.Id,
                DeviceId = point.DeviceId,
                Operation = request.Excluded ? "exclude" : "include",
                RevisionBefore = revisionBefore,
                RevisionAfter = point.Revision,
                OriginalLatitude = point.OriginalLatitude,
                OriginalLongitude = point.OriginalLongitude,
                ExcludedBefore = excludedBefore,
                ExcludedAfter = point.IsExcluded,
                Reason = reason,
                ActorUserId = userId,
                CreatedAt = now
            });
            changed.Add(point);
            results.Add(new MobileLocationBatchItemResult(point.Id, "updated", null));
        }

        if (changed.Count > 0)
        {
            EnqueueAudit(userId, request.Excluded ? "mobile.location.exclude" : "mobile.location.include", null,
                new Dictionary<string, string>
                {
                    ["pointIds"] = string.Join(',', changed.Select(point => point.Id)),
                    ["count"] = changed.Count.ToString(CultureInfo.InvariantCulture)
                });
            await SavePointMutationAsync(ct);
            var recomputedAtUtc = await RecalculateAsync(
                changed.Select(point => LocalDayScope(point.DeviceId, point.RecordedAtUtc)).ToList(),
                ct);
            return new MobileLocationBatchMutationResult(changed.Count, results, recomputedAtUtc);
        }

        return new MobileLocationBatchMutationResult(changed.Count, results, now);
    }

    public Task<MobileLocationPointDeleteResult> DeleteAsync(
        Guid pointId,
        int revision,
        CancellationToken ct = default)
        => ExecuteMutationAsync(
            token => DeleteAttemptAsync(pointId, revision, token),
            ct);

    private async Task<MobileLocationPointDeleteResult> DeleteAttemptAsync(
        Guid pointId,
        int revision,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var point = await _db.Set<MobileLocationPointEntity>()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.Id == pointId, ct)
            ?? throw new DomainException(40401, "定位点不存在。");
        var now = _timeProvider.GetUtcNow();
        if (point.DeletedAtUtc is not null)
        {
            // 旧数据可能还留有删除前的修订行；幂等重试也要把它们清干净，
            // 保证任何历史接口都无法从已删除点读回坐标、原因或操作者。
            if (await QueueDeletedPointHistoryPurgeAsync(userId, point.Id, ct))
            {
                await SavePointMutationAsync(ct);
                var purgedAtUtc = await RecalculateAsync(
                    [LocalDayScope(point.DeviceId, point.RecordedAtUtc)],
                    ct);
                return new MobileLocationPointDeleteResult(point.Id, point.DeletedAtUtc.Value, purgedAtUtc);
            }

            return new MobileLocationPointDeleteResult(
                point.Id,
                point.DeletedAtUtc.Value,
                point.UpdatedAt ?? point.DeletedAtUtc.Value);
        }

        EnsureRevision(point, revision);
        var revisionBefore = point.Revision;
        point.DeletedAtUtc = now;
        point.DeletedByUserId = userId;
        point.Revision++;
        point.UpdatedAt = now;
        await QueueDeletedPointHistoryPurgeAsync(userId, point.Id, ct);
        EnqueueAudit(userId, "mobile.location.delete", point.Id, new Dictionary<string, string>
        {
            ["revisionBefore"] = revisionBefore.ToString(CultureInfo.InvariantCulture)
        });

        await SavePointMutationAsync(ct);
        var recomputedAtUtc = await RecalculateAsync(
            [LocalDayScope(point.DeviceId, point.RecordedAtUtc)],
            ct);
        return new MobileLocationPointDeleteResult(point.Id, now, recomputedAtUtc);
    }

    public Task<MobileLocationPointMutationResult> FillAsync(
        MobileLocationFillRequest request,
        CancellationToken ct = default)
        => ExecuteMutationAsync(
            token => FillAttemptAsync(request, token),
            ct);

    private async Task<MobileLocationPointMutationResult> FillAttemptAsync(
        MobileLocationFillRequest request,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        ValidateCoordinates(request.Latitude, request.Longitude);
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            throw new DomainException(6208, "必须指定设备。");
        var deviceId = request.DeviceId.Trim();
        var deviceExists = await _db.Set<MobileDeviceEntity>()
            .AnyAsync(device => device.UserId == userId && device.DeviceId == deviceId, ct);
        if (!deviceExists)
            throw new DomainException(40401, "设备不存在。");

        var accuracy = request.HorizontalAccuracyMeters ?? 25;
        if (!double.IsFinite(accuracy) || accuracy <= 0 || accuracy > MobileLocationQueryService.DefaultMaxAccuracyMeters)
            throw new DomainException(6208, "补点精度必须在 0 到 50 米之间。");

        var reason = NormalizeReason(request.Reason);
        var now = _timeProvider.GetUtcNow();
        var point = new MobileLocationPointEntity
        {
            UserId = userId,
            DeviceId = deviceId,
            RecordedAtUtc = request.RecordedAtUtc.ToUniversalTime(),
            Latitude = Coordinate(request.Latitude),
            Longitude = Coordinate(request.Longitude),
            HorizontalAccuracyMeters = Accuracy(accuracy),
            Provider = "manual",
            Source = "manual-fill",
            Quality = accuracy < MobileLocationQueryService.DefaultMaxAccuracyMeters ? "usable" : "rejected",
            RawJson = JsonSerializer.Serialize(new
            {
                manualFill = true,
                reason,
                createdBy = userId
            }),
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Set<MobileLocationPointEntity>().Add(point);
        _db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = userId,
            PointId = point.Id,
            DeviceId = point.DeviceId,
            Operation = "fill",
            RevisionBefore = 0,
            RevisionAfter = 0,
            NewLatitude = point.Latitude,
            NewLongitude = point.Longitude,
            Reason = reason,
            ActorUserId = userId,
            CreatedAt = now
        });
        EnqueueAudit(userId, "mobile.location.fill", point.Id, new Dictionary<string, string>
        {
            ["deviceId"] = point.DeviceId,
            ["recordedAtUtc"] = point.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["latitude"] = point.Latitude.ToString(CultureInfo.InvariantCulture),
            ["longitude"] = point.Longitude.ToString(CultureInfo.InvariantCulture)
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (!ct.IsCancellationRequested
            && MobileDbErrors.IsUniqueConstraintViolation(ex))
        {
            throw new DomainException(6205, "该设备在这个时刻已经存在相同坐标的定位点。");
        }

        var recomputedAtUtc = await RecalculateAsync(
            [LocalDayScope(point.DeviceId, point.RecordedAtUtc)],
            ct);
        return new MobileLocationPointMutationResult(MapPoint(point), recomputedAtUtc);
    }

    public async Task<MobileLocationSegmentMergeResult> MergeAdjacentSegmentsAsync(
        MobileLocationSegmentMergeRequest request,
        CancellationToken ct = default)
    {
        var normalized = NormalizeSegmentMergeRequest(request);
        try
        {
            return await ExecuteMutationAsync(
                token => MergeAdjacentSegmentsAttemptAsync(normalized, token),
                ct);
        }
        catch (DbUpdateException ex) when (!ct.IsCancellationRequested
            && MobileDbErrors.IsUniqueConstraintViolation(ex))
        {
            // 并发相同请求可能同时通过预检查，唯一索引只允许一个插入。回滚后读回赢家：
            // 相同目标状态按幂等成功返回，不同 Kind 则是明确冲突。
            _db.ChangeTracker.Clear();
            var userId = MobileUserContext.RequireUserId(_currentUser);
            var boundaryDeviceId = await _db.Set<MobileLocationPointEntity>()
                .AsNoTracking()
                .Where(point => point.UserId == userId && point.Id == normalized.BoundaryPointId)
                .Select(point => point.DeviceId)
                .SingleOrDefaultAsync(ct);
            var winner = await FindSegmentMergeAsync(
                userId,
                normalized.BoundaryPointId,
                boundaryDeviceId,
                ct);
            if (winner is not null
                && string.Equals(winner.Kind, normalized.Kind, StringComparison.Ordinal))
            {
                return new MobileLocationSegmentMergeResult(
                    winner.Kind,
                    normalized.BoundaryPointId,
                    request.RangeStartUtc,
                    request.RangeEndUtc,
                    winner.CreatedAt);
            }

            throw new DomainException(6205, "这个分段边界已被其他修改更新，请刷新后重试。");
        }
    }

    private async Task<MobileLocationSegmentMergeResult> MergeAdjacentSegmentsAttemptAsync(
        NormalizedSegmentMerge normalized,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var tracks = await _aggregationService.GetTracksAsync(new MobileLocationQueryRequest(
            normalized.RangeStartUtc,
            normalized.RangeEndUtc,
            normalized.Timezone,
            normalized.DeviceId,
            IncludeRejected: false), ct);

        var boundaryPoint = await _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(point => point.UserId == userId
                && point.Id == normalized.BoundaryPointId, ct)
            ?? throw new DomainException(40401, "轨迹分段不存在或不在同一时间范围内。");
        if (!string.IsNullOrWhiteSpace(normalized.DeviceId)
            && !string.Equals(boundaryPoint.DeviceId, normalized.DeviceId, StringComparison.Ordinal))
        {
            throw new DomainException(40401, "轨迹分段不存在或不在同一时间范围内。");
        }

        var endpointIds = new[] { normalized.FirstPointId, normalized.LastPointId };
        var endpoints = await _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(point => point.UserId == userId && endpointIds.Contains(point.Id))
            .Select(point => new { point.Id, point.DeviceId })
            .ToListAsync(ct);
        if (endpoints.Count != 2
            || endpoints.Any(endpoint =>
                !string.Equals(endpoint.DeviceId, boundaryPoint.DeviceId, StringComparison.Ordinal)))
        {
            throw new DomainException(40401, "轨迹分段不存在或不在同一时间范围内。");
        }

        var existing = await FindSegmentMergeAsync(
            userId,
            normalized.BoundaryPointId,
            boundaryPoint.DeviceId,
            ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.Kind, normalized.Kind, StringComparison.Ordinal))
            {
                throw new DomainException(
                    6205,
                    "这个分段边界已经按其他分段类型合并，请刷新后重试。");
            }

            // 同一目标状态重试：首次合并已生效，分段 ID 已经变化，但边界覆盖仍是同一份。
            return new MobileLocationSegmentMergeResult(
                existing.Kind,
                normalized.BoundaryPointId,
                normalized.RangeStartUtc,
                normalized.RangeEndUtc,
                existing.CreatedAt);
        }

        var segments = tracks.SelectMany(track => track.Segments)
            .Where(segment => normalized.SegmentIds.Contains(segment.Id, StringComparer.Ordinal))
            .OrderBy(segment => segment.StartUtc)
            .ToList();
        if (segments.Count != 2)
        {
            throw new DomainException(6205, "分段已因其他修改发生变化，请刷新后重试。");
        }

        var first = segments[0];
        var second = segments[1];
        if (!string.Equals(first.DeviceId, second.DeviceId, StringComparison.Ordinal)
            || !string.Equals(first.Path[^1].Id, second.Path[0].Id, StringComparison.Ordinal)
            || !Guid.TryParse(first.Path[^1].Id, out var boundaryPointId)
            || boundaryPointId != normalized.BoundaryPointId)
        {
            throw new DomainException(6205, "分段已因其他修改发生变化，请刷新后重试。");
        }
        if (!string.Equals(first.DeviceId, boundaryPoint.DeviceId, StringComparison.Ordinal))
            throw new DomainException(40401, "轨迹分段不存在或不在同一时间范围内。");

        var now = _timeProvider.GetUtcNow();
        _db.Set<MobileLocationSegmentMergeEntity>().Add(new MobileLocationSegmentMergeEntity
        {
            UserId = userId,
            DeviceId = first.DeviceId,
            BeforePointId = boundaryPointId,
            AfterPointId = boundaryPointId,
            Kind = normalized.Kind,
            ActorUserId = userId,
            CreatedAt = now
        });

        EnqueueAudit(userId, "mobile.location.segment.merge", null, new Dictionary<string, string>
        {
            ["deviceId"] = first.DeviceId,
            ["boundaryPointId"] = boundaryPointId.ToString(),
            ["kind"] = normalized.Kind,
            ["segmentIds"] = string.Join(',', normalized.SegmentIds)
        });
        await _db.SaveChangesAsync(ct);

        var recomputedAtUtc = await RecalculateAsync(
            [new MobileLocationRecalculationScope(
                first.DeviceId,
                normalized.RangeStartUtc,
                normalized.RangeEndUtc)],
            ct);
        return new MobileLocationSegmentMergeResult(
            normalized.Kind,
            boundaryPointId,
            first.StartUtc,
            second.EndUtc,
            recomputedAtUtc);
    }

    /// <summary>
    /// 分段 ID 自带首尾点（<c>segment_{first:N}_{last:N}</c>）。先解析出共享边界，
    /// 才能在“首次合并已完成、分段 ID 已变化”的重试请求上识别目标状态，而不是重算后判 404。
    /// </summary>
    private static NormalizedSegmentMerge NormalizeSegmentMergeRequest(MobileLocationSegmentMergeRequest request)
    {
        var segmentIds = (request.SegmentIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (segmentIds.Count != 2)
            throw new DomainException(6210, "一次只能合并两个相邻分段。");
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("stay" or "move"))
            throw new DomainException(6210, "合并后的分段类型只能是 stay 或 move。");
        if (request.RangeEndUtc <= request.RangeStartUtc)
            throw new DomainException(6210, "时间范围无效。");

        if (!MobileLocationSegmentBoundaries.TryParseSegmentId(
                segmentIds[0], out var firstStart, out var firstEnd)
            || !MobileLocationSegmentBoundaries.TryParseSegmentId(
                segmentIds[1], out var secondStart, out var secondEnd))
        {
            throw new DomainException(6210, "分段边界无效。");
        }

        string orderFirst;
        string orderSecond;
        Guid segmentFirstPointId;
        Guid boundaryPointId;
        Guid segmentLastPointId;
        if (firstEnd == secondStart)
        {
            (orderFirst, orderSecond) = (segmentIds[0], segmentIds[1]);
            (segmentFirstPointId, boundaryPointId, segmentLastPointId) = (firstStart, firstEnd, secondEnd);
        }
        else if (secondEnd == firstStart)
        {
            (orderFirst, orderSecond) = (segmentIds[1], segmentIds[0]);
            (segmentFirstPointId, boundaryPointId, segmentLastPointId) = (secondStart, secondEnd, firstEnd);
        }
        else
        {
            throw new DomainException(6210, "只能合并共享边界点的相邻分段。");
        }

        if (boundaryPointId == Guid.Empty
            || segmentFirstPointId == Guid.Empty
            || segmentLastPointId == Guid.Empty
            || boundaryPointId == segmentFirstPointId
            || boundaryPointId == segmentLastPointId
            || segmentFirstPointId == segmentLastPointId)
        {
            throw new DomainException(6210, "分段边界无效。");
        }

        return new NormalizedSegmentMerge(
            [orderFirst, orderSecond],
            kind,
            request.RangeStartUtc,
            request.RangeEndUtc,
            request.Timezone,
            request.DeviceId?.Trim(),
            segmentFirstPointId,
            boundaryPointId,
            segmentLastPointId);
    }

    private async Task<MobileLocationSegmentMergeEntity?> FindSegmentMergeAsync(
        Guid userId,
        Guid boundaryPointId,
        string? deviceId,
        CancellationToken ct)
    {
        // 边界点 ID 已唯一归属一台设备。请求未带 deviceId 时也必须按边界点设备查询，
        // 否则历史脏数据里同一边界存在多设备行时 SingleOrDefault 会抛异常而不是给出 409。
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var device = deviceId.Trim();
        return await _db.Set<MobileLocationSegmentMergeEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(merge => merge.UserId == userId
                && merge.DeviceId == device
                && merge.BeforePointId == boundaryPointId
                && merge.AfterPointId == boundaryPointId, ct);
    }

    private sealed record NormalizedSegmentMerge(
        IReadOnlyList<string> SegmentIds,
        string Kind,
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        string? Timezone,
        string? DeviceId,
        Guid FirstPointId,
        Guid BoundaryPointId,
        Guid LastPointId);

    public async Task<IReadOnlyList<MobileLocationPointRevisionDto>> GetRevisionsAsync(
        Guid pointId,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var pointExists = await _db.Set<MobileLocationPointEntity>()
            .AnyAsync(point => point.UserId == userId && point.Id == pointId, ct);
        if (!pointExists)
            throw new DomainException(40401, "定位点不存在。");

        return await _db.Set<MobileLocationPointRevisionEntity>()
            .AsNoTracking()
            .Where(revision => revision.UserId == userId && revision.PointId == pointId)
            .OrderByDescending(revision => revision.CreatedAt)
            .Select(revision => new MobileLocationPointRevisionDto(
                revision.Id,
                revision.PointId,
                revision.DeviceId,
                revision.Operation,
                revision.RevisionBefore,
                revision.RevisionAfter,
                DecimalToDouble(revision.OriginalLatitude),
                DecimalToDouble(revision.OriginalLongitude),
                DecimalToDouble(revision.NewLatitude),
                DecimalToDouble(revision.NewLongitude),
                revision.ExcludedBefore,
                revision.ExcludedAfter,
                revision.Reason,
                revision.ActorUserId,
                revision.CreatedAt))
            .ToListAsync(ct);
    }

    private async Task<MobileLocationPointEntity> LoadPointAsync(
        Guid userId,
        Guid pointId,
        CancellationToken ct)
        => await _db.Set<MobileLocationPointEntity>()
            .SingleOrDefaultAsync(point => point.UserId == userId && point.Id == pointId, ct)
            ?? throw new DomainException(40401, "定位点不存在。");

    private static void EnsureRevision(MobileLocationPointEntity point, int revision)
    {
        if (point.Revision != revision)
            throw new DomainException(6204, "这个定位点已被其他修改更新，请刷新后重新选择。");
    }

    private static void ApplyExclusion(
        MobileLocationPointEntity point,
        bool excluded,
        Guid userId,
        DateTimeOffset now)
    {
        point.IsExcluded = excluded;
        point.ExcludedAtUtc = excluded ? now : null;
        point.ExcludedByUserId = excluded ? userId : null;
    }

    /// <summary>
    /// 位置写操作统一的事务边界：每次执行策略重试都清空跟踪器并重新加载，
    /// 业务行、修订、审计和同步重算在同一事务内完成；只有全部成功并提交后才驱逐聚合缓存。
    /// 这样重算失败不会留下“写入已生效但接口报错”的半完成状态。
    /// </summary>
    private async Task<T> ExecuteMutationAsync<T>(
        Func<CancellationToken, Task<T>> attempt,
        CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(
            async token =>
            {
                _db.ChangeTracker.Clear();
                await using var transaction = _db.Database.IsRelational()
                    ? await _db.Database.BeginTransactionAsync(token)
                    : null;
                var value = await attempt(token);
                if (transaction is not null)
                    await transaction.CommitAsync(token);
                return value;
            },
            ct);

        // 提交后再驱逐，避免并发读把提交前的结果回填到新代际缓存。
        _cache.EvictByPrefix("/api/v1/mobile/");
        return result;
    }

    private async Task SavePointMutationAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException(6204, "这个定位点已被其他修改更新，请刷新后重新选择。");
        }
        catch (DbUpdateException ex) when (!ct.IsCancellationRequested
            && MobileDbErrors.IsUniqueConstraintViolation(ex))
        {
            throw new DomainException(6205, "修改后的坐标与同一时刻的另一个定位点重复。");
        }
    }

    /// <summary>
    /// 永久删除点后，修订行与依赖该边界点的分段合并覆盖都必须一起消失。
    /// 只排队删除，由调用方与点状态/审计一起提交，避免出现“点已删除、历史仍可读”的中间态。
    /// </summary>
    private async Task<bool> QueueDeletedPointHistoryPurgeAsync(
        Guid userId,
        Guid pointId,
        CancellationToken ct)
    {
        var revisions = await _db.Set<MobileLocationPointRevisionEntity>()
            .Where(revision => revision.UserId == userId && revision.PointId == pointId)
            .ToListAsync(ct);
        var merges = await _db.Set<MobileLocationSegmentMergeEntity>()
            .Where(merge => merge.UserId == userId
                && (merge.BeforePointId == pointId || merge.AfterPointId == pointId))
            .ToListAsync(ct);
        if (revisions.Count == 0 && merges.Count == 0)
            return false;

        _db.Set<MobileLocationPointRevisionEntity>().RemoveRange(revisions);
        _db.Set<MobileLocationSegmentMergeEntity>().RemoveRange(merges);
        return true;
    }

    private static MobileLocationCursorValue? ResolveCursor(string? cursor)
        => string.IsNullOrWhiteSpace(cursor) ? null : MobileLocationCursor.Parse(cursor);

    private async Task<DateTimeOffset> RecalculateAsync(
        IReadOnlyCollection<MobileLocationRecalculationScope> scopes,
        CancellationToken ct)
        => await _recalculationService.RecalculateAsync(scopes, ct);

    private static MobileLocationRecalculationScope LocalDayScope(
        string deviceId,
        DateTimeOffset recordedAtUtc)
    {
        var timeZone = ResolveTimeZone(MobileAnalyticsDefaults.DefaultTimezone);
        var localDate = TimeZoneInfo.ConvertTime(recordedAtUtc, timeZone).Date;
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified),
            timeZone);
        return new MobileLocationRecalculationScope(
            deviceId,
            new DateTimeOffset(startUtc, TimeSpan.Zero),
            new DateTimeOffset(startUtc.AddDays(1), TimeSpan.Zero));
    }

    private static TimeZoneInfo ResolveTimeZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }

    private void EnqueueAudit(
        Guid userId,
        string action,
        Guid? pointId,
        IReadOnlyDictionary<string, string> metadata)
        => _auditLog.Enqueue(new CreateAuditLogRequest(
            userId,
            AuditActorType.User,
            action,
            ResourceType,
            pointId?.ToString(),
            AuditSource,
            AuditResult.Success,
            null,
            null,
            null,
            metadata,
            null,
            null));

    private static MobileManagedLocationPointDto MapPoint(MobileLocationPointEntity point)
        => new(
            point.Id,
            point.DeviceId,
            point.RecordedAtUtc,
            Convert.ToDouble(point.Latitude),
            Convert.ToDouble(point.Longitude),
            DecimalToDouble(point.OriginalLatitude),
            DecimalToDouble(point.OriginalLongitude),
            Convert.ToDouble(point.HorizontalAccuracyMeters),
            point.Provider,
            point.Source,
            point.Quality,
            point.IsExcluded,
            point.OriginalLatitude is not null
                && point.OriginalLongitude is not null
                && (point.Latitude != point.OriginalLatitude || point.Longitude != point.OriginalLongitude),
            point.Revision,
            point.ExcludedAtUtc,
            point.UpdatedAt,
            point.DeletedAtUtc);

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)
            || latitude is < -90 or > 90
            || longitude is < -180 or > 180)
        {
            throw new DomainException(6201, "定位点经纬度无效。");
        }
    }

    private static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;
        var value = reason.Trim();
        return value.Length <= MaxReasonLength ? value : value[..MaxReasonLength];
    }

    private static decimal Coordinate(double value)
        => decimal.Round(Convert.ToDecimal(value), CoordinateScale, MidpointRounding.AwayFromZero);

    private static decimal Accuracy(double value)
        => decimal.Round(Convert.ToDecimal(value), AccuracyScale, MidpointRounding.AwayFromZero);

    private static double? DecimalToDouble(decimal? value)
        => value is null ? null : Convert.ToDouble(value.Value);
}
