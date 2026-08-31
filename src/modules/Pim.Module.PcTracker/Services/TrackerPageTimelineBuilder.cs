using System.Text.Json;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public static class TrackerPageTimelineBuilder
{
    private const double ShortPageThresholdSeconds = 5;
    private const double MaxShortPageMergeGapSeconds = 30;

    private static readonly HashSet<string> BrowserAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera"
    };

    public static List<PcDetailRecord> BuildInterpretedRecords(
        List<TrackerEventEntity> events,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules)
    {
        // Direct conversion: each tracker event becomes a record
        // For window events with url/domain, treat as web-page if present
        var records = new List<PcDetailRecord>();
        foreach (var e in events.OrderBy(x => x.Timestamp))
        {
            records.Add(ToRecord(e, rules));
        }

        // Merge short web pages similar to aw builder
        return MergeShortWebPages(records);
    }

    public static PcDetailRecord ToRecord(TrackerEventEntity e, IReadOnlyCollection<ActivityCategoryRuleEntity> rules)
    {
        var isWebPage = string.Equals(e.EventType, "web-page", StringComparison.OrdinalIgnoreCase);
        var recordType = isWebPage ? "web-page" : e.EventType;
        var displayName = isWebPage ? (e.Domain ?? e.DisplayName ?? e.AppName) : e.DisplayName;
        var isLocalFile = false;
        if (isWebPage && !string.IsNullOrWhiteSpace(e.Url) && Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
            isLocalFile = uri.IsFile;
        var appNameForClassification = AppNameNormalizer.Normalize(e.AppName ?? e.DisplayName);
        var classification = ActivityClassifier.Classify(
            new ActivityClassificationContext(
                recordType,
                e.AppName,
                appNameForClassification,
                e.Domain,
                e.PagePath,
                e.WindowTitle,
                e.WindowTitle,
                null,
                null),
            rules);

        var start = FormatUtc(e.Timestamp);
        var end = FormatUtc(e.Timestamp.AddSeconds(e.Duration));

        return new PcDetailRecord(
            RecordType: recordType,
            Start: start,
            End: end,
            DurationSeconds: e.Duration,
            DeviceId: e.DeviceId,
            AppName: e.AppName,
            DisplayName: displayName,
            CategoryName: classification.CategoryName,
            Title: e.WindowTitle,
            KeyPresses: null,
            TotalClicks: null,
            MouseDistance: null,
            ScrollDistance: null,
            KeyCounts: null,
            Raw: ParseJsonObject(e.RawJson),
            Url: e.Url,
            Domain: e.Domain,
            Path: e.PagePath,
            IsLocalFile: isLocalFile,
            BrowserAppName: e.AppName,
            BrowserWindowTitle: e.WindowTitle,
            Audible: e.Audible,
            Incognito: e.Incognito,
            TabCount: e.TabCount,
            AbsorbedShortEventsCount: e.PageVisitCount,
            AbsorbedDurationSeconds: e.PageVisitDuration,
            SourceWebEventIds: null,
            SourceWindowEventIds: null,
            CategoryColor: classification.CategoryColor,
            ProjectTag: classification.ProjectTag,
            ClassificationConfidence: classification.Confidence,
            ClassificationSource: classification.Source,
            ClassificationExplanation: classification.Explanation,
            BucketType: null,
            RecordKey: null,
            RecordKeyVersion: null,
            RecordKeyStability: null,
            SourceBucketIds: null,
            SourceType: "tracker",
            InterpretationVersion: "interpreted-tracker-v1");
    }

    public static PcDetailRecord ToRawTrackerRecord(TrackerEventEntity e, IReadOnlyCollection<ActivityCategoryRuleEntity> rules)
    {
        return ToRecord(e, rules);
    }

    private static List<PcDetailRecord> MergeShortWebPages(List<PcDetailRecord> records)
    {
        // Simple merge: if consecutive short web-page records (<5s) within 30s gap, merge into next longer page
        var result = new List<PcDetailRecord>();
        var pendingShort = new List<PcDetailRecord>();

        foreach (var r in records.OrderBy(x => DateTimeOffset.Parse(x.Start)))
        {
            if (r.RecordType == "web-page" && (r.DurationSeconds ?? 0) <= ShortPageThresholdSeconds)
            {
                if (pendingShort.Count > 0)
                {
                    var lastEnd = DateTimeOffset.Parse(pendingShort[^1].End ?? pendingShort[^1].Start);
                    var curStart = DateTimeOffset.Parse(r.Start);
                    if ((curStart - lastEnd).TotalSeconds > MaxShortPageMergeGapSeconds)
                    {
                        FlushPendingShort(pendingShort, result);
                        pendingShort = new List<PcDetailRecord>();
                    }
                }
                pendingShort.Add(r);
                continue;
            }

            if (r.RecordType == "web-page" && pendingShort.Count > 0)
            {
                // merge leading shorts that are adjacent
                var leading = TakeAdjacentSuffix(pendingShort, DateTimeOffset.Parse(r.Start));
                var remaining = pendingShort.Where(x => !leading.Contains(x)).ToList();
                FlushPendingShort(remaining, result);
                pendingShort.Clear();

                // Merge leading shorts into current
                if (leading.Count > 0)
                {
                    var all = leading.Append(r).ToList();
                    var start = all.Min(x => DateTimeOffset.Parse(x.Start));
                    var end = all.Max(x => DateTimeOffset.Parse(x.End ?? x.Start));
                    var merged = r with
                    {
                        Start = FormatUtc(start),
                        End = FormatUtc(end),
                        DurationSeconds = (end - start).TotalSeconds,
                        AbsorbedShortEventsCount = all.Count - 1,
                        AbsorbedDurationSeconds = leading.Sum(x => x.DurationSeconds ?? 0)
                    };
                    result.Add(merged);
                    continue;
                }
            }

            // flush any pending shorts if we encounter non-web page
            if (pendingShort.Count > 0 && r.RecordType != "web-page")
            {
                FlushPendingShort(pendingShort, result);
                pendingShort.Clear();
            }

            result.Add(r);
        }

        if (pendingShort.Count > 0)
        {
            if (result.Count > 0)
                FlushPendingShort(pendingShort, result);
            else
            {
                // only shorts: merge into one
                var start = pendingShort.Min(x => DateTimeOffset.Parse(x.Start));
                var end = pendingShort.Max(x => DateTimeOffset.Parse(x.End ?? x.Start));
                var primary = pendingShort[^1];
                var merged = primary with
                {
                    Start = FormatUtc(start),
                    End = FormatUtc(end),
                    DurationSeconds = (end - start).TotalSeconds,
                    AbsorbedShortEventsCount = pendingShort.Count - 1,
                    AbsorbedDurationSeconds = pendingShort.Take(pendingShort.Count - 1).Sum(x => x.DurationSeconds ?? 0)
                };
                result.Add(merged);
            }
        }

        return result.OrderBy(x => x.Start, StringComparer.Ordinal).ToList();
    }

    private static void FlushPendingShort(List<PcDetailRecord> shorts, List<PcDetailRecord> result)
    {
        foreach (var s in shorts)
            result.Add(s);
    }

    private static List<PcDetailRecord> TakeAdjacentSuffix(List<PcDetailRecord> shorts, DateTimeOffset nextStart)
    {
        var adjacent = new List<PcDetailRecord>();
        var cursor = nextStart;
        for (var i = shorts.Count - 1; i >= 0; i--)
        {
            var s = shorts[i];
            var sEnd = DateTimeOffset.Parse(s.End ?? s.Start);
            if (sEnd <= cursor && (cursor - sEnd).TotalSeconds <= MaxShortPageMergeGapSeconds)
            {
                adjacent.Add(s);
                cursor = DateTimeOffset.Parse(s.Start);
            }
            else if (sEnd > cursor)
            {
                adjacent.Add(s);
                cursor = DateTimeOffset.Parse(s.Start);
            }
            else
                break;
        }
        adjacent.Reverse();
        return adjacent;
    }

    private static string FormatUtc(DateTimeOffset ts) => ts.ToUniversalTime().ToString("O");

    private static object? ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return null; }
    }
}
