using System.Text.Json;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public static class BrowserPageTimelineBuilder
{
    private const double ShortPageThresholdSeconds = 5;

    private static readonly HashSet<string> BrowserAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge",
        "chrome",
        "firefox",
        "brave",
        "opera"
    };

    private static readonly Dictionary<string, string> BrowserBucketTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edge"] = "msedge",
        ["msedge"] = "msedge",
        ["chrome"] = "chrome",
        ["firefox"] = "firefox",
        ["brave"] = "brave",
        ["opera"] = "opera"
    };

    public static List<PcDetailRecord> BuildInterpretedAwRecords(
        List<AwEventEntity> awEvents,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules)
    {
        var records = new List<PcDetailRecord>();

        foreach (var deviceGroup in awEvents.GroupBy(e => e.DeviceId))
        {
            var deviceEvents = deviceGroup
                .OrderBy(e => e.Timestamp)
                .ThenBy(e => e.SourceEventId ?? e.Id)
                .ToList();
            var webEvents = deviceEvents
                .Where(IsWebEvent)
                .OrderBy(e => e.Timestamp)
                .ThenBy(e => e.SourceEventId ?? e.Id)
                .ToList();
            var webPages = BuildWebPageClusters(webEvents, rules)
                .Select(page => page.ToDetailPage(deviceEvents))
                .ToList();
            var explainedBrowserWindows = new HashSet<AwEventEntity>(ReferenceEqualityComparer.Instance);

            foreach (var page in webPages)
            {
                if (page.BrowserWindow is not null)
                    explainedBrowserWindows.Add(page.BrowserWindow);
            }

            var nonWebRecords = deviceEvents
                .Where(e => !IsWebEvent(e))
                .Where(e => !explainedBrowserWindows.Contains(e))
                .Select(e => ToRawAwRecord(e, rules));

            records.AddRange(webPages.Select(page => page.Record));
            records.AddRange(nonWebRecords);
        }

        return records
            .OrderBy(r => r.Start, StringComparer.Ordinal)
            .ToList();
    }

    public static PcDetailRecord ToRawAwRecord(AwEventEntity e, IReadOnlyCollection<ActivityCategoryRuleEntity> rules)
    {
        var normalizedApp = AppNameNormalizer.Normalize(e.AppNameNormalized ?? e.AppName);
        var webData = IsWebEvent(e) ? ParseWebData(e) : null;
        var recordType = IsWebEvent(e) ? "web" : e.EventType;
        var classification = ActivityClassifier.Classify(
            new ActivityClassificationContext(
                recordType,
                e.AppName,
                normalizedApp,
                webData?.Domain,
                webData?.Path,
                webData?.Title ?? e.WindowTitle,
                e.WindowTitle,
                webData?.IsLocalFile == true ? webData.Path : null,
                e.BucketType),
            rules);

        return new PcDetailRecord(
            recordType,
            FormatUtc(e.Timestamp),
            FormatUtc(e.Timestamp.AddSeconds(e.Duration)),
            e.Duration,
            e.DeviceId,
            e.AppName,
            normalizedApp,
            classification.CategoryName,
            webData?.Title ?? e.WindowTitle,
            null,
            null,
            null,
            null,
            null,
            ParseJsonObject(e.DataJson),
            webData?.Url,
            webData?.Domain,
            webData?.Path,
            webData?.IsLocalFile ?? false,
            null,
            null,
            webData?.Audible,
            webData?.Incognito,
            webData?.TabCount,
            SourceWebEventIds: IsWebEvent(e) ? SourceIds(new[] { e }) : null,
            SourceWindowEventIds: string.Equals(recordType, "window", StringComparison.Ordinal)
                ? SourceIds(new[] { e })
                : null,
            CategoryColor: classification.CategoryColor,
            ProjectTag: classification.ProjectTag,
            ClassificationConfidence: classification.Confidence,
            ClassificationSource: classification.Source,
            ClassificationExplanation: classification.Explanation);
    }

    private static List<WebPageCluster> BuildWebPageClusters(
        List<AwEventEntity> webEvents,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules)
    {
        var clusters = new List<WebPageCluster>();
        var pendingShortEvents = new List<AwEventEntity>();

        foreach (var webEvent in webEvents)
        {
            if (webEvent.Duration <= ShortPageThresholdSeconds)
            {
                pendingShortEvents.Add(webEvent);
                continue;
            }

            var leadingShortEvents = pendingShortEvents;
            pendingShortEvents = new List<AwEventEntity>();
            clusters.Add(new WebPageCluster(webEvent, leadingShortEvents, new List<AwEventEntity>(), rules));
        }

        if (pendingShortEvents.Count > 0 && clusters.Count > 0)
        {
            var previous = clusters[^1];
            clusters[^1] = previous with { TrailingShortEvents = pendingShortEvents };
        }
        else if (pendingShortEvents.Count > 0)
        {
            clusters.Add(WebPageCluster.FromShortEvents(pendingShortEvents, rules));
        }

        return clusters;
    }

    private sealed record WebPageCluster(
        AwEventEntity Primary,
        List<AwEventEntity> LeadingShortEvents,
        List<AwEventEntity> TrailingShortEvents,
        IReadOnlyCollection<ActivityCategoryRuleEntity> Rules)
    {
        public static WebPageCluster FromShortEvents(
            List<AwEventEntity> shortEvents,
            IReadOnlyCollection<ActivityCategoryRuleEntity> rules)
        {
            return new WebPageCluster(shortEvents[^1], shortEvents, new List<AwEventEntity>(), rules);
        }

        public WebPageDetail ToDetailPage(List<AwEventEntity> awEvents)
        {
            var allWebEvents = LeadingShortEvents
                .Concat(new[] { Primary })
                .Concat(TrailingShortEvents)
                .DistinctBy(e => e)
                .OrderBy(e => e.Timestamp)
                .ThenBy(e => e.SourceEventId ?? e.Id)
                .ToList();
            var start = allWebEvents.Min(e => e.Timestamp);
            var end = allWebEvents.Max(e => e.Timestamp.AddSeconds(e.Duration));
            var data = ParseWebData(Primary);
            var browserName = InferBrowserName(Primary) ?? InferBrowserName(allWebEvents);
            var browserWindows = awEvents
                .Where(IsBrowserWindowEvent)
                .Where(e => Overlaps(start, end, e.Timestamp, e.Timestamp.AddSeconds(e.Duration)))
                .ToList();
            var browserWindow = browserWindows
                .Where(e => browserName is null || string.Equals(
                    AppNameNormalizer.Normalize(e.AppNameNormalized ?? e.AppName),
                    browserName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => OverlapSeconds(start, end, e.Timestamp, e.Timestamp.AddSeconds(e.Duration)))
                .ThenBy(e => e.Timestamp)
                .FirstOrDefault()
                ?? browserWindows
                    .OrderByDescending(e => OverlapSeconds(start, end, e.Timestamp, e.Timestamp.AddSeconds(e.Duration)))
                    .ThenBy(e => e.Timestamp)
                    .FirstOrDefault();
            var displayName = data.Domain ?? (data.IsLocalFile ? "文件" : null);
            var absorbedShortEvents = allWebEvents
                .Where(e => e.Duration <= ShortPageThresholdSeconds)
                .ToList();
            var normalizedBrowserApp = browserWindow is null
                ? browserName
                : AppNameNormalizer.Normalize(browserWindow.AppNameNormalized ?? browserWindow.AppName);
            var classification = ActivityClassifier.Classify(
                new ActivityClassificationContext(
                    "web-page",
                    browserWindow?.AppName,
                    normalizedBrowserApp,
                    data.Domain,
                    data.Path,
                    data.Title ?? Primary.WindowTitle,
                    browserWindow?.WindowTitle,
                    data.IsLocalFile ? data.Path : null,
                    Primary.BucketType),
                Rules);

            var record = new PcDetailRecord(
                "web-page",
                FormatUtc(start),
                FormatUtc(end),
                (end - start).TotalSeconds,
                Primary.DeviceId,
                null,
                displayName,
                classification.CategoryName,
                data.Title ?? Primary.WindowTitle,
                null,
                null,
                null,
                null,
                null,
                ParseJsonObject(Primary.DataJson),
                data.Url,
                data.Domain,
                data.Path,
                data.IsLocalFile,
                browserWindow?.AppName,
                browserWindow?.WindowTitle,
                data.Audible,
                data.Incognito,
                data.TabCount,
                absorbedShortEvents.Count,
                absorbedShortEvents.Sum(e => e.Duration),
                SourceIds(allWebEvents),
                browserWindow is null ? null : SourceIds(new[] { browserWindow }),
                classification.CategoryColor,
                classification.ProjectTag,
                classification.Confidence,
                classification.Source,
                classification.Explanation);
            return new WebPageDetail(record, browserWindow);
        }
    }

    private sealed record WebPageDetail(PcDetailRecord Record, AwEventEntity? BrowserWindow);

    private static bool IsWebEvent(AwEventEntity e)
    {
        return string.Equals(e.EventType, "web", StringComparison.Ordinal)
            || string.Equals(e.BucketType, "web.tab.current", StringComparison.Ordinal);
    }

    private static bool IsBrowserWindowEvent(AwEventEntity e)
    {
        if (!string.Equals(e.EventType, "window", StringComparison.Ordinal))
            return false;

        var normalized = AppNameNormalizer.Normalize(e.AppNameNormalized ?? e.AppName);
        return BrowserAppNames.Contains(normalized);
    }

    private static string? InferBrowserName(IEnumerable<AwEventEntity> webEvents)
    {
        foreach (var webEvent in webEvents)
        {
            var browserName = InferBrowserName(webEvent);
            if (browserName is not null)
                return browserName;
        }

        return null;
    }

    private static string? InferBrowserName(AwEventEntity webEvent)
    {
        return InferBrowserName(webEvent.BucketId) ?? InferBrowserName(webEvent.BucketClient);
    }

    private static string? InferBrowserName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (var pair in BrowserBucketTokens)
        {
            if (value.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static bool Overlaps(DateTimeOffset firstStart, DateTimeOffset firstEnd, DateTimeOffset secondStart, DateTimeOffset secondEnd)
    {
        return firstStart < secondEnd && secondStart < firstEnd;
    }

    private static double OverlapSeconds(DateTimeOffset firstStart, DateTimeOffset firstEnd, DateTimeOffset secondStart, DateTimeOffset secondEnd)
    {
        var start = firstStart > secondStart ? firstStart : secondStart;
        var end = firstEnd < secondEnd ? firstEnd : secondEnd;
        return end > start ? (end - start).TotalSeconds : 0;
    }

    private static WebPageData ParseWebData(AwEventEntity e)
    {
        var root = TryParseJson(e.DataJson);
        var url = GetString(root, "url");
        var title = GetString(root, "title") ?? e.WindowTitle;
        var uri = ParseUri(url);

        return new WebPageData(
            url,
            uri?.IsFile == true ? null : uri?.Host,
            uri?.IsFile == true ? uri.LocalPath : uri?.PathAndQuery,
            uri is not null && uri.IsFile,
            title,
            GetBool(root, "audible"),
            GetBool(root, "incognito"),
            GetInt(root, "tabCount"));
    }

    private static Uri? ParseUri(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static JsonElement? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement? root, string key)
    {
        return root is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(key, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static bool? GetBool(JsonElement? root, string key)
    {
        return root is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(key, out var property)
            && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
                ? property.GetBoolean()
                : null;
    }

    private static int? GetInt(JsonElement? root, string key)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(key, out var property))
            return null;

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static object? ParseJsonObject(string? json)
    {
        var parsed = TryParseJson(json);
        return parsed;
    }

    private static List<long> SourceIds(IEnumerable<AwEventEntity> events)
    {
        return events
            .Select(e => e.SourceEventId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    private static DateTimeOffset ParseRecordTime(string? value)
    {
        return DateTimeOffset.Parse(value ?? throw new InvalidOperationException("Record timestamp is required."));
    }

    private static string FormatUtc(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O");
    }

    private sealed record WebPageData(
        string? Url,
        string? Domain,
        string? Path,
        bool IsLocalFile,
        string? Title,
        bool? Audible,
        bool? Incognito,
        int? TabCount);
}
