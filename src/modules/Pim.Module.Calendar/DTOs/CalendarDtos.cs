using System.ComponentModel.DataAnnotations;

namespace Pim.Module.Calendar.DTOs;

public record CreateCalendarRequest(
    [Required][MaxLength(100)] string Name,
    [MaxLength(7)] string? Color
);

public record CalendarResponse(
    Guid Id, string Name, string Color, bool IsDefault, int EventCount
);

public record CreateEventRequest(
    [Required] Guid CalendarId,
    [Required][MaxLength(255)] string Title,
    string? Description,
    [MaxLength(500)] string? Location,
    [Required] DateTimeOffset DtStart,
    [Required] DateTimeOffset DtEnd,
    string? RRule
);

public record EventResponse(
    Guid Id, Guid CalendarId, string Uid, string Title,
    string? Description, string? Location,
    DateTimeOffset DtStart, DateTimeOffset DtEnd,
    string? RRule, string Status, string Source
);

public record CreateTaskRequest(
    Guid? CalendarId,
    [Required][MaxLength(255)] string Title,
    string? Description,
    int Priority,
    string? EstimatedDuration,
    string? MinimumSegment,
    DateTimeOffset? Due,
    DateTimeOffset? DtStart
);

public record TaskResponse(
    Guid Id, Guid? CalendarId, string Uid, string Title,
    string? Description, int Priority,
    string? EstimatedDuration, string? MinimumSegment,
    DateTimeOffset? DtStart, DateTimeOffset? Due,
    string Status, bool IsInbox, int SortOrder,
    List<TaskResponse> SubTasks
);

public record MoveTaskRequest(
    DateTimeOffset? ScheduledStart,
    TimeSpan? Duration,
    int? NewSortOrder
);

public record ScheduleRequest(
    List<Guid> TaskIds
);

public record SchedulePlanResponse(
    Guid PlanId,
    string AlgorithmName,
    List<ScheduledTaskSlot> Slots
);

public record ScheduledTaskSlot(
    Guid TaskId,
    string TaskTitle,
    DateTimeOffset Start,
    DateTimeOffset End
);

public record ImportResult(int Imported, int Skipped);
