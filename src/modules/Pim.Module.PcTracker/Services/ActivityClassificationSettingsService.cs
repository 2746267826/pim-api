using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class ActivityClassificationSettingsService
{
    private const string DefaultSettingsKey = "default";
    private const int DefaultRecommendedMinimumMinutes = 5;
    private static readonly int[] SupportedRecommendedMinimumDurations = [1, 3, 5, 10, 15];

    private readonly PimDbContext _db;

    public ActivityClassificationSettingsService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<ActivityClassificationSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var entity = await GetSettingsEntityAsync(ct);
        return entity is not null
            ? ToDto(entity)
            : DefaultDto();
    }

    public async Task<ActivityClassificationSettingsDto> SaveSettingsAsync(int requestedMinutes, CancellationToken ct)
    {
        var entity = await GetSettingsEntityAsync(ct) ?? CreateDefaultSettingsEntity();
        entity.RecommendedMinimumClassificationDurationMinutes = ClampToSupportedPreset(requestedMinutes);
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        if (_db.Entry(entity).State == EntityState.Detached)
            _db.Set<ActivityClassificationSettingsEntity>().Add(entity);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (_db.Entry(entity).State == EntityState.Added)
        {
            _db.ChangeTracker.Clear();
            entity = await GetSettingsEntityAsync(ct)
                ?? throw new InvalidOperationException("保存设置时发生冲突，且默认设置行不存在。");
            entity.RecommendedMinimumClassificationDurationMinutes = ClampToSupportedPreset(requestedMinutes);
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return ToDto(entity);
    }

    private async Task<ActivityClassificationSettingsEntity?> GetSettingsEntityAsync(CancellationToken ct)
    {
        return await _db.Set<ActivityClassificationSettingsEntity>()
            .FirstOrDefaultAsync(e => e.SettingsKey == DefaultSettingsKey, ct);
    }

    private static ActivityClassificationSettingsEntity CreateDefaultSettingsEntity()
    {
        var now = DateTimeOffset.UtcNow;
        return new ActivityClassificationSettingsEntity
        {
            Id = Guid.NewGuid(),
            SettingsKey = DefaultSettingsKey,
            RecommendedMinimumClassificationDurationMinutes = DefaultRecommendedMinimumMinutes,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static int ClampToSupportedPreset(int requestedMinutes)
    {
        return SupportedRecommendedMinimumDurations
            .OrderBy(duration => Math.Abs(duration - requestedMinutes))
            .ThenBy(duration => duration)
            .First();
    }

    private static ActivityClassificationSettingsDto ToDto(ActivityClassificationSettingsEntity entity)
    {
        return new ActivityClassificationSettingsDto(
            entity.RecommendedMinimumClassificationDurationMinutes,
            SupportedRecommendedMinimumDurations.ToArray());
    }

    private static ActivityClassificationSettingsDto DefaultDto()
    {
        return new ActivityClassificationSettingsDto(
            DefaultRecommendedMinimumMinutes,
            SupportedRecommendedMinimumDurations.ToArray());
    }
}
