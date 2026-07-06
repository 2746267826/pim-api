using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public sealed class AppKnowledgeSuggestionService
{
    private const string SuggestionSource = "app-knowledge-suggestion";

    private readonly PimDbContext _db;
    private readonly AppKnowledgeContextService _contexts;
    private readonly AppSignatureService _appSignatures;

    public AppKnowledgeSuggestionService(
        PimDbContext db,
        AppKnowledgeContextService contexts,
        AppSignatureService appSignatures)
    {
        _db = db;
        _contexts = contexts;
        _appSignatures = appSignatures;
    }

    public async Task<AppKnowledgeSuggestionPreviewDto> BuildRecommendedContextAsync(
        Guid suggestionId,
        SuggestionClassificationPreviewRequest request,
        ActivityClassificationPreviewDto? preview,
        CancellationToken ct)
    {
        var suggestion = await _db.Set<ActivityClassificationSuggestionEntity>()
            .FirstOrDefaultAsync(item => item.Id == suggestionId, ct)
            ?? throw new KeyNotFoundException($"Activity classification suggestion '{suggestionId}' was not found.");

        var context = SanitizedSuggestionContext.Parse(suggestion.SanitizedContextJson);
        var domainCandidates = context.Domains.Count > 0
            ? context.Domains
            : context.Urls.Select(ExtractDomainFromUrl).WhereNotNull().ToList();
        var processName = ResolveProcessName(suggestion.ClusterKey, context);
        var app = await FindAppSignatureAsync(processName, context.Apps, ct);
        if (app is not null)
            processName = app.ProcessName;

        var recommendedPattern = BuildRecommendedPattern(processName, domainCandidates, context.Titles);
        var recommended = BuildContextDto(
            suggestion,
            app,
            processName,
            recommendedPattern.PatternType,
            recommendedPattern.PatternValue,
            request,
            preview);

        var alternatives = new List<AppKnowledgeContextDto>();
        foreach (var domain in domainCandidates.Skip(1))
        {
            alternatives.Add(BuildContextDto(
                suggestion,
                app,
                processName,
                "domain",
                domain,
                request,
                preview));
        }

        foreach (var title in context.Titles)
        {
            alternatives.Add(BuildContextDto(
                suggestion,
                app,
                processName,
                "title",
                title,
                request,
                preview));
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            alternatives.Add(BuildContextDto(
                suggestion,
                app,
                processName,
                "app-default",
                processName,
                request,
                preview));
        }

        alternatives = Deduplicate(alternatives)
            .Where(item => !IsSamePattern(item, recommended))
            .ToList();

        return new AppKnowledgeSuggestionPreviewDto(
            suggestion.Id,
            recommended,
            alternatives,
            preview ?? BuildSuggestionImpactPreview(suggestion));
    }

    public async Task<AppKnowledgeContextDto> SaveRecommendedContextAsync(
        AppKnowledgeSuggestionPreviewDto suggestionPreview,
        CancellationToken ct)
    {
        var context = suggestionPreview.RecommendedContext;
        var saved = await _contexts.SaveAsync(new SaveAppKnowledgeContextRequest(
            context.AppId,
            context.ProcessName,
            context.PatternType,
            context.PatternValue,
            context.TargetCategoryName,
            context.ProjectTag,
            context.Confidence,
            context.Enabled), ct);

        var entity = await _db.Set<AppKnowledgeContextEntity>()
            .FirstAsync(item => item.Id == saved.Id, ct);
        entity.Source = SuggestionSource;
        entity.SourceSuggestionId = suggestionPreview.SuggestionId;
        entity.AffectedRecordCount = suggestionPreview.Preview.AffectedRecordCount;
        entity.AffectedDurationSeconds = suggestionPreview.Preview.AffectedDurationSeconds;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return AppKnowledgeContextService.ToDto(entity);
    }

    private static (string PatternType, string PatternValue) BuildRecommendedPattern(
        string processName,
        IReadOnlyList<string> domains,
        IReadOnlyList<string> titles)
    {
        if (domains.Count > 0)
            return ("domain", domains[0]);

        if (titles.Count > 0)
            return ("title", titles[0]);

        return ("app-default", processName);
    }

    private async Task<AppSignatureDto?> FindAppSignatureAsync(
        string processName,
        IReadOnlyList<string> appCandidates,
        CancellationToken ct)
    {
        var candidates = appCandidates
            .Prepend(processName)
            .SelectMany(AddExeVariant)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            return null;

        foreach (var candidate in candidates)
        {
            var app = await _appSignatures.FindByProcessNameAsync(candidate, ct);
            if (app is not null)
                return app;
        }

        var normalizedCandidates = candidates
            .Select(value => value.ToLowerInvariant())
            .ToList();

        var displayNameMatch = await _db.Set<AppSignatureEntity>()
            .Where(item => normalizedCandidates.Contains(item.DisplayName.ToLower()))
            .OrderBy(item => item.ProcessName)
            .FirstOrDefaultAsync(ct);

        return displayNameMatch is not null
            ? AppSignatureService.ToDto(displayNameMatch)
            : null;
    }

    private static IEnumerable<string> AddExeVariant(string value)
    {
        yield return value;
        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            yield return value + ".exe";
    }

    private static AppKnowledgeContextDto BuildContextDto(
        ActivityClassificationSuggestionEntity suggestion,
        AppSignatureDto? app,
        string processName,
        string patternType,
        string patternValue,
        SuggestionClassificationPreviewRequest request,
        ActivityClassificationPreviewDto? preview)
    {
        var normalizedPatternType = patternType.Trim().ToLowerInvariant();
        var normalizedPatternValue = patternValue.Trim();
        var targetCategory = TrimToNull(request.CategoryName) ?? TrimToNull(suggestion.SuggestedCategory) ?? TrimToNull(suggestion.CurrentCategory);
        var projectTag = TrimToNull(request.ProjectTag) ?? TrimToNull(suggestion.SuggestedProjectTag);
        var affectedRecordCount = preview?.AffectedRecordCount ?? suggestion.SampleCount;
        var affectedDurationSeconds = preview?.AffectedDurationSeconds ?? suggestion.TotalDurationSeconds;
        var appLabel = string.IsNullOrWhiteSpace(app?.DisplayName) ? processName : app.DisplayName;

        return new AppKnowledgeContextDto(
            Guid.Empty,
            app?.Id,
            processName,
            normalizedPatternType,
            normalizedPatternValue,
            targetCategory,
            projectTag,
            $"{appLabel} - {ToPatternLabel(normalizedPatternType)}: {normalizedPatternValue}",
            SuggestionSource,
            0.9,
            true,
            affectedRecordCount,
            affectedDurationSeconds,
            null);
    }

    private static string ResolveProcessName(
        string clusterKey,
        SanitizedSuggestionContext context)
    {
        var contextApp = context.Apps.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(contextApp))
            return contextApp;

        var parsed = ParseClusterKey(clusterKey);
        if (!string.IsNullOrWhiteSpace(parsed.Value))
            return parsed.Value;

        return string.IsNullOrWhiteSpace(clusterKey) ? "unknown-app" : clusterKey.Trim();
    }

    private static (string Kind, string Value) ParseClusterKey(string clusterKey)
    {
        if (string.IsNullOrWhiteSpace(clusterKey))
            return (string.Empty, string.Empty);

        var trimmed = clusterKey.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator > 0 && separator < trimmed.Length - 1)
            return (trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim());

        separator = trimmed.IndexOf('|');
        if (separator > 0)
            return (string.Empty, trimmed[..separator].Trim());

        return (string.Empty, trimmed);
    }

    private static ActivityClassificationPreviewDto BuildSuggestionImpactPreview(
        ActivityClassificationSuggestionEntity suggestion) =>
        new(
            suggestion.SampleCount,
            suggestion.TotalDurationSeconds,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            Array.Empty<PcDetailRecord>(),
            suggestion.SampleCount > 0,
            $"App Knowledge suggestion impact estimate: {suggestion.SampleCount} records, {suggestion.TotalDurationSeconds:R} seconds.");

    private static List<AppKnowledgeContextDto> Deduplicate(IEnumerable<AppKnowledgeContextDto> contexts) =>
        contexts
            .GroupBy(item => $"{item.PatternType}\u001f{item.PatternValue}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private static bool IsSamePattern(
        AppKnowledgeContextDto left,
        AppKnowledgeContextDto right) =>
        string.Equals(left.PatternType, right.PatternType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.PatternValue, right.PatternValue, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractDomainFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return null;
    }

    private static string ToPatternLabel(string patternType) => patternType switch
    {
        "app-default" => "app default",
        "url-path" => "URL path",
        "source-family" => "source family",
        _ => patternType
    };

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed record SanitizedSuggestionContext(
        IReadOnlyList<string> Apps,
        IReadOnlyList<string> Domains,
        IReadOnlyList<string> Titles,
        IReadOnlyList<string> Urls)
    {
        public static SanitizedSuggestionContext Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Empty;

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Empty;

                return new SanitizedSuggestionContext(
                    ReadValues(document.RootElement, "appName", "app", "processName", "apps"),
                    ReadValues(document.RootElement, "domain", "domains"),
                    ReadValues(document.RootElement, "title", "windowTitle", "titles"),
                    ReadValues(document.RootElement, "url", "urls"));
            }
            catch (JsonException)
            {
                return Empty;
            }
        }

        private static SanitizedSuggestionContext Empty { get; } =
            new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        private static IReadOnlyList<string> ReadValues(JsonElement root, params string[] names)
        {
            var values = new List<string>();
            foreach (var name in names)
            {
                if (!TryGetProperty(root, name, out var element))
                    continue;

                AddValues(element, values);
            }

            return values
                .Select(TrimToNull)
                .WhereNotNull()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static void AddValues(JsonElement element, List<string> values)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    AddValues(item, values);
                return;
            }

            var value = element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? element.ToString()
                    : null;
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
    }
}

internal static class AppKnowledgeSuggestionEnumerableExtensions
{
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source)
        where T : class
    {
        foreach (var item in source)
        {
            if (item is not null)
                yield return item;
        }
    }
}
