using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileUsageGoalService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileUsageGoalService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<MobileUsageGoalDto>> ListAsync(CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        return await _db.Set<MobileUsageGoalEntity>()
            .AsNoTracking()
            .Where(goal => goal.UserId == userId)
            .OrderBy(goal => goal.Scope)
            .ThenBy(goal => goal.LifeCategory)
            .ThenBy(goal => goal.PackageName)
            .Select(goal => ToDto(goal))
            .ToListAsync(ct);
    }

    public async Task<MobileUsageGoalDto> SaveAsync(MobileUsageGoalUpsertRequest request, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var now = _timeProvider.GetUtcNow();
        var scope = Normalize(request.Scope, "total-daily");
        var packageName = NormalizeOptional(request.PackageName);
        var lifeCategory = NormalizeOptional(request.LifeCategory);

        var entity = await _db.Set<MobileUsageGoalEntity>()
            .SingleOrDefaultAsync(goal => goal.UserId == userId
                && goal.Scope == scope
                && goal.PackageName == packageName
                && goal.LifeCategory == lifeCategory, ct);

        if (entity is null)
        {
            entity = new MobileUsageGoalEntity
            {
                UserId = userId,
                Scope = scope,
                PackageName = packageName,
                LifeCategory = lifeCategory,
                CreatedAt = now
            };
            _db.Set<MobileUsageGoalEntity>().Add(entity);
        }

        entity.Label = Normalize(request.Label, "每日手机总时长");
        entity.LimitSeconds = Math.Max(0, request.LimitSeconds);
        entity.IsEnabled = request.IsEnabled;
        entity.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        if (!Guid.TryParse(id, out var goalId))
            return false;

        var entity = await _db.Set<MobileUsageGoalEntity>()
            .SingleOrDefaultAsync(goal => goal.UserId == userId && goal.Id == goalId, ct);
        if (entity is null)
            return false;

        _db.Set<MobileUsageGoalEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static MobileUsageGoalDto ToDto(MobileUsageGoalEntity goal)
        => new(
            goal.Id.ToString("D"),
            goal.Scope,
            goal.PackageName,
            goal.LifeCategory,
            goal.Label,
            goal.LimitSeconds,
            goal.IsEnabled,
            goal.CreatedAt,
            goal.UpdatedAt);

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
