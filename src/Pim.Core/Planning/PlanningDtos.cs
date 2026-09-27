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

/// <summary>
/// 习惯规则视图。
/// </summary>
/// <param name="Id">习惯 Id。</param>
/// <param name="Title">标题。</param>
/// <param name="Cadence">频率。</param>
/// <param name="Source">来源。</param>
/// <param name="Status">状态（active / Archived 等）。</param>
/// <param name="Description">
/// #351：描述。编辑表单需要回显已保存的描述，否则用户改了别的字段会把描述静默清空，
/// 也无从知道当前描述是什么。放在末尾并给默认值，保持既有构造点与旧消费方兼容。
/// </param>
public sealed record HabitRoutineDto(
    Guid Id,
    string Title,
    HabitCadence Cadence,
    string Source,
    string Status,
    string? Description = null);

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
