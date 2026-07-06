using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public sealed class AppKnowledgeContextService
{
    private static readonly HashSet<string> AllowedPatternTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "app-default",
        "domain",
        "title",
        "url-path",
        "source-family"
    };

    private readonly PimDbContext _db;

    public AppKnowledgeContextService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<List<AppKnowledgeContextDto>> GetByAppAsync(Guid appId, CancellationToken ct)
    {
        var contexts = await _db.Set<AppKnowledgeContextEntity>()
            .Where(item => item.AppSignatureId == appId)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.PatternType)
            .ThenBy(item => item.PatternValue)
            .ToListAsync(ct);

        return contexts.Select(ToDto).ToList();
    }

    public async Task<AppKnowledgeContextDto> SaveAsync(SaveAppKnowledgeContextRequest req, CancellationToken ct)
    {
        var processName = RequireTrimmed(req.ProcessName, nameof(req.ProcessName));
        var patternType = RequireTrimmed(req.PatternType, nameof(req.PatternType)).ToLowerInvariant();
        var patternValue = RequireTrimmed(req.PatternValue, nameof(req.PatternValue));
        var confidence = req.Confidence ?? 1.0;

        if (!AllowedPatternTypes.Contains(patternType))
        {
            throw new ArgumentException($"PatternType must be one of: {string.Join(", ", AllowedPatternTypes)}.", nameof(req.PatternType));
        }

        if (confidence is < 0 or > 1)
        {
            throw new ArgumentException("Confidence must be between 0 and 1.", nameof(req.Confidence));
        }

        var now = DateTimeOffset.UtcNow;
        var contexts = _db.Set<AppKnowledgeContextEntity>();
        var entity = await contexts.FirstOrDefaultAsync(item =>
            item.ProcessName == processName &&
            item.PatternType == patternType &&
            item.PatternValue == patternValue, ct);
        var isInsert = entity is null;

        if (entity is null)
        {
            entity = new AppKnowledgeContextEntity
            {
                Id = Guid.NewGuid(),
                CreatedAt = now,
                AffectedRecordCount = 0,
                AffectedDurationSeconds = 0
            };
            contexts.Add(entity);
        }

        var scopeSummary = await BuildScopeSummaryAsync(req.AppId, processName, patternType, patternValue, ct);
        ApplyRequest(entity, req, processName, patternType, patternValue, scopeSummary, confidence, now);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isInsert)
        {
            _db.Entry(entity).State = EntityState.Detached;
            entity = await contexts.FirstOrDefaultAsync(item =>
                item.ProcessName == processName &&
                item.PatternType == patternType &&
                item.PatternValue == patternValue, ct);

            if (entity is null)
            {
                throw;
            }

            ApplyRequest(entity, req, processName, patternType, patternValue, scopeSummary, confidence, now);
            await _db.SaveChangesAsync(ct);
        }

        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<AppKnowledgeContextEntity>().FindAsync(new object[] { id }, ct);
        if (entity is null)
        {
            return false;
        }

        _db.Set<AppKnowledgeContextEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    internal static AppKnowledgeContextDto ToDto(AppKnowledgeContextEntity entity) => new(
        entity.Id,
        entity.AppSignatureId,
        entity.ProcessName,
        entity.PatternType,
        entity.PatternValue,
        entity.TargetCategoryName,
        entity.ProjectTag,
        entity.ScopeSummary,
        entity.Source,
        entity.Confidence,
        entity.Enabled,
        entity.AffectedRecordCount,
        entity.AffectedDurationSeconds,
        entity.LastMatchedAt);

    private async Task<string> BuildScopeSummaryAsync(
        Guid? appId,
        string processName,
        string patternType,
        string patternValue,
        CancellationToken ct)
    {
        var appName = processName;
        if (appId is not null)
        {
            var displayName = await _db.Set<AppSignatureEntity>()
                .Where(item => item.Id == appId.Value)
                .Select(item => item.DisplayName)
                .FirstOrDefaultAsync(ct);

            if (displayName is null)
            {
                throw new ArgumentException("AppId was not found.", nameof(appId));
            }

            appName = string.IsNullOrWhiteSpace(displayName)
                ? processName
                : displayName.Trim();
        }

        return $"{appName} - {ToPatternLabel(patternType)}: {patternValue}";
    }

    private static string ToPatternLabel(string patternType) => patternType switch
    {
        "app-default" => "app default",
        "url-path" => "URL path",
        "source-family" => "source family",
        _ => patternType
    };

    private static void ApplyRequest(
        AppKnowledgeContextEntity entity,
        SaveAppKnowledgeContextRequest req,
        string processName,
        string patternType,
        string patternValue,
        string scopeSummary,
        double confidence,
        DateTimeOffset updatedAt)
    {
        entity.AppSignatureId = req.AppId;
        entity.ProcessName = processName;
        entity.PatternType = patternType;
        entity.PatternValue = patternValue;
        entity.TargetCategoryName = TrimToNull(req.TargetCategoryName);
        entity.ProjectTag = TrimToNull(req.ProjectTag);
        entity.ScopeSummary = scopeSummary;
        entity.Source = "user-confirmed";
        entity.Confidence = confidence;
        entity.Enabled = req.Enabled ?? true;
        entity.UpdatedAt = updatedAt;
    }

    private static string RequireTrimmed(string? value, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException($"{name} is required.", name);
        }

        return trimmed;
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
