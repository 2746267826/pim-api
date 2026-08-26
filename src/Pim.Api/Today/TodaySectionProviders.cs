using Pim.Core.Operations;
using Pim.Core.Today;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Endpoints;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Services;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Services;

namespace Pim.Api.Today;

public sealed record CalendarScheduleTodayData(
    IReadOnlyList<EventResponse> Events,
    IReadOnlyList<TaskResponse> ScheduledTasks);

public sealed record CalendarTasksTodayData(
    int IncompleteCount,
    IReadOnlyList<TaskResponse> DueTodayTasks,
    IReadOnlyList<TaskResponse> OverdueTasks,
    IReadOnlyList<TaskResponse> UnscheduledTasks);

public sealed record TodayLayerCountData(int Count, IReadOnlyList<CalendarLayerItem> Items);

public sealed record PendingConfirmationTodayData(int PendingCount, IReadOnlyList<OperationConfirmationDto> Items);

public sealed record TodayPlaceholderData(string Kind, int Count);

public sealed record EndpointStatusTodayData(
    int EndpointCount,
    int OnlineOnlyBlockedCount,
    IReadOnlyList<Pim.Core.Endpoints.EndpointStatusDto> Items);

public sealed record ReportsAvailableTodayData(
    int AvailableCount,
    IReadOnlyList<ReportArtifactDto> Reports);

public sealed record PcActivityTodayData(PcSummaryResponse Summary);

public sealed record PcQualityTodayData(PcQualityResponse Quality, int IssueCount);

public sealed record OperationsHealthTodayData(SystemStatusDetailDto Detail)
{
    public SystemStatusSummaryDto Summary => Detail.Summary;
}

public sealed record ClassificationSuggestionsTodayData(
    int PendingCount,
    IReadOnlyList<ActivityClassificationSuggestionDto> Suggestions);

public sealed class CalendarScheduleTodaySectionProvider(CalendarService calendarService) : ITodaySectionProvider
{
    public string SectionId => "calendar.schedule";

    public string Kind => "calendar.schedule";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var start = LocalMidnight(query.Date);
        var end = start.AddDays(1);
        var events = await calendarService.GetEventsAsync(start, end, ct);
        var tasks = await calendarService.GetTasksAsync(null, ct);
        var shanghai = ResolveShanghaiTimeZone();
        var scheduledTasks = tasks
            .Where(t => !string.Equals(t.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            .Where(t => t.DtStart is not null && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t.DtStart.Value, shanghai).Date) == query.Date)
            .OrderBy(t => t.DtStart)
            .ToList();
        var status = events.Count == 0 && scheduledTasks.Count == 0
            ? TodaySectionStatuses.Empty
            : TodaySectionStatuses.Normal;

        return Section(status, new CalendarScheduleTodayData(events, scheduledTasks), Details("/calendar"));
    }

    private TodaySectionDto Section(string status, object data, IReadOnlyList<TodayLinkDto> links)
        => TodaySectionProviderResult.Build(SectionId, Kind, status, data, links);

    private static IReadOnlyList<TodayLinkDto> Details(string href) => TodaySectionProviderResult.Details(href);

    private static DateTimeOffset LocalMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static TimeZoneInfo ResolveShanghaiTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}

public sealed class CalendarTasksTodaySectionProvider(CalendarService calendarService) : ITodaySectionProvider
{
    public string SectionId => "calendar.tasks";

    public string Kind => "calendar.tasks";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var tasks = await calendarService.GetTasksAsync(null, ct);
        var incomplete = tasks
            .Where(t => !string.Equals(t.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var dueTodayTasks = incomplete
            .Where(t => t.Due is not null && DateOnly.FromDateTime(t.Due.Value.Date) == query.Date)
            .OrderBy(t => t.Due)
            .ToList();
        var overdueTasks = incomplete
            .Where(t => t.Due is not null && DateOnly.FromDateTime(t.Due.Value.Date) < query.Date)
            .OrderBy(t => t.Due)
            .ToList();
        var unscheduledTasks = incomplete
            .Where(t => t.DtStart is null)
            .OrderBy(t => t.SortOrder)
            .ToList();
        var status = overdueTasks.Count > 0 || dueTodayTasks.Count > 0
            ? TodaySectionStatuses.Warning
            : incomplete.Count == 0
                ? TodaySectionStatuses.Empty
                : TodaySectionStatuses.Normal;

        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            status,
            new CalendarTasksTodayData(incomplete.Count, dueTodayTasks, overdueTasks, unscheduledTasks),
            TodaySectionProviderResult.Details("/tasks", "/calendar"));
    }
}

public sealed class CalendarHabitsTodaySectionProvider(PlanningModelService planningService) : ITodaySectionProvider
{
    public string SectionId => "calendar.habits";

    public string Kind => "calendar.habits";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var layers = await planningService.GetCalendarLayersAsync(LayerQuery(query, "habits"), ct);
        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            layers.Items.Count == 0 ? TodaySectionStatuses.Empty : TodaySectionStatuses.Normal,
            new TodayLayerCountData(layers.Items.Count, layers.Items),
            TodaySectionProviderResult.Details("/habits", "/calendar"));
    }

    internal static CalendarLayerQuery LayerQuery(TodayQuery query, string layer)
    {
        var start = query.Date.ToDateTime(TimeOnly.MinValue);
        var offset = TimeZoneInfo.Local.GetUtcOffset(start);
        var from = new DateTimeOffset(start, offset);
        return new CalendarLayerQuery(from, from.AddDays(1), [layer]);
    }
}

public sealed class CalendarAvailabilityTodaySectionProvider(PlanningModelService planningService) : ITodaySectionProvider
{
    public string SectionId => "calendar.availability";

    public string Kind => "calendar.availability";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var layers = await planningService.GetCalendarLayersAsync(
            CalendarHabitsTodaySectionProvider.LayerQuery(query, "availability"),
            ct);
        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            layers.Items.Count == 0 ? TodaySectionStatuses.Empty : TodaySectionStatuses.Normal,
            new TodayLayerCountData(layers.Items.Count, layers.Items),
            TodaySectionProviderResult.Details("/calendar"));
    }
}

public sealed class CalendarAiPlaceholdersTodaySectionProvider(PlanningModelService planningService) : ITodaySectionProvider
{
    public string SectionId => "calendar.ai_placeholders";

    public string Kind => "calendar.ai_placeholders";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var layers = await planningService.GetCalendarLayersAsync(
            CalendarHabitsTodaySectionProvider.LayerQuery(query, "ai-placeholders"),
            ct);
        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            layers.Items.Count == 0 ? TodaySectionStatuses.Empty : TodaySectionStatuses.Warning,
            new TodayLayerCountData(layers.Items.Count, layers.Items),
            TodaySectionProviderResult.Details("/calendar", "/confirmations"));
    }
}

public sealed class OperationsConfirmationsTodaySectionProvider(
    IOperationConfirmationService confirmations,
    ICurrentUserService currentUser) : ITodaySectionProvider
{
    public string SectionId => "operations.confirmations";

    public string Kind => "operations.confirmations";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var pending = await confirmations.ListPendingForUserAsync(currentUser.UserId, ct);
        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            pending.Count == 0 ? TodaySectionStatuses.Empty : TodaySectionStatuses.Warning,
            new PendingConfirmationTodayData(pending.Count, pending),
            TodaySectionProviderResult.Details("/confirmations"));
    }
}

public sealed class OutlookSyncTodaySectionProvider : ITodaySectionProvider
{
    public string SectionId => "sync.outlook";

    public string Kind => "sync.outlook";

    public Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
        => Task.FromResult(TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            TodaySectionStatuses.Empty,
            new TodayPlaceholderData(Kind, 0),
            TodaySectionProviderResult.Details("/sync")));
}

public sealed class RemindersQueueTodaySectionProvider(ReminderService reminderService) : ITodaySectionProvider
{
    public string SectionId => "reminders.queue";

    public string Kind => "reminders.queue";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var reminders = await reminderService.ListAsync(ct);
        var open = reminders
            .Where(r => r.Status is "Open" or "Snoozed")
            .Where(r => r.ScheduledAt <= DateTimeOffset.UtcNow.AddDays(1))
            .ToList();
        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            open.Count == 0 ? TodaySectionStatuses.Empty : TodaySectionStatuses.Warning,
            new TodayPlaceholderData(Kind, open.Count),
            TodaySectionProviderResult.Details("/reminders"));
    }
}

public sealed class ReportsAvailableTodaySectionProvider(ReportService reportService) : ITodaySectionProvider
{
    public string SectionId => "reports.available";

    public string Kind => "reports.available";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var reports = await reportService.ListAsync(ct);
        var shanghai2 = ResolveShanghaiTimeZone();
        var available = reports
            .Where(r => string.Equals(r.Status, "Active", StringComparison.OrdinalIgnoreCase))
            .Where(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.GeneratedAt, shanghai2).Date) == query.Date)
            .Take(5)
            .ToList();

        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            available.Count == 0 ? TodaySectionStatuses.Empty : TodaySectionStatuses.Normal,
            new ReportsAvailableTodayData(available.Count, available),
            TodaySectionProviderResult.Details("/reports"));
    }

    private static TimeZoneInfo ResolveShanghaiTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}

public sealed class EndpointsStatusTodaySectionProvider(EndpointStatusService endpointStatus) : ITodaySectionProvider
{
    public string SectionId => "endpoints.status";

    public string Kind => "endpoints.status";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var endpoints = await endpointStatus.ListAsync(ct);
        var blocked = endpoints.Sum(endpoint => endpoint.OnlineOnlyBlockedCount);
        var hasWarning = blocked > 0
            || endpoints.Any(endpoint =>
                string.Equals(endpoint.UploadStatus, "Warning", StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpoint.UploadStatus, "Critical", StringComparison.OrdinalIgnoreCase));

        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            endpoints.Count == 0
                ? TodaySectionStatuses.Empty
                : hasWarning
                    ? TodaySectionStatuses.Warning
                    : TodaySectionStatuses.Normal,
            new EndpointStatusTodayData(endpoints.Count, blocked, endpoints),
            TodaySectionProviderResult.Details("/endpoint-shell"));
    }
}

public sealed class PcActivityTodaySectionProvider(PcTrackerService pcTrackerService) : ITodaySectionProvider
{
    public string SectionId => "pc.activity";

    public string Kind => "pc.activity";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var summary = await pcTrackerService.GetSummaryAsync(query.PcBusinessDate.ToDateTime(TimeOnly.MinValue), ct);
        var hasData = summary.Heatmap.Any(bucket => bucket.TotalEvents > 0 || bucket.ActiveMinutes > 0)
            || summary.AppRanking.Count > 0
            || summary.Timeline.Count > 0
            || summary.Sessions.Count > 0
            || summary.Keystats is not null;
        var status = hasData ? TodaySectionStatuses.Normal : TodaySectionStatuses.Empty;

        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            status,
            new PcActivityTodayData(summary),
            TodaySectionProviderResult.Details("/pc-tracker"));
    }
}

public sealed class PcQualityTodaySectionProvider(PcTrackerQualityService qualityService) : ITodaySectionProvider
{
    public string SectionId => "pc.quality";

    public string Kind => "pc.quality";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var quality = await qualityService.GetQualityAsync(
            query.PcBusinessDate.ToDateTime(TimeOnly.MinValue),
            null,
            null,
            ct);

        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            TodaySectionProviderResult.MapStatus(quality.OverallStatus),
            new PcQualityTodayData(quality, quality.Issues.Count),
            TodaySectionProviderResult.Details("/pc-tracker"));
    }
}

public sealed class OperationsHealthTodaySectionProvider(ISystemStatusService statusService) : ITodaySectionProvider
{
    public string SectionId => "operations.health";

    public string Kind => "operations.health";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var detail = await statusService.GetDetailAsync(ct);

        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            TodaySectionProviderResult.MapStatus(detail.Summary.Status),
            new OperationsHealthTodayData(detail),
            TodaySectionProviderResult.Details("/status"));
    }
}

public sealed class ClassificationSuggestionsTodaySectionProvider(ActivitySuggestionService suggestionService)
    : ITodaySectionProvider
{
    public string SectionId => "pc.classification_suggestions";

    public string Kind => "pc.classification_suggestions";

    public async Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
    {
        var suggestions = await suggestionService.GetSuggestionsAsync(ct);
        var preview = suggestions.Take(5).ToList();
        var status = suggestions.Count > 0 ? TodaySectionStatuses.Warning : TodaySectionStatuses.Empty;

        return TodaySectionProviderResult.Build(
            SectionId,
            Kind,
            status,
            new ClassificationSuggestionsTodayData(suggestions.Count, preview),
            TodaySectionProviderResult.Details("/pc-tracker"));
    }
}

internal static class TodaySectionProviderResult
{
    public static TodaySectionDto Build(
        string sectionId,
        string kind,
        string status,
        object data,
        IReadOnlyList<TodayLinkDto> links)
        => new(sectionId, kind, status, DateTimeOffset.UtcNow, data, links, null);

    public static IReadOnlyList<TodayLinkDto> Details(params string[] hrefs)
        => hrefs.Select(href => new TodayLinkDto(TodayLinkRels.Details, href)).ToList();

    public static string MapStatus(PimHealthStatus status)
        => status switch
        {
            PimHealthStatus.Healthy => TodaySectionStatuses.Normal,
            PimHealthStatus.Warning => TodaySectionStatuses.Warning,
            PimHealthStatus.Critical => TodaySectionStatuses.Critical,
            _ => TodaySectionStatuses.Unavailable
        };

    private static TimeZoneInfo ResolveShanghaiTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}
