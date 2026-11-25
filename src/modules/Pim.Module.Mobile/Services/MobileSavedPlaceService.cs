using Microsoft.EntityFrameworkCore;
using Pim.Core.Caching;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 常去地点的稳定名称。聚类结果随日期/设备筛选变化，名称按中心点半径匹配复用。
/// </summary>
public sealed class MobileSavedPlaceService
{
    private const int MaxNameLength = 100;
    private const double MinRadiusMeters = 30;
    private const double MaxRadiusMeters = 500;
    private const string ResourceType = "mobile-saved-place";

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogService _auditLog;
    private readonly IAggregateResultCache _cache;
    private readonly TimeProvider _timeProvider;

    public MobileSavedPlaceService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IAuditLogService auditLog,
        IAggregateResultCache cache,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _auditLog = auditLog;
        _cache = cache;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<MobileSavedPlaceDto>> ListAsync(CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var places = await _db.Set<MobileSavedPlaceEntity>()
            .AsNoTracking()
            .Where(place => place.UserId == userId)
            .OrderByDescending(place => place.UpdatedAt)
            .ToListAsync(ct);
        return places.Select(Map).ToList();
    }

    public async Task<MobileSavedPlaceDto> CreateAsync(
        MobileSavedPlaceCreateRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var name = NormalizeName(request.Name);
        var type = NormalizeType(request.Type);
        ValidateCoordinates(request.CenterLatitude, request.CenterLongitude);
        var radius = ClampRadius(request.RadiusMeters);
        var centerLatitude = Coordinate(request.CenterLatitude);
        var centerLongitude = Coordinate(request.CenterLongitude);
        var radiusMeters = Convert.ToDecimal(radius);
        var existing = await FindDuplicateAsync(
            userId,
            name,
            type,
            centerLatitude,
            centerLongitude,
            radiusMeters,
            ct);
        if (existing is not null)
            return Map(existing);

        var now = _timeProvider.GetUtcNow();
        var place = new MobileSavedPlaceEntity
        {
            UserId = userId,
            Name = name,
            PlaceType = type,
            CenterLatitude = centerLatitude,
            CenterLongitude = centerLongitude,
            RadiusMeters = radiusMeters,
            IsEnabled = true,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Set<MobileSavedPlaceEntity>().Add(place);
        var audit = EnqueueAudit(userId, "mobile.location.place.create", place.Id, new Dictionary<string, string>
        {
            ["name"] = name,
            ["type"] = type,
            ["radiusMeters"] = radius.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (!ct.IsCancellationRequested
            && MobileDbErrors.IsUniqueConstraintViolation(ex))
        {
            // 并发重复 POST 也可能同时越过上面的查询：撞唯一索引后按同一幂等请求返回已存在行。
            DetachPendingInserts(place.Id, audit.Id);
            existing = await FindDuplicateAsync(
                userId,
                name,
                type,
                centerLatitude,
                centerLongitude,
                radiusMeters,
                ct);
            if (existing is null)
                throw;
            return Map(existing);
        }

        _cache.EvictByPrefix("/api/v1/mobile/");
        return Map(place);
    }

    public async Task<MobileSavedPlaceDto> UpdateAsync(
        Guid placeId,
        MobileSavedPlaceUpdateRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var place = await _db.Set<MobileSavedPlaceEntity>()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.Id == placeId, ct)
            ?? throw new DomainException(40401, "地点不存在。");

        var name = NormalizeName(request.Name);
        var type = NormalizeType(request.Type);
        if (string.Equals(place.Name, name, StringComparison.Ordinal)
            && string.Equals(place.PlaceType, type, StringComparison.Ordinal))
        {
            return Map(place);
        }
        if (place.Revision != request.Revision)
            throw new DomainException(6204, "这个地点已被其他修改更新，请刷新后重新选择。");

        var now = _timeProvider.GetUtcNow();
        place.Name = name;
        place.PlaceType = type;
        place.Revision++;
        place.UpdatedAt = now;
        EnqueueAudit(userId, "mobile.location.place.update", place.Id, new Dictionary<string, string>
        {
            ["name"] = name,
            ["type"] = type,
            ["revision"] = place.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException(6204, "这个地点已被其他修改更新，请刷新后重新选择。");
        }
        catch (DbUpdateException ex) when (!ct.IsCancellationRequested
            && MobileDbErrors.IsUniqueConstraintViolation(ex))
        {
            throw new DomainException(6205, "该地点名称已被另一个相同范围的地点使用。");
        }

        _cache.EvictByPrefix("/api/v1/mobile/");
        return Map(place);
    }

    private AuditLogDto EnqueueAudit(
        Guid userId,
        string action,
        Guid placeId,
        IReadOnlyDictionary<string, string> metadata)
        => _auditLog.Enqueue(new CreateAuditLogRequest(
            userId,
            AuditActorType.User,
            action,
            ResourceType,
            placeId.ToString(),
            "web",
            AuditResult.Success,
            null,
            null,
            null,
            metadata,
            null,
            null));

    private async Task<MobileSavedPlaceEntity?> FindDuplicateAsync(
        Guid userId,
        string name,
        string type,
        decimal centerLatitude,
        decimal centerLongitude,
        decimal radiusMeters,
        CancellationToken ct)
        => await _db.Set<MobileSavedPlaceEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(place => place.UserId == userId
                && place.Name == name
                && place.PlaceType == type
                && place.CenterLatitude == centerLatitude
                && place.CenterLongitude == centerLongitude
                && place.RadiusMeters == radiusMeters, ct);

    private void DetachPendingInserts(Guid placeId, Guid auditId)
    {
        foreach (var entry in _db.ChangeTracker.Entries()
                     .Where(entry => entry.State == EntityState.Added)
                     .ToList())
        {
            if (entry.Entity is MobileSavedPlaceEntity place && place.Id == placeId)
                entry.State = EntityState.Detached;
            else if (entry.Entity is AuditLogEntity audit && audit.Id == auditId)
                entry.State = EntityState.Detached;
        }
    }

    private static MobileSavedPlaceDto Map(MobileSavedPlaceEntity place)
        => new(
            place.Id,
            place.Name,
            place.PlaceType,
            Convert.ToDouble(place.CenterLatitude),
            Convert.ToDouble(place.CenterLongitude),
            Convert.ToDouble(place.RadiusMeters),
            place.IsEnabled,
            place.Revision,
            place.CreatedAt,
            place.UpdatedAt);

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new DomainException(6208, "地点名称不能为空。");
        return normalized.Length <= MaxNameLength ? normalized : normalized[..MaxNameLength];
    }

    private static string NormalizeType(string? type)
        => type?.Trim().ToLowerInvariant() switch
        {
            "home" or "office" or "other" => type!.Trim().ToLowerInvariant(),
            _ => "other"
        };

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)
            || latitude is < -90 or > 90
            || longitude is < -180 or > 180)
        {
            throw new DomainException(6201, "地点经纬度无效。");
        }
    }

    private static double ClampRadius(double radius)
        => double.IsFinite(radius)
            ? Math.Clamp(radius, MinRadiusMeters, MaxRadiusMeters)
            : MinRadiusMeters;

    private static decimal Coordinate(double value)
        => decimal.Round(Convert.ToDecimal(value), 7, MidpointRounding.AwayFromZero);
}
