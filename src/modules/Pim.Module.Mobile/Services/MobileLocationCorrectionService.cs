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
    private readonly IAuditLogService _auditLog;
    private readonly IAggregateResultCache _cache;
    private readonly TimeProvider _timeProvider;

    public MobileLocationCorrectionService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileLocationQueryService queryService,
        MobileLocationAggregationService aggregationService,
        IAuditLogService auditLog,
        IAggregateResultCache cache,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _queryService = queryService;
        _aggregationService = aggregationService;
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

        var query = _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(point => point.UserId == userId
                && point.RecordedAtUtc >= context.Range.RangeStartUtc
                && point.RecordedAtUtc < context.Range.RangeEndUtc);
        if (!request.IncludeExcluded)
            query = query.Where(point => !point.IsExcluded);
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            query = query.Where(point => point.DeviceId == context.DeviceId);

        var points = await query
            .OrderBy(point => point.RecordedAtUtc)
            .ThenBy(point => point.Id)
            .ToListAsync(ct);

        if (!request.IncludeExcluded && !string.IsNullOrWhiteSpace(request.SegmentId))
        {
            var segment = await _aggregationService.GetSegmentAsync(
                request.SegmentId.Trim(),
                new MobileLocationQueryRequest(
                    request.RangeStartUtc,
                    request.RangeEndUtc,
                    request.Timezone,
                    request.DeviceId,
                    PageSize: request.PageSize,
                    Cursor: request.Cursor),
                ct);
            if (segment is null)
                return new MobileManagedLocationPointPageDto([], null, false);

            var segmentPointIds = segment.Path
                .Select(point => Guid.Parse(point.Id))
                .ToHashSet();
            points = points.Where(point => segmentPointIds.Contains(point.Id)).ToList();
        }

        var startIndex = 0;
        if (Guid.TryParse(context.Cursor, out var cursorId))
        {
            var cursorIndex = points.FindIndex(point => point.Id == cursorId);
            if (cursorIndex >= 0)
                startIndex = cursorIndex + 1;
        }

        var page = points.Skip(startIndex).Take(context.PageSize).ToList();
        var hasMore = startIndex + page.Count < points.Count;
        var nextCursor = hasMore && page.Count > 0 ? page[^1].Id.ToString() : null;
        return new MobileManagedLocationPointPageDto(page.Select(MapPoint).ToList(), nextCursor, hasMore);
    }

    public async Task<MobileLocationPointMutationResult> CorrectAsync(
        Guid pointId,
        MobileLocationPointCorrectionRequest request,
        CancellationToken ct = default)
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
            return new MobileLocationPointMutationResult(MapPoint(point), now);

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
            return new MobileLocationPointMutationResult(MapPoint(point), now);

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
        _cache.EvictByPrefix("/api/v1/mobile/");
        return new MobileLocationPointMutationResult(MapPoint(point), now);
    }

    public async Task<MobileLocationPointMutationResult> RestoreAsync(
        Guid pointId,
        MobileLocationPointRestoreRequest request,
        CancellationToken ct = default)
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
            return new MobileLocationPointMutationResult(MapPoint(point), now);

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
        _cache.EvictByPrefix("/api/v1/mobile/");
        return new MobileLocationPointMutationResult(MapPoint(point), now);
    }

    public async Task<MobileLocationBatchMutationResult> SetExclusionAsync(
        MobileLocationPointExclusionRequest request,
        CancellationToken ct = default)
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
            _cache.EvictByPrefix("/api/v1/mobile/");
        }

        return new MobileLocationBatchMutationResult(changed.Count, results, now);
    }

    public async Task<MobileLocationPointDeleteResult> DeleteAsync(
        Guid pointId,
        int revision,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var point = await _db.Set<MobileLocationPointEntity>()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.Id == pointId, ct)
            ?? throw new DomainException(40401, "定位点不存在。");
        var now = _timeProvider.GetUtcNow();
        if (point.DeletedAtUtc is not null)
            return new MobileLocationPointDeleteResult(point.Id, point.DeletedAtUtc.Value, now);

        EnsureRevision(point, revision);
        var revisionBefore = point.Revision;
        point.DeletedAtUtc = now;
        point.DeletedByUserId = userId;
        point.Revision++;
        point.UpdatedAt = now;
        _db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = userId,
            PointId = point.Id,
            DeviceId = point.DeviceId,
            Operation = "delete",
            RevisionBefore = revisionBefore,
            RevisionAfter = point.Revision,
            ExcludedBefore = point.IsExcluded,
            ExcludedAfter = point.IsExcluded,
            ActorUserId = userId,
            CreatedAt = now
        });
        EnqueueAudit(userId, "mobile.location.delete", point.Id, new Dictionary<string, string>
        {
            ["deviceId"] = point.DeviceId,
            ["recordedAtUtc"] = point.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["latitude"] = point.Latitude.ToString(CultureInfo.InvariantCulture),
            ["longitude"] = point.Longitude.ToString(CultureInfo.InvariantCulture),
            ["revisionBefore"] = revisionBefore.ToString(CultureInfo.InvariantCulture)
        });

        await SavePointMutationAsync(ct);
        _cache.EvictByPrefix("/api/v1/mobile/");
        return new MobileLocationPointDeleteResult(point.Id, now, now);
    }

    public async Task<MobileLocationPointMutationResult> FillAsync(
        MobileLocationFillRequest request,
        CancellationToken ct = default)
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
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException(6205, "该设备在这个时刻已经存在相同坐标的定位点。");
        }

        _cache.EvictByPrefix("/api/v1/mobile/");
        return new MobileLocationPointMutationResult(MapPoint(point), now);
    }

    public async Task<MobileLocationSegmentMergeResult> MergeAdjacentSegmentsAsync(
        MobileLocationSegmentMergeRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var segmentIds = (request.SegmentIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (segmentIds.Count != 2)
            throw new DomainException(6210, "一次只能合并两个相邻分段。");
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not ("stay" or "move"))
            throw new DomainException(6210, "合并后的分段类型只能是 stay 或 move。");
        if (request.RangeEndUtc <= request.RangeStartUtc)
            throw new DomainException(6210, "时间范围无效。");

        var tracks = await _aggregationService.GetTracksAsync(new MobileLocationQueryRequest(
            request.RangeStartUtc,
            request.RangeEndUtc,
            request.Timezone,
            request.DeviceId,
            IncludeRejected: false), ct);
        var segments = tracks.SelectMany(track => track.Segments)
            .Where(segment => segmentIds.Contains(segment.Id, StringComparer.Ordinal))
            .OrderBy(segment => segment.StartUtc)
            .ToList();
        if (segments.Count != 2)
            throw new DomainException(40401, "轨迹分段不存在或不在同一时间范围内。");
        var first = segments[0];
        var second = segments[1];
        if (!string.Equals(first.DeviceId, second.DeviceId, StringComparison.Ordinal)
            || !string.Equals(first.Path[^1].Id, second.Path[0].Id, StringComparison.Ordinal))
        {
            throw new DomainException(6210, "只能合并共享边界点的相邻分段。");
        }
        if (!Guid.TryParse(first.Path[^1].Id, out var boundaryPointId))
            throw new DomainException(6210, "分段边界无效。");

        var now = _timeProvider.GetUtcNow();
        var existing = await _db.Set<MobileLocationSegmentMergeEntity>()
            .SingleOrDefaultAsync(merge => merge.UserId == userId
                && merge.DeviceId == first.DeviceId
                && merge.BeforePointId == boundaryPointId
                && merge.AfterPointId == boundaryPointId, ct);
        if (existing is null)
        {
            _db.Set<MobileLocationSegmentMergeEntity>().Add(new MobileLocationSegmentMergeEntity
            {
                UserId = userId,
                DeviceId = first.DeviceId,
                BeforePointId = boundaryPointId,
                AfterPointId = boundaryPointId,
                Kind = kind,
                ActorUserId = userId,
                CreatedAt = now
            });
        }
        else
        {
            existing.Kind = kind;
            existing.CreatedAt = now;
            existing.ActorUserId = userId;
        }

        EnqueueAudit(userId, "mobile.location.segment.merge", null, new Dictionary<string, string>
        {
            ["deviceId"] = first.DeviceId,
            ["boundaryPointId"] = boundaryPointId.ToString(),
            ["kind"] = kind,
            ["segmentIds"] = string.Join(',', segmentIds)
        });
        await _db.SaveChangesAsync(ct);
        _cache.EvictByPrefix("/api/v1/mobile/");
        return new MobileLocationSegmentMergeResult(
            kind,
            boundaryPointId,
            first.StartUtc,
            second.EndUtc,
            now);
    }

    public async Task<IReadOnlyList<MobileLocationPointRevisionDto>> GetRevisionsAsync(
        Guid pointId,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var pointExists = await _db.Set<MobileLocationPointEntity>()
            .IgnoreQueryFilters()
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
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException(6205, "修改后的坐标与同一时刻的另一个定位点重复。");
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
            point.UpdatedAt);

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
