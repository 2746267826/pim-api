using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.Extensions.Logging;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class RecurrenceService
{
    private readonly ILogger<RecurrenceService> _logger;

    public RecurrenceService(ILogger<RecurrenceService> logger)
    {
        _logger = logger;
    }

    public virtual List<ExpandedEvent> ExpandEvents(
        IEnumerable<EventEntity> events,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        return ExpandEventsV2(events, rangeStart, rangeEnd);
    }

    private const int MaxOccurrences = 500;
    private const int MaxWindowDays = 1825;

    private DateTimeOffset GetEffectiveRangeEnd(DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        // Only cap unbounded windows (rangeEnd=MaxValue or rangeStart=MinValue).
        // Finite / normal events with explicit >5yr window must not be truncated.
        var isUnbounded = rangeEnd == DateTimeOffset.MaxValue || rangeStart == DateTimeOffset.MinValue;
        if (!isUnbounded) return rangeEnd;

        DateTimeOffset capped;
        if (rangeStart == DateTimeOffset.MinValue)
            capped = SafeAddDays(DateTimeOffset.UtcNow, MaxWindowDays);
        else
            capped = SafeAddDays(rangeStart, MaxWindowDays);

        _logger.LogWarning("[Recurrence V2] unbounded window capped: rangeStart={Start} rangeEnd={End} -> effectiveEnd={Capped} (MaxWindowDays={Days}, MaxOccurrences={Max})", rangeStart, rangeEnd, capped, MaxWindowDays, MaxOccurrences);
        return capped;
    }

    private static DateTimeOffset SafeAddDays(DateTimeOffset date, int days)
    {
        try
        {
            return date.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Overflow when date near MaxValue/MinValue — clamp to boundary
            return days > 0 ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
        }
    }

    public virtual List<ExpandedEvent> ExpandEventsV2(
        IEnumerable<EventEntity> events,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var effectiveRangeEnd = GetEffectiveRangeEnd(rangeStart, rangeEnd);
        var all = events.ToList();
        // normalize RecurrenceId to O format for stable matching
        string Normalize(string? v)
        {
            if (string.IsNullOrEmpty(v)) return v ?? string.Empty;
            return DateTimeOffset.TryParse(v, out var parsed) ? parsed.ToString("O") : v;
        }
        var exceptionsByMaster = all
            .Where(e => e.IsException && e.SeriesMasterId.HasValue && !string.IsNullOrEmpty(e.RecurrenceId))
            .GroupBy(e => e.SeriesMasterId!.Value)
            .ToDictionary(g => g.Key, g =>
            {
                var deduped = g.GroupBy(x => Normalize(x.RecurrenceId)!, StringComparer.Ordinal)
                    .ToDictionary(
                        grp => grp.Key,
                        grp =>
                        {
                            if (grp.Count() > 1)
                            {
                                _logger.LogWarning("[Recurrence V2] duplicate exception RecurrenceId {RecurrenceId} for master {MasterId}: {Count} entries, picking latest UpdatedAt", grp.Key, g.Key, grp.Count());
                            }
                            return grp.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.CreatedAt).First();
                        },
                        StringComparer.Ordinal);
                // ensure stored key is normalized and entity RecurrenceId normalized for overlay
                var normalized = new Dictionary<string, EventEntity>(StringComparer.Ordinal);
                foreach (var kv in deduped)
                {
                    var entity = kv.Value;
                    // normalize stored entity's RecurrenceId for consistency (in-memory only)
                    if (!string.IsNullOrEmpty(entity.RecurrenceId))
                    {
                        var n = Normalize(entity.RecurrenceId);
                        if (n != entity.RecurrenceId)
                            entity.RecurrenceId = n;
                    }
                    normalized[kv.Key] = entity;
                }
                return normalized;
            });

        var masterIds = new HashSet<Guid>(exceptionsByMaster.Keys);
        var results = new List<ExpandedEvent>();
        int recurring = 0, simple = 0, errors = 0, exceptionsApplied = 0;

        foreach (var entity in all)
        {
            // Skip exception rows themselves — they are rendered via overlay, not as standalone
            if (entity.IsException)
                continue;

            // Skip legacy Outlook occurrence rows that are not part of master model (stage A compatibility)
            if (entity.OutlookEventType == "occurrence" && !entity.IsSeriesMaster && entity.SeriesMasterId is null && !entity.IsException)
                continue;

            // Simple non-recurring
            if (string.IsNullOrEmpty(entity.RRule))
            {
                simple++;
                if (entity.DtEnd > rangeStart && entity.DtStart < effectiveRangeEnd)
                    results.Add(new ExpandedEvent(entity, entity.Id, entity.DtStart, entity.DtEnd, entity.RecurrenceId, entity.IsSeriesMaster, entity.IsException, entity.SeriesMasterId));
                continue;
            }

            // Recurring master (or legacy RRule without IsSeriesMaster flag)
            recurring++;
            var exDates = ParseExDates(entity.ExDatesJson);
            var expanded = ExpandRecurring(entity, rangeStart, effectiveRangeEnd, exDates);
            if (expanded.Count == 0 && entity.RRule is not null)
                errors++;

            // Overlay exceptions
            if (exceptionsByMaster.TryGetValue(entity.Id, out var map))
            {
                var overlaid = new List<ExpandedEvent>();
                foreach (var occ in expanded)
                {
                    var recurrenceId = occ.RecurrenceId ?? occ.OccurrenceStart.ToString("O");
                    if (map.TryGetValue(recurrenceId, out var exception))
                    {
                        exceptionsApplied++;
                        // Use exception entity's own time and status; keep original recurrenceId
                        overlaid.Add(new ExpandedEvent(
                            exception,
                            exception.Id,
                            exception.DtStart,
                            exception.DtEnd,
                            exception.RecurrenceId,
                            exception.IsSeriesMaster,
                            exception.IsException,
                            exception.SeriesMasterId));
                    }
                    else
                    {
                        // Tag normal occurrence with master linkage
                        overlaid.Add(new ExpandedEvent(
                            occ.Entity,
                            occ.OccurrenceId,
                            occ.OccurrenceStart,
                            occ.OccurrenceEnd,
                            recurrenceId,
                            false,
                            false,
                            entity.Id));
                    }
                }
                // Add exceptions that have no matching generated occurrence (e.g., out of range but still in window, or standalone)
                foreach (var kv in map)
                {
                    if (!expanded.Any(o => (o.RecurrenceId ?? o.OccurrenceStart.ToString("O")) == kv.Key))
                    {
                        var ex = kv.Value;
                        if (ex.DtEnd > rangeStart && ex.DtStart < effectiveRangeEnd)
                        {
                            overlaid.Add(new ExpandedEvent(ex, ex.Id, ex.DtStart, ex.DtEnd, ex.RecurrenceId, ex.IsSeriesMaster, ex.IsException, ex.SeriesMasterId));
                        }
                    }
                }
                results.AddRange(overlaid);
            }
            else
            {
                // Tag occurrences with master linkage
                foreach (var occ in expanded)
                {
                    var recurrenceId = occ.RecurrenceId ?? occ.OccurrenceStart.ToString("O");
                    results.Add(new ExpandedEvent(
                        occ.Entity,
                        occ.OccurrenceId,
                        occ.OccurrenceStart,
                        occ.OccurrenceEnd,
                        recurrenceId,
                        false,
                        false,
                        entity.Id));
                }
            }
        }

        _logger.LogInformation("[Recurrence V2] {EventCount} events: {Recurring} recurring, {Simple} simple, {Results} results, {Errors} errors, {Exceptions} overlays (range: {Start} to {End} effective:{EffEnd})",
            all.Count, recurring, simple, results.Count, errors, exceptionsApplied, rangeStart, rangeEnd, effectiveRangeEnd);
        return results;
    }

    private static HashSet<string> ParseExDates(string exDatesJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exDatesJson)) return new HashSet<string>();
            var arr = JsonSerializer.Deserialize<List<string>>(exDatesJson);
            return arr is null ? new HashSet<string>() : new HashSet<string>(arr, StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private static List<ExpandedEvent> ExpandRecurring(
        EventEntity entity,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        HashSet<string>? exDates = null)
    {
        var results = new List<ExpandedEvent>();
        var duration = entity.DtEnd - entity.DtStart;

        try
        {
            var calEvent = new CalendarEvent
            {
                DtStart = new CalDateTime(entity.DtStart.UtcDateTime),
                DtEnd = new CalDateTime(entity.DtEnd.UtcDateTime),
            };
#pragma warning disable CS0618
            calEvent.RecurrenceRules.Add(new RecurrencePattern(entity.RRule));
#pragma warning restore CS0618

            var options = new EvaluationOptions { MaxUnmatchedIncrementsLimit = 500 };
            var startDt = new CalDateTime(entity.DtStart.UtcDateTime);

            var occurrences = calEvent.GetOccurrences(startDt, options);

            // Only cap infinite rules (no COUNT and no UNTIL); finite rules respect their COUNT even if >500
            var rruleUpper = entity.RRule ?? string.Empty;
            var isInfinite = rruleUpper.IndexOf("COUNT", StringComparison.OrdinalIgnoreCase) < 0
                          && rruleUpper.IndexOf("UNTIL", StringComparison.OrdinalIgnoreCase) < 0;

            foreach (var occ in occurrences)
            {
                if (isInfinite && results.Count >= MaxOccurrences)
                    break;
                var start = new DateTimeOffset(occ.Period.StartTime.Value, TimeSpan.Zero);
                if (start < rangeStart)
                    continue;
                if (start >= rangeEnd)
                    break;

                var end = new DateTimeOffset(occ.Period.EndTime?.Value ?? start.Add(duration).UtcDateTime, TimeSpan.Zero);
                var recurrenceId = start.ToString("O");
                if (exDates != null && exDates.Contains(recurrenceId))
                    continue;
                // Also support ExDates stored as date-only or without millis
                if (exDates != null && exDates.Any(d => string.Equals(d, start.ToString("yyyy-MM-ddTHH:mm:ssZ"), StringComparison.Ordinal) || string.Equals(d, start.UtcDateTime.ToString("O"), StringComparison.Ordinal)))
                    continue;

                results.Add(new ExpandedEvent(
                    entity,
                    DeriveOccurrenceId(entity.Id, start),
                    start, end,
                    recurrenceId,
                    entity.IsSeriesMaster,
                    entity.IsException,
                    entity.SeriesMasterId));
            }
        }
        catch
        {
            if (entity.DtEnd > rangeStart && entity.DtStart < rangeEnd)
                results.Add(new ExpandedEvent(entity, entity.Id, entity.DtStart, entity.DtEnd, entity.RecurrenceId, entity.IsSeriesMaster, entity.IsException, entity.SeriesMasterId));
        }

        return results;
    }

    private static Guid DeriveOccurrenceId(Guid eventId, DateTimeOffset occurrenceStart)
    {
        var input = $"{eventId:D}|{occurrenceStart:yyyyMMddTHHmmssZ}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}

public class ExpandedEvent
{
    public EventEntity Entity { get; }
    public Guid OccurrenceId { get; }
    public DateTimeOffset OccurrenceStart { get; }
    public DateTimeOffset OccurrenceEnd { get; }
    public string? RecurrenceId { get; }
    public bool IsSeriesMaster { get; }
    public bool IsException { get; }
    public Guid? SeriesMasterId { get; }
    public bool IsCancelled { get; }

    public ExpandedEvent(EventEntity entity, Guid occurrenceId, DateTimeOffset occurrenceStart, DateTimeOffset occurrenceEnd, string? recurrenceId = null, bool isSeriesMaster = false, bool isException = false, Guid? seriesMasterId = null, bool? isCancelled = null)
    {
        Entity = entity;
        OccurrenceId = occurrenceId;
        OccurrenceStart = occurrenceStart;
        OccurrenceEnd = occurrenceEnd;
        RecurrenceId = recurrenceId;
        IsSeriesMaster = isSeriesMaster;
        IsException = isException;
        SeriesMasterId = seriesMasterId;
        IsCancelled = isCancelled ?? string.Equals(entity.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
    }
}
