using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileDeviceService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileDeviceService(PimDbContext db, ICurrentUserService currentUser, TimeProvider? timeProvider = null)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MobileDeviceDto> RegisterAsync(MobileDeviceRegisterRequest request, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var now = _timeProvider.GetUtcNow();
        var entity = await _db.Set<MobileDeviceEntity>()
            .SingleOrDefaultAsync(d => d.UserId == userId && d.DeviceId == request.DeviceId, ct);

        if (entity is null)
        {
            entity = new MobileDeviceEntity
            {
                UserId = userId,
                DeviceId = request.DeviceId,
                RegisteredAtUtc = now,
                CreatedAt = now
            };
            _db.Set<MobileDeviceEntity>().Add(entity);
        }

        entity.DeviceHash = request.DeviceHash;
        entity.DisplayName = request.DisplayName;
        entity.Manufacturer = request.Manufacturer;
        entity.Brand = request.Brand;
        entity.Model = request.Model;
        entity.OsVersion = request.OsVersion;
        entity.ApiLevel = request.ApiLevel;
        entity.AppVersion = request.AppVersion;
        entity.MetadataJson = JsonOrDefault(request.MetadataJson);
        entity.LastSeenAtUtc = now;
        entity.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<IReadOnlyList<MobileDeviceDto>> ListAsync(CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        return await _db.Set<MobileDeviceEntity>()
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .Select(d => Map(d))
            .ToListAsync(ct);
    }

    private static MobileDeviceDto Map(MobileDeviceEntity entity)
        => new(
            entity.Id,
            entity.DeviceId,
            entity.DeviceHash,
            entity.DisplayName,
            entity.Manufacturer,
            entity.Brand,
            entity.Model,
            entity.OsVersion,
            entity.ApiLevel,
            entity.AppVersion,
            entity.MetadataJson,
            entity.RegisteredAtUtc,
            entity.LastSeenAtUtc);

    private static string JsonOrDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;
}
