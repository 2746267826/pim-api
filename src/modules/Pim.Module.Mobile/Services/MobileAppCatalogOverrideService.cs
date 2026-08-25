using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileAppCatalogOverrideService
{
    private static readonly HashSet<string> SupportedRuleTypes = new(StringComparer.Ordinal)
    {
        MobileAppClassificationService.RuleTypePackageExact,
        MobileAppClassificationService.RuleTypePackagePrefix,
        MobileAppClassificationService.RuleTypeKeyword,
        MobileAppClassificationService.RuleTypeDisplayKeyword,
        MobileAppClassificationService.RuleTypePackageKeyword
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileAppCatalogOverrideService(
        PimDbContext db,
        ICurrentUserService currentUser,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<MobileAppCatalogOverrideDto>> ListOverridesAsync(CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        return await _db.Set<MobileAppCatalogOverrideEntity>()
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderBy(o => o.PackageName)
            .Select(o => ToDto(o))
            .ToListAsync(ct);
    }

    public async Task<MobileAppCatalogOverrideDto> UpsertOverrideAsync(
        MobileAppCatalogOverrideUpsertRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var packageName = NormalizePackageName(request.PackageName);
        var lifeCategory = NormalizeLifeCategory(request.LifeCategory);
        var now = _timeProvider.GetUtcNow();

        var entity = await _db.Set<MobileAppCatalogOverrideEntity>()
            .SingleOrDefaultAsync(o => o.UserId == userId && o.PackageName == packageName, ct);

        if (entity is null)
        {
            entity = new MobileAppCatalogOverrideEntity
            {
                UserId = userId,
                PackageName = packageName,
                CreatedAt = now
            };
            _db.Set<MobileAppCatalogOverrideEntity>().Add(entity);
        }

        entity.DisplayNameOverride = NullIfBlank(request.DisplayNameOverride);
        entity.LifeCategory = lifeCategory;
        entity.IsSystemNoise = request.IsSystemNoise;
        entity.HideShortEvents = request.HideShortEvents;
        entity.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteOverrideAsync(string packageName, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var normalizedPackageName = NormalizePackageName(packageName);
        var entity = await _db.Set<MobileAppCatalogOverrideEntity>()
            .SingleOrDefaultAsync(o => o.UserId == userId && o.PackageName == normalizedPackageName, ct);

        if (entity is null)
            return false;

        _db.Set<MobileAppCatalogOverrideEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> ClearOverridesAsync(CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var entities = await _db.Set<MobileAppCatalogOverrideEntity>()
            .Where(o => o.UserId == userId)
            .ToListAsync(ct);

        if (entities.Count == 0)
            return 0;

        _db.Set<MobileAppCatalogOverrideEntity>().RemoveRange(entities);
        await _db.SaveChangesAsync(ct);
        return entities.Count;
    }

    public async Task<IReadOnlyList<MobileAppCategoryRuleDto>> ListCategoryRulesAsync(CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        return await _db.Set<MobileAppCategoryRuleEntity>()
            .AsNoTracking()
            .Where(rule => rule.UserId == userId)
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.CreatedAt)
            .ThenBy(rule => rule.Pattern)
            .ThenBy(rule => rule.Id)
            .Select(rule => ToDto(rule))
            .ToListAsync(ct);
    }

    public async Task<MobileAppCategoryRuleDto> CreateCategoryRuleAsync(
        MobileAppCategoryRuleUpsertRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var now = _timeProvider.GetUtcNow();
        var entity = new MobileAppCategoryRuleEntity
        {
            UserId = userId,
            RuleType = NormalizeRuleType(request.RuleType),
            Pattern = NormalizePattern(request.Pattern),
            LifeCategory = NormalizeLifeCategory(request.LifeCategory),
            Priority = request.Priority,
            IsEnabled = request.IsEnabled,
            DisplayNameOverride = NullIfBlank(request.DisplayNameOverride),
            IsSystemNoise = request.IsSystemNoise,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<MobileAppCategoryRuleEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<MobileAppCategoryRuleDto> UpdateCategoryRuleAsync(
        string id,
        MobileAppCategoryRuleUpsertRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var ruleId = ParseRuleId(id);
        var entity = await _db.Set<MobileAppCategoryRuleEntity>()
            .SingleOrDefaultAsync(rule => rule.UserId == userId && rule.Id == ruleId, ct)
            ?? throw new KeyNotFoundException($"Mobile category rule '{id}' was not found.");

        entity.RuleType = NormalizeRuleType(request.RuleType);
        entity.Pattern = NormalizePattern(request.Pattern);
        entity.LifeCategory = NormalizeLifeCategory(request.LifeCategory);
        entity.Priority = request.Priority;
        entity.IsEnabled = request.IsEnabled;
        entity.DisplayNameOverride = NullIfBlank(request.DisplayNameOverride);
        entity.IsSystemNoise = request.IsSystemNoise;
        entity.UpdatedAt = _timeProvider.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteCategoryRuleAsync(string id, CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var ruleId = ParseRuleId(id);
        var entity = await _db.Set<MobileAppCategoryRuleEntity>()
            .SingleOrDefaultAsync(rule => rule.UserId == userId && rule.Id == ruleId, ct);

        if (entity is null)
            return false;

        _db.Set<MobileAppCategoryRuleEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<MobileAnalyticsStaleMarkResult> MarkAnalyticsStaleAsync(
        string packageName,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default)
    {
        if (rangeEndUtc <= rangeStartUtc)
            throw new ArgumentException("Range end must be after range start.", nameof(rangeEndUtc));

        var userId = MobileUserContext.RequireUserId(_currentUser);
        var normalizedPackageName = NormalizePackageName(packageName);
        var now = _timeProvider.GetUtcNow();

        var aggregates = await _db.Set<MobileUsageAggregateEntity>()
            .Where(a => a.UserId == userId
                && a.PackageName == normalizedPackageName
                && a.BucketStartUtc < rangeEndUtc
                && a.BucketEndUtc > rangeStartUtc)
            .ToListAsync(ct);

        var aggregateCount = 0;
        foreach (var aggregate in aggregates.Where(a => !a.IsStale))
        {
            aggregate.IsStale = true;
            aggregate.UpdatedAt = now;
            aggregateCount++;
        }

        var candidateBlocks = await _db.Set<MobileTimelineBlockEntity>()
            .Where(block => block.UserId == userId
                && block.StartUtc < rangeEndUtc
                && block.EndUtc > rangeStartUtc)
            .ToListAsync(ct);

        var timelineCount = 0;
        foreach (var block in candidateBlocks.Where(block => !block.IsStale
            && TopAppsJsonContainsPackage(block.TopAppsJson, normalizedPackageName)))
        {
            block.IsStale = true;
            block.UpdatedAt = now;
            timelineCount++;
        }

        if (aggregateCount > 0 || timelineCount > 0)
            await _db.SaveChangesAsync(ct);

        return new MobileAnalyticsStaleMarkResult(aggregateCount, timelineCount);
    }

    private static MobileAppCatalogOverrideDto ToDto(MobileAppCatalogOverrideEntity entity)
        => new(
            entity.PackageName,
            entity.DisplayNameOverride,
            entity.LifeCategory,
            entity.IsSystemNoise,
            entity.HideShortEvents,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static MobileAppCategoryRuleDto ToDto(MobileAppCategoryRuleEntity entity)
        => new(
            entity.Id.ToString(),
            entity.RuleType,
            entity.Pattern,
            entity.LifeCategory,
            entity.Priority,
            entity.IsEnabled,
            entity.DisplayNameOverride,
            entity.IsSystemNoise,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static Guid ParseRuleId(string id)
        => Guid.TryParse(id, out var value)
            ? value
            : throw new ArgumentException("Rule id must be a GUID.", nameof(id));

    private static string NormalizePackageName(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Package name is required.", nameof(value));

        return normalized;
    }

    private static string NormalizePattern(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Rule pattern is required.", nameof(value));

        return normalized;
    }

    private static string NormalizeRuleType(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedRuleTypes.Contains(normalized))
            throw new ArgumentException($"Unsupported mobile category rule type: {value}.", nameof(value));

        return normalized;
    }

    private static string NormalizeLifeCategory(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!MobileLifeCategories.All.Contains(normalized)
            && !string.Equals(normalized, MobileLifeCategories.ToolsSystem, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported mobile life category: {value}.", nameof(value));

        return normalized;
    }

    private static string? NullIfBlank(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool TopAppsJsonContainsPackage(string json, string packageName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(packageName))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        if (string.Equals(element.GetString(), packageName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    else if (element.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in element.EnumerateObject())
                        {
                            if (prop.NameEquals("packageName"u8) || prop.NameEquals("PackageName"u8) || string.Equals(prop.Name, "package_name", StringComparison.OrdinalIgnoreCase))
                            {
                                if (prop.Value.ValueKind == JsonValueKind.String
                                    && string.Equals(prop.Value.GetString(), packageName, StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                        }
                    }
                }

                return false;
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("packageName"u8) || prop.NameEquals("PackageName"u8) || string.Equals(prop.Name, "package_name", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String
                            && string.Equals(prop.Value.GetString(), packageName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            return false;
        }
        catch (JsonException ex)
        {
            // Keep not stale on malformed JSON but retain observability for diagnostics
            System.Diagnostics.Debug.WriteLine($"TopAppsJson parse failed for package '{packageName}': {ex.Message}");
            return false;
        }
    }
}

public sealed record MobileAnalyticsStaleMarkResult(int AggregatesMarked, int TimelineBlocksMarked);
