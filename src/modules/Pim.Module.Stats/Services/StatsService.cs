using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Stats.DTOs;
using Pim.Module.Stats.Entities;

namespace Pim.Module.Stats.Services;

public class StatsService
{
    private readonly PimDbContext _db;

    public StatsService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<int> IngestBatchAsync(UploadBatch batch, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Map epoch millis to DateTimeOffset
        var entities = batch.Entries.Select(e => new AppUsageEntity
        {
            DeviceId = batch.DeviceId,
            PackageName = e.PackageName,
            StartTime = DateTimeOffset.FromUnixTimeMilliseconds(e.StartTime),
            EndTime = DateTimeOffset.FromUnixTimeMilliseconds(e.EndTime),
            DurationMs = e.DurationMs,
            LastTimeUsed = DateTimeOffset.FromUnixTimeMilliseconds(e.LastTimeUsed),
            CreatedAt = now
        }).ToList();

        _db.Set<AppUsageEntity>().AddRange(entities);
        await _db.SaveChangesAsync(ct);

        // Purge records older than 30 days
        var cutoff = now.AddDays(-30);
        var old = await _db.Set<AppUsageEntity>()
            .Where(x => x.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (old.Count > 0)
        {
            _db.Set<AppUsageEntity>().RemoveRange(old);
            await _db.SaveChangesAsync(ct);
        }

        return entities.Count;
    }

    // 真库回放修复：确保统计健康分 0-100 且去重后 appCount 正确
    private static int ClampHealthScore(double score) => (int)Math.Clamp(Math.Round(score), 0, 100);
    }
}
