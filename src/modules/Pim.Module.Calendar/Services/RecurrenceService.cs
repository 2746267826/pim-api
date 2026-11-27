using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
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
    private const int MaxOccurrencesPerSeries = 10_000;
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
        var capUnbounded = rangeEnd == DateTimeOffset.MaxValue || rangeStart == DateTimeOffset.MinValue;
        var displayDateStart = EventTimeZoneResolver.DisplayDate(rangeStart);
        var displayDateEndExclusive = EventTimeZoneResolver.DisplayDateEndExclusive(effectiveRangeEnd);
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
                var simpleEvent = BuildSimpleExpandedEvent(entity);
                if (OccursInWindow(simpleEvent, rangeStart, effectiveRangeEnd, displayDateStart, displayDateEndExclusive))
                    results.Add(simpleEvent);
                continue;
            }

            // Recurring master (or legacy RRule without IsSeriesMaster flag)
            recurring++;
            var exDates = ParseExDates(entity.ExDatesJson);
            var expanded = ExpandRecurring(
                entity,
                rangeStart,
                effectiveRangeEnd,
                displayDateStart,
                displayDateEndExclusive,
                exDates,
                capUnbounded);
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
                        // Use exception entity's own time and status; keep original recurrenceId.
                        // A moved exception outside the query window must not leak into the page.
                        var exceptionEvent = BuildSimpleExpandedEvent(exception);
                        if (OccursInWindow(
                                exceptionEvent,
                                rangeStart,
                                effectiveRangeEnd,
                                displayDateStart,
                                displayDateEndExclusive))
                        {
                            overlaid.Add(exceptionEvent);
                        }
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
                            entity.Id,
                            null,
                            occ.AllDayStartDate,
                            occ.AllDayEndDateExclusive));
                    }
                }
                // Add exceptions that have no matching generated occurrence (e.g., out of range but still in window, or standalone)
                foreach (var kv in map)
                {
                    if (!expanded.Any(o => (o.RecurrenceId ?? o.OccurrenceStart.ToString("O")) == kv.Key))
                    {
                        var ex = kv.Value;
                        var exceptionEvent = BuildSimpleExpandedEvent(ex);
                        if (OccursInWindow(
                                exceptionEvent,
                                rangeStart,
                                effectiveRangeEnd,
                                displayDateStart,
                                displayDateEndExclusive))
                            overlaid.Add(exceptionEvent);
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
                        entity.Id,
                        null,
                        occ.AllDayStartDate,
                        occ.AllDayEndDateExclusive));
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
        DateOnly displayDateStart,
        DateOnly displayDateEndExclusive,
        HashSet<string>? exDates = null,
        bool capUnbounded = true)
    {
        var results = new List<ExpandedEvent>();
        var timeZone = EventTimeZoneResolver.Resolve(entity.TimeZoneId, entity.SourceTimeZoneId, entity.OriginalStartTimeZone);
        var isAllDay = entity.IsAllDay && entity.AllDayStartDate.HasValue && entity.AllDayEndDateExclusive.HasValue;
        var duration = isAllDay
            ? entity.AllDayEndDateExclusive!.Value.DayNumber - entity.AllDayStartDate!.Value.DayNumber
            : Math.Max(0, (entity.DtEnd - entity.DtStart).TotalMinutes);

        try
        {
            CalendarEvent calEvent;
            CalDateTime evaluationStart;
            if (isAllDay)
            {
                var allDayStart = entity.AllDayStartDate!.Value.ToDateTime(TimeOnly.MinValue);
                var allDayEnd = entity.AllDayEndDateExclusive!.Value.ToDateTime(TimeOnly.MinValue);
                var allDayZone = timeZone.Zone ?? EventTimeZoneResolver.DisplayZone;
                calEvent = new CalendarEvent
                {
                    DtStart = new CalDateTime(allDayStart),
                    DtEnd = new CalDateTime(allDayEnd),
                };
                var rangeStartDate = DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTime(rangeStart, allDayZone).DateTime);
                evaluationStart = new CalDateTime(
                    rangeStartDate.AddDays(-Math.Max(1, (int)duration)).ToDateTime(TimeOnly.MinValue));
            }
            else if (timeZone.IsResolved)
            {
                var localStart = TimeZoneInfo.ConvertTime(entity.DtStart, timeZone.Zone!).DateTime;
                var localEnd = TimeZoneInfo.ConvertTime(entity.DtEnd, timeZone.Zone!).DateTime;
                calEvent = new CalendarEvent
                {
                    DtStart = new CalDateTime(localStart, timeZone.Id),
                    DtEnd = new CalDateTime(localEnd, timeZone.Id),
                };
                var lookback = TimeSpan.FromMinutes(duration).Add(TimeSpan.FromDays(1));
                var evaluation = rangeStart - lookback;
                evaluationStart = new CalDateTime(
                    TimeZoneInfo.ConvertTime(evaluation, timeZone.Zone!).DateTime,
                    timeZone.Id);
            }
            else
            {
                calEvent = new CalendarEvent
                {
                    DtStart = new CalDateTime(entity.DtStart.UtcDateTime),
                    DtEnd = new CalDateTime(entity.DtEnd.UtcDateTime),
                };
                var lookback = TimeSpan.FromMinutes(duration).Add(TimeSpan.FromDays(1));
                evaluationStart = new CalDateTime((rangeStart - lookback).UtcDateTime);
            }
#pragma warning disable CS0618
            calEvent.RecurrenceRules.Add(new RecurrencePattern(entity.RRule));
#pragma warning restore CS0618

            var options = new EvaluationOptions { MaxUnmatchedIncrementsLimit = 500 };
            var occurrences = calEvent.GetOccurrences(evaluationStart, options);

            // Only cap infinite rules (no COUNT and no UNTIL); finite rules respect their COUNT even if >500
            var rruleUpper = entity.RRule ?? string.Empty;
            var isInfinite = rruleUpper.IndexOf("COUNT", StringComparison.OrdinalIgnoreCase) < 0
                          && rruleUpper.IndexOf("UNTIL", StringComparison.OrdinalIgnoreCase) < 0;

            foreach (var occ in occurrences)
            {
                if (capUnbounded && isInfinite && results.Count >= MaxOccurrences)
                    break;

                DateOnly? occurrenceStartDate = null;
                DateOnly? occurrenceEndDate = null;
                DateTimeOffset start;
                DateTimeOffset end;
                if (isAllDay)
                {
                    occurrenceStartDate = DateOnly.FromDateTime(occ.Period.StartTime.Value);
                    occurrenceEndDate = occurrenceStartDate.Value.AddDays(Math.Max(1, (int)duration));
                    var allDayZone = timeZone.Zone ?? EventTimeZoneResolver.DisplayZone;
                    start = EventTimeZoneResolver.StartOfLocalDate(
                        occurrenceStartDate.Value,
                        allDayZone,
                        allDayZone.GetUtcOffset(occurrenceStartDate.Value.ToDateTime(TimeOnly.MinValue)));
                    end = EventTimeZoneResolver.StartOfLocalDate(
                        occurrenceEndDate.Value,
                        allDayZone,
                        allDayZone.GetUtcOffset(occurrenceEndDate.Value.ToDateTime(TimeOnly.MinValue)));
                }
                else
                {
                    var startUtc = occ.Period.StartTime.AsUtc;
                    start = new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), TimeSpan.Zero);
                    var endUtc = occ.Period.EndTime?.AsUtc;
                    end = endUtc.HasValue
                        ? new DateTimeOffset(DateTime.SpecifyKind(endUtc.Value, DateTimeKind.Utc), TimeSpan.Zero)
                        : start.AddMinutes(duration);
                }

                var inWindow = start < rangeEnd && end > rangeStart;
                if (!inWindow)
                {
                    if (start >= rangeEnd)
                        break;
                    continue;
                }

                var recurrenceId = start.ToString("O");
                if (exDates != null && exDates.Contains(recurrenceId))
                    continue;
                // Also support ExDates stored as date-only or without millis
                if (exDates != null && exDates.Any(d =>
                        string.Equals(d, start.ToString("yyyy-MM-ddTHH:mm:ssZ"), StringComparison.Ordinal)
                        || string.Equals(d, start.UtcDateTime.ToString("O"), StringComparison.Ordinal)
                        || (occurrenceStartDate.HasValue
                            && (string.Equals(d, occurrenceStartDate.Value.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
                                || string.Equals(d, occurrenceStartDate.Value.ToString("yyyyMMdd"), StringComparison.Ordinal)))))
                    continue;

                if (results.Count >= MaxOccurrencesPerSeries)
                {
                    throw new DomainException(
                        02009,
                        $"重复规则展开超出安全预算（单系列最多 {MaxOccurrencesPerSeries} 次），请缩小查询窗口或降低重复频率。");
                }

                results.Add(new ExpandedEvent(
                    entity,
                    DeriveOccurrenceId(entity.Id, start),
                    start, end,
                    recurrenceId,
                    entity.IsSeriesMaster,
                    entity.IsException,
                    entity.SeriesMasterId,
                    null,
                    occurrenceStartDate,
                    occurrenceEndDate));
            }
        }
        catch (DomainException)
        {
            throw;
        }
        catch
        {
            var fallback = BuildSimpleExpandedEvent(entity);
            if (OccursInWindow(fallback, rangeStart, rangeEnd, displayDateStart, displayDateEndExclusive))
                results.Add(fallback);
        }

        return results;
    }

    private static ExpandedEvent BuildSimpleExpandedEvent(EventEntity entity)
    {
        if (!entity.IsAllDay
            || !entity.AllDayStartDate.HasValue
            || !entity.AllDayEndDateExclusive.HasValue)
        {
            return new ExpandedEvent(
                entity,
                entity.Id,
                entity.DtStart,
                entity.DtEnd,
                entity.RecurrenceId,
                entity.IsSeriesMaster,
                entity.IsException,
                entity.SeriesMasterId);
        }

        var startDate = entity.AllDayStartDate.Value;
        var endDate = entity.AllDayEndDateExclusive.Value;
        var zone = EventTimeZoneResolver.Resolve(
            entity.TimeZoneId,
            entity.SourceTimeZoneId,
            entity.OriginalStartTimeZone).Zone ?? EventTimeZoneResolver.DisplayZone;
        return new ExpandedEvent(
            entity,
            entity.Id,
            EventTimeZoneResolver.StartOfLocalDate(
                startDate, zone, zone.GetUtcOffset(startDate.ToDateTime(TimeOnly.MinValue))),
            EventTimeZoneResolver.StartOfLocalDate(
                endDate, zone, zone.GetUtcOffset(endDate.ToDateTime(TimeOnly.MinValue))),
            entity.RecurrenceId,
            entity.IsSeriesMaster,
            entity.IsException,
            entity.SeriesMasterId,
            null,
            startDate,
            endDate);
    }

    private static bool OccursInWindow(
        ExpandedEvent expanded,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateOnly displayDateStart,
        DateOnly displayDateEndExclusive)
    {
        return expanded.OccurrenceStart < rangeEnd && expanded.OccurrenceEnd > rangeStart;
    }

    internal static Guid DeriveOccurrenceId(Guid eventId, DateTimeOffset occurrenceStart)
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
    public DateOnly? AllDayStartDate { get; }
    public DateOnly? AllDayEndDateExclusive { get; }

    public ExpandedEvent(
        EventEntity entity,
        Guid occurrenceId,
        DateTimeOffset occurrenceStart,
        DateTimeOffset occurrenceEnd,
        string? recurrenceId = null,
        bool isSeriesMaster = false,
        bool isException = false,
        Guid? seriesMasterId = null,
        bool? isCancelled = null,
        DateOnly? allDayStartDate = null,
        DateOnly? allDayEndDateExclusive = null)
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
        AllDayStartDate = allDayStartDate;
        AllDayEndDateExclusive = allDayEndDateExclusive;
    }
}
