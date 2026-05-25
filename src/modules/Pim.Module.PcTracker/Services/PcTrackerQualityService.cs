using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public sealed class PcTrackerQualityService
{
    private static readonly TimeSpan StaleBucketAge = TimeSpan.FromHours(24);
    private readonly PimDbContext _db;

    public PcTrackerQualityService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<PcQualityResponse> GetQualityAsync(DateTime? date, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var (rangeStart, rangeEnd) = GetRange(date, dateFrom, dateTo);

        var buckets = await _db.Set<AwBucketEntity>()
            .AsNoTracking()
            .ToListAsync(ct);

        var events = await _db.Set<AwEventEntity>()
            .AsNoTracking()
            .Where(e => e.Timestamp >= rangeStart && e.Timestamp < rangeEnd)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

        var samples = await _db.Set<KeystatsSampleEntity>()
            .AsNoTracking()
            .Where(s => s.SampledAtUtc >= rangeStart && s.SampledAtUtc < rangeEnd)
            .OrderBy(s => s.PimDeviceId)
            .ThenBy(s => s.SampledAtUtc)
            .ToListAsync(ct);

        var heartbeat = await _db.Set<DaemonHeartbeatEntity>()
            .AsNoTracking()
            .Where(h => h.DaemonKind == "windows")
            .OrderByDescending(h => h.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        var issues = new List<PcQualityIssueDto>();
        var components = new List<PcQualityComponentDto>
        {
            CheckBuckets(buckets, checkedAt, issues),
            CheckEvents(events, issues),
            CheckKeystats(samples, issues),
            CheckDaemon(heartbeat, checkedAt, issues),
            CheckTimeline(events, samples, issues)
        };

        var overallStatus = components
            .Select(c => c.Status)
            .OrderByDescending(GetSeverityRank)
            .FirstOrDefault();

        return new PcQualityResponse(
            overallStatus,
            GetLabel(overallStatus),
            GetMessage(overallStatus),
            checkedAt,
            components,
            issues,
            issues
                .Select(i => i.NextStep)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .Cast<string>()
                .ToList());
    }

    private static (DateTimeOffset Start, DateTimeOffset End) GetRange(DateTime? date, DateTime? dateFrom, DateTime? dateTo)
    {
        var from = dateFrom ?? date ?? DateTime.Today;
        var to = dateTo ?? date ?? from;

        if (to < from)
        {
            (from, to) = (to, from);
        }

        var start = PcTrackerService.GetBusinessDayStartForQuery(from);
        var end = PcTrackerService.GetBusinessDayStartForQuery(to.Date.AddDays(1));
        return (start, end);
    }

    private static PcQualityComponentDto CheckBuckets(
        IReadOnlyCollection<AwBucketEntity> buckets,
        DateTimeOffset checkedAt,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();

        if (!HasBucketType(buckets, "currentwindow"))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-window-bucket",
                PimHealthStatus.Critical,
                "aw-buckets",
                "ActivityWatch window bucket is missing.",
                "Start or reconnect the ActivityWatch window watcher."));
        }

        if (!HasBucketType(buckets, "afkstatus"))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-afk-bucket",
                PimHealthStatus.Warning,
                "aw-buckets",
                "ActivityWatch AFK bucket is missing.",
                "Start or reconnect the ActivityWatch AFK watcher."));
        }

        if (!HasBucketType(buckets, "web.tab.current"))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-web-bucket",
                PimHealthStatus.Warning,
                "aw-buckets",
                "ActivityWatch web bucket is missing.",
                "Install or reconnect the browser ActivityWatch extension."));
        }

        var staleBuckets = buckets.Count(b => checkedAt - b.SeenAt > StaleBucketAge);
        if (staleBuckets > 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "stale-aw-bucket",
                PimHealthStatus.Warning,
                "aw-buckets",
                "One or more ActivityWatch buckets have not been seen recently.",
                "Restart ActivityWatch watchers and confirm uploads resume."));
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["bucketCount"] = buckets.Count.ToString(),
            ["staleBucketCount"] = staleBuckets.ToString()
        };

        return BuildComponent("aw-buckets", "ActivityWatch buckets", componentIssues, details);
    }

    private static PcQualityComponentDto CheckEvents(IReadOnlyCollection<AwEventEntity> events, List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();

        if (events.Count == 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-aw-events",
                PimHealthStatus.Warning,
                "aw-events",
                "No ActivityWatch events were captured for the selected range.",
                "Confirm ActivityWatch data is being uploaded."));
        }
        else
        {
            if (!events.Any(IsWindowEvent))
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "missing-aw-window-events",
                    PimHealthStatus.Warning,
                    "aw-events",
                    "No ActivityWatch window events were captured for the selected range.",
                    "Confirm the window watcher is running."));
            }

            if (!events.Any(IsAfkEvent))
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "missing-aw-afk-events",
                    PimHealthStatus.Warning,
                    "aw-events",
                    "No ActivityWatch AFK events were captured for the selected range.",
                    "Confirm the AFK watcher is running."));
            }

            var missingSourceIds = events.Count(e => e.SourceEventId is null);
            if (missingSourceIds > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "aw-events-missing-source-id",
                    MajoritySeverity(missingSourceIds, events.Count),
                    "aw-events",
                    "Some ActivityWatch events are missing source event ids.",
                    "Re-upload ActivityWatch events from the daemon."));
            }

            var invalidJson = events.Count(e => string.IsNullOrWhiteSpace(e.DataJson) || !IsValidJson(e.DataJson));
            if (invalidJson > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "aw-events-invalid-data-json",
                    MajoritySeverity(invalidJson, events.Count),
                    "aw-events",
                    "Some ActivityWatch events have missing or invalid data_json.",
                    "Check daemon serialization and re-upload affected events."));
            }
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["eventCount"] = events.Count.ToString(),
            ["windowEventCount"] = events.Count(IsWindowEvent).ToString(),
            ["afkEventCount"] = events.Count(IsAfkEvent).ToString()
        };

        return BuildComponent("aw-events", "ActivityWatch events", componentIssues, details);
    }

    private static PcQualityComponentDto CheckKeystats(
        IReadOnlyCollection<KeystatsSampleEntity> samples,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var gaps = 0;
        var resets = 0;

        if (samples.Count == 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "missing-keystats-samples",
                PimHealthStatus.Critical,
                "keystats-samples",
                "No KeyStats samples were captured for the selected range.",
                "Start KeyStats collection and confirm daemon uploads."));
        }
        else
        {
            foreach (var group in samples.GroupBy(s => s.PimDeviceId))
            {
                KeystatsSampleEntity? previous = null;
                foreach (var sample in group.OrderBy(s => s.SampledAtUtc))
                {
                    var delta = KeystatsDeltaCalculator.Calculate(previous, sample);
                    if (previous is not null && delta.IsGap)
                    {
                        gaps++;
                    }

                    if (delta.IsReset)
                    {
                        resets++;
                    }

                    previous = sample;
                }
            }

            if (gaps > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "keystats-sample-gap",
                    PimHealthStatus.Warning,
                    "keystats-samples",
                    "KeyStats samples contain collection gaps.",
                    "Keep the Windows daemon running continuously."));
            }

            if (resets > 0)
            {
                componentIssues.Add(new PcQualityIssueDto(
                    "keystats-counter-reset",
                    PimHealthStatus.Warning,
                    "keystats-samples",
                    "KeyStats counters reset within the selected range.",
                    "Check whether KeyStats or the daemon restarted."));
            }
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["sampleCount"] = samples.Count.ToString(),
            ["gapCount"] = gaps.ToString(),
            ["resetCount"] = resets.ToString()
        };

        return BuildComponent("keystats-samples", "KeyStats samples", componentIssues, details);
    }

    private static PcQualityComponentDto CheckDaemon(
        DaemonHeartbeatEntity? heartbeat,
        DateTimeOffset checkedAt,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var details = new Dictionary<string, string>();

        if (heartbeat is null)
        {
            details["heartbeat"] = "missing";
            componentIssues.Add(new PcQualityIssueDto(
                "missing-windows-daemon-heartbeat",
                PimHealthStatus.Unknown,
                "daemon-upload",
                "Windows daemon heartbeat has not been received.",
                "Start and log in to the Windows daemon."));
            issues.AddRange(componentIssues);
            return BuildComponent("daemon-upload", "Windows daemon upload", componentIssues, details);
        }

        var age = checkedAt - heartbeat.ReceivedAt;
        details["receivedAt"] = heartbeat.ReceivedAt.ToString("O");
        details["ageMinutes"] = Math.Max(0, age.TotalMinutes).ToString("0.0");
        details["uploadQueueCount"] = (heartbeat.UploadQueueCount ?? 0).ToString();
        details["activityWatchState"] = heartbeat.ActivityWatchState;
        details["keyStatsState"] = heartbeat.KeyStatsState;

        if (age >= TimeSpan.FromMinutes(60))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "stale-windows-daemon-heartbeat",
                PimHealthStatus.Critical,
                "daemon-upload",
                "Windows daemon heartbeat is stale.",
                "Restart the Windows daemon and verify it can reach the API."));
        }
        else if (age >= TimeSpan.FromMinutes(10))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "old-daemon-heartbeat",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows daemon heartbeat is old.",
                "Check whether the Windows daemon is still running."));
        }

        if (!string.IsNullOrWhiteSpace(heartbeat.LastError))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-last-error",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows daemon reported a recent error.",
                "Open daemon diagnostics and resolve the last error."));
        }

        if (heartbeat.UploadQueueCount.GetValueOrDefault() > 0)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-upload-queue",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows daemon has queued uploads.",
                "Verify the API is reachable from the Windows daemon."));
        }

        if (IsSourceUnavailable(heartbeat.ActivityWatchState) || IsSourceUnavailable(heartbeat.KeyStatsState))
        {
            componentIssues.Add(new PcQualityIssueDto(
                "daemon-source-unavailable",
                PimHealthStatus.Warning,
                "daemon-upload",
                "Windows daemon reported a collection source unavailable.",
                "Start unavailable collection sources on the PC."));
        }

        issues.AddRange(componentIssues);
        return BuildComponent("daemon-upload", "Windows daemon upload", componentIssues, details);
    }

    private static PcQualityComponentDto CheckTimeline(
        IReadOnlyCollection<AwEventEntity> events,
        IReadOnlyCollection<KeystatsSampleEntity> samples,
        List<PcQualityIssueDto> issues)
    {
        var componentIssues = new List<PcQualityIssueDto>();
        var hasActivityWatchEvents = events.Count > 0;
        var hasKeystatsSamples = samples.Count > 0;
        var hasKeystatsDeltaPair = samples
            .GroupBy(s => s.PimDeviceId)
            .Any(g => g.Count() >= 2);

        if (!hasActivityWatchEvents || !hasKeystatsSamples)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "timeline-inputs-incomplete",
                PimHealthStatus.Warning,
                "interpreted-timeline",
                "Interpreted timeline inputs are incomplete for the selected range.",
                "Resolve ActivityWatch and KeyStats collection issues first."));
        }
        else if (!hasKeystatsDeltaPair)
        {
            componentIssues.Add(new PcQualityIssueDto(
                "keystats-insufficient-samples",
                PimHealthStatus.Warning,
                "interpreted-timeline",
                "KeyStats has too few samples to build input timeline deltas.",
                "Collect at least two KeyStats samples from the same device."));
        }

        issues.AddRange(componentIssues);
        var details = new Dictionary<string, string>
        {
            ["hasActivityWatchEvents"] = hasActivityWatchEvents.ToString(),
            ["hasKeystatsSamples"] = hasKeystatsSamples.ToString(),
            ["hasKeystatsDeltaPair"] = hasKeystatsDeltaPair.ToString()
        };

        return BuildComponent("interpreted-timeline", "Interpreted timeline", componentIssues, details);
    }

    private static bool HasBucketType(IEnumerable<AwBucketEntity> buckets, string bucketType)
        => buckets.Any(b => string.Equals(b.BucketType, bucketType, StringComparison.OrdinalIgnoreCase));

    private static bool IsWindowEvent(AwEventEntity e)
        => string.Equals(e.EventType, "window", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.BucketType, "currentwindow", StringComparison.OrdinalIgnoreCase);

    private static bool IsAfkEvent(AwEventEntity e)
        => string.Equals(e.EventType, "afk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.BucketType, "afkstatus", StringComparison.OrdinalIgnoreCase);

    private static PimHealthStatus MajoritySeverity(int count, int total)
        => count > total / 2 ? PimHealthStatus.Critical : PimHealthStatus.Warning;

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSourceUnavailable(string state)
        => string.Equals(state, DaemonSourceState.Unavailable.ToString(), StringComparison.OrdinalIgnoreCase);

    private static PcQualityComponentDto BuildComponent(
        string key,
        string name,
        IReadOnlyCollection<PcQualityIssueDto> issues,
        IReadOnlyDictionary<string, string> details)
    {
        var status = issues.Count == 0
            ? PimHealthStatus.Healthy
            : issues.Select(i => i.Severity).OrderByDescending(GetSeverityRank).First();

        return new PcQualityComponentDto(key, name, status, ComponentMessage(status), details);
    }

    private static string ComponentMessage(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "Component looks healthy.",
            PimHealthStatus.Warning => "Component has collection quality warnings.",
            PimHealthStatus.Critical => "Component has critical collection quality issues.",
            _ => "Component quality is unknown."
        };

    private static string GetLabel(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "正常",
            PimHealthStatus.Warning => "有警告",
            PimHealthStatus.Critical => "故障",
            _ => "未知"
        };

    private static string GetMessage(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => "PC facts look complete for the selected range.",
            PimHealthStatus.Warning => "PC facts are usable, but some collection quality issues need attention.",
            PimHealthStatus.Critical => "PC facts are not reliable enough for the selected range.",
            _ => "PC facts quality cannot be fully determined yet."
        };

    private static int GetSeverityRank(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => 0,
            PimHealthStatus.Unknown => 1,
            PimHealthStatus.Warning => 2,
            PimHealthStatus.Critical => 3,
            _ => 0
        };
}
