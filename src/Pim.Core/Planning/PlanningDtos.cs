namespace Pim.Core.Planning;

public sealed record DomainProjectDto(
    Guid Id,
    string Name,
    string? Description,
    string Status);

public sealed record TaskBookDto(
    Guid Id,
    Guid? DomainProjectId,
    string Name,
    string Kind,
    string Status,
    int TaskCount = 0);

public sealed record TaskChecklistItemDto(
    Guid Id,
    Guid TaskId,
    string Title,
    bool IsDone,
    int SortOrder);

public sealed record HabitRoutineDto(
    Guid Id,
    string Title,
    HabitCadence Cadence,
    string Source,
    string Status);

public sealed record HabitOccurrenceDto(
    Guid Id,
    Guid HabitRoutineId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status);

public sealed record AvailabilityWindowDto(
    Guid Id,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Kind,
    string Source);

public sealed record AiPlanningPlaceholderDto(
    Guid Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    Guid? ConfirmationId);
