using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileLocationService
{
    private const double MaxUsableAccuracyMeters = 50;
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileLocationService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<MobileLocationPointDto> SubmitAsync(MobileLocationPointRequest request, CancellationToken ct = default)
    {
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            throw new DomainException(6201, "Invalid mobile location coordinates.");

        var userId = MobileUserContext.RequireUserId(_currentUser);
        if (request.HorizontalAccuracyMeters > MaxUsableAccuracyMeters)
        {
            await SavePointAsync(userId, request, "rejected", ct);
            throw new DomainException(6202, "Mobile location accuracy is not usable.");
        }

        return Map(await SavePointAsync(userId, request, "usable", ct));
    }

    private async Task<MobileLocationPointEntity> SavePointAsync(
        Guid userId,
        MobileLocationPointRequest request,
        string quality,
        CancellationToken ct)
    {
        var entity = new MobileLocationPointEntity
        {
            UserId = userId,
            DeviceId = request.DeviceId,
            RecordedAtUtc = request.RecordedAtUtc,
            Latitude = Decimal(request.Latitude),
            Longitude = Decimal(request.Longitude),
            HorizontalAccuracyMeters = Decimal(request.HorizontalAccuracyMeters),
            Provider = request.Provider,
            Source = request.Source,
            AltitudeMeters = DecimalOrNull(request.AltitudeMeters),
            VerticalAccuracyMeters = DecimalOrNull(request.VerticalAccuracyMeters),
            SpeedMetersPerSecond = DecimalOrNull(request.SpeedMetersPerSecond),
            SpeedAccuracyMetersPerSecond = DecimalOrNull(request.SpeedAccuracyMetersPerSecond),
            BearingDegrees = DecimalOrNull(request.BearingDegrees),
            BearingAccuracyDegrees = DecimalOrNull(request.BearingAccuracyDegrees),
            IsMock = request.IsMock,
            RawJson = JsonOrDefault(request.RawJson),
            Quality = quality,
            CreatedAt = _timeProvider.GetUtcNow()
        };

        _db.Set<MobileLocationPointEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<IReadOnlyList<MobileLocationPointDto>> GetHistoryAsync(
        string? deviceId,
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        double maxAccuracyMeters = MaxUsableAccuracyMeters,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var query = _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(p => p.UserId == userId);

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(p => p.DeviceId == deviceId);
        if (rangeStartUtc is not null)
            query = query.Where(p => p.RecordedAtUtc >= rangeStartUtc);
        if (rangeEndUtc is not null)
            query = query.Where(p => p.RecordedAtUtc < rangeEndUtc);
        query = query.Where(p => p.HorizontalAccuracyMeters <= Decimal(maxAccuracyMeters));

        return await query
            .OrderByDescending(p => p.RecordedAtUtc)
            .Take(500)
            .Select(p => Map(p))
            .ToListAsync(ct);
    }

    private static MobileLocationPointDto Map(MobileLocationPointEntity entity)
        => new(
            entity.Id,
            entity.DeviceId,
            entity.RecordedAtUtc,
            entity.CreatedAt,
            (double)entity.Latitude,
            (double)entity.Longitude,
            (double)entity.HorizontalAccuracyMeters,
            entity.Provider,
            entity.Source,
            DecimalToDouble(entity.AltitudeMeters),
            DecimalToDouble(entity.VerticalAccuracyMeters),
            DecimalToDouble(entity.SpeedMetersPerSecond),
            DecimalToDouble(entity.SpeedAccuracyMetersPerSecond),
            DecimalToDouble(entity.BearingDegrees),
            DecimalToDouble(entity.BearingAccuracyDegrees),
            string.Equals(entity.Source, "auto", StringComparison.OrdinalIgnoreCase),
            entity.Quality,
            entity.RawJson);

    private static decimal Decimal(double value) => Convert.ToDecimal(value);

    private static decimal? DecimalOrNull(double? value)
        => value is null ? null : Convert.ToDecimal(value.Value);

    private static double? DecimalToDouble(decimal? value)
        => value is null ? null : Convert.ToDouble(value.Value);

    private static string JsonOrDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;
}
