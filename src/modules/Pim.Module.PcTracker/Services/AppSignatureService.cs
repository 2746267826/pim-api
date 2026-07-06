using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class AppSignatureService
{
    private readonly PimDbContext _db;

    public AppSignatureService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<List<AppSignatureDto>> GetAllAsync(string? search, CancellationToken ct)
    {
        var query = _db.Set<AppSignatureEntity>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLowerInvariant();
            query = query.Where(x => x.ProcessName.ToLower().Contains(s)
                                  || x.DisplayName.ToLower().Contains(s));
        }
        return await query
            .OrderByDescending(x => x.LastSeenAt)
            .ThenBy(x => x.ProcessName)
            .Select(x => ToDto(x))
            .ToListAsync(ct);
    }

    public async Task<List<AppKnowledgeAppDto>> GetKnowledgeAppsAsync(string? search, CancellationToken ct)
    {
        var query = _db.Set<AppSignatureEntity>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLowerInvariant();
            query = query.Where(x => x.ProcessName.ToLower().Contains(s)
                                  || x.DisplayName.ToLower().Contains(s)
                                  || (x.SearchKeywords != null && x.SearchKeywords.ToLower().Contains(s)));
        }

        var apps = await query
            .OrderByDescending(x => x.LastSeenAt)
            .ThenBy(x => x.ProcessName)
            .ToListAsync(ct);

        var appIds = apps.Select(x => x.Id).ToList();
        var contextStats = await _db.Set<AppKnowledgeContextEntity>()
            .Where(x => x.AppSignatureId.HasValue && appIds.Contains(x.AppSignatureId.Value))
            .GroupBy(x => x.AppSignatureId!.Value)
            .Select(x => new
            {
                AppId = x.Key,
                ContextCount = x.Count(),
                RecentAffectedDurationSeconds = x.Sum(item => item.AffectedDurationSeconds)
            })
            .ToListAsync(ct);
        var statsByAppId = contextStats.ToDictionary(x => x.AppId);

        return apps.Select(app =>
        {
            statsByAppId.TryGetValue(app.Id, out var stats);
            return new AppKnowledgeAppDto(
                app.Id,
                app.ProcessName,
                app.DisplayName,
                app.CategoryPath,
                app.Productivity,
                app.Description,
                app.Source,
                app.Confidence,
                app.Icon,
                app.LastSeenAt,
                app.CreatedAt,
                stats?.ContextCount ?? 0,
                0,
                stats?.RecentAffectedDurationSeconds ?? 0);
        }).ToList();
    }

    public async Task<AppSignatureDto?> LookupByProcessNameAsync(string processName, CancellationToken ct)
    {
        var normalizedName = processName.ToLowerInvariant();

        // Try exact match (case-insensitive via normalized)
        var entity = await _db.Set<AppSignatureEntity>()
            .FirstOrDefaultAsync(x => x.ProcessName.ToLower() == normalizedName, ct);

        if (entity is null && !normalizedName.EndsWith(".exe"))
        {
            // If normalized name (stripped .exe) didn't match, try with .exe suffix
            entity = await _db.Set<AppSignatureEntity>()
                .FirstOrDefaultAsync(x => x.ProcessName.ToLower() == normalizedName + ".exe", ct);
        }

        if (entity is null)
        {
            // Try glob pattern match (e.g. MobaXterm*.exe)
            var all = await _db.Set<AppSignatureEntity>().ToListAsync(ct);
            foreach (var candidateName in new[] { normalizedName, normalizedName + ".exe" })
            {
                entity = all.FirstOrDefault(sig =>
                {
                    var pattern = sig.ProcessName;
                    if (!pattern.Contains('*') && !pattern.Contains('?'))
                        return false;
                    var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                        .Replace("\\*", ".*")
                        .Replace("\\?", ".") + "$";
                    return System.Text.RegularExpressions.Regex.IsMatch(candidateName, regex,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                });
                if (entity is not null) break;
            }
        }

        if (entity is not null)
        {
            entity.LastSeenAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return entity is not null ? ToDto(entity) : null;
    }

    public async Task<AppSignatureDto> SaveAsync(SaveAppSignatureRequest req, CancellationToken ct)
    {
        var existing = await _db.Set<AppSignatureEntity>()
            .FirstOrDefaultAsync(x => x.ProcessName == req.ProcessName, ct);

        if (existing is not null)
        {
            existing.DisplayName = req.DisplayName;
            existing.CategoryPath = req.CategoryPath;
            existing.Productivity = req.Productivity;
            existing.Description = req.Description;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            existing = new AppSignatureEntity
            {
                Id = Guid.NewGuid(),
                ProcessName = req.ProcessName,
                DisplayName = req.DisplayName,
                CategoryPath = req.CategoryPath,
                Productivity = req.Productivity,
                Description = req.Description,
                Source = "manual",
                Confidence = 1.0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.Set<AppSignatureEntity>().Add(existing);
        }

        await _db.SaveChangesAsync(ct);
        return ToDto(existing);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<AppSignatureEntity>().FindAsync(new object[] { id }, ct);
        if (entity is null || entity.Source == "builtin")
            return false;
        _db.Set<AppSignatureEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> GetCountAsync(CancellationToken ct)
    {
        return await _db.Set<AppSignatureEntity>().CountAsync(ct);
    }

    private static AppSignatureDto ToDto(AppSignatureEntity e) => new(
        e.Id, e.ProcessName, e.DisplayName, e.CategoryPath,
        e.Productivity, e.Description, e.Source, e.Confidence,
        e.Icon, e.LastSeenAt, e.CreatedAt);
}
