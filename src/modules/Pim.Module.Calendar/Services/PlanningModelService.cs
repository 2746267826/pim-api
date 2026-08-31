using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Core.Planning;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class PlanningModelService
{
    private static readonly HashSet<string> DefaultLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "events",
        "task-segments",
        "habits",
        "availability",
        "ai-placeholders"
    };

    private static readonly HashSet<string> OutlookSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook",
        "outlook-graph",
        "outlook-ics"
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOperationConfirmationService? _confirmationService;
    private readonly RecurrenceService _recurrence;

    public PlanningModelService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IOperationConfirmationService? confirmationService = null,
        RecurrenceService? recurrence = null)
    {
        _db = db;
        _currentUser = currentUser;
        _confirmationService = confirmationService;
        _recurrence = recurrence ?? new RecurrenceService(NullLogger<RecurrenceService>.Instance);
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<CalendarLayerResponse> GetCalendarLayersAsync(
        CalendarLayerQuery query,
        CancellationToken ct = default)
    {
        var userId = UserId;
        if (query.End <= query.Start)
            throw new DomainException(02027, "Layer end must be after start");

        var requestedLayers = NormalizeLayers(query.Layers);
        var items = new List<CalendarLayerItem>();

        if (requestedLayers.Contains("events"))
        {
            var minValidDate = DateTimeOffset.MinValue.AddYears(100);
            var eventEntities = await _db.Set<EventEntity>()
                .AsNoTracking()
                .Include(e => e.Calendar)
                .Where(e => e.Calendar.UserId == userId
                    && e.DtStart > minValidDate
                    && e.DtEnd > minValidDate
                    && ((e.DtStart < query.End && e.DtEnd > query.Start)
                        || !string.IsNullOrEmpty(e.RRule)
                        || e.IsException))
                .ToListAsync(ct);

            var expanded = _recurrence.ExpandEventsV2(eventEntities, query.Start, query.End);

            items.AddRange(expanded
                .Where(ex => !query.OutlookOnly || IsOutlookSource(ex.Entity.Source))
                .Select(ex => new CalendarLayerItem(
                    $"event:{ex.OccurrenceId}",
                    "events",
                    "event",
                    ex.OccurrenceId,
                    ex.Entity.Title,
                    ex.OccurrenceStart,
                    ex.OccurrenceEnd,
                    ex.Entity.Source,
                    ex.Entity.Status,
                    ex.Entity.Calendar.Color,
                    false)));
        }

        if (requestedLayers.Contains("task-segments"))
        {
            var segments = await _db.Set<TaskExecutionSegmentEntity>()
                .AsNoTracking()
                .Include(s => s.Task)
                .Where(s => s.UserId == userId
                    && s.StartsAt < query.End
                    && s.EndsAt > query.Start)
                .ToListAsync(ct);

            items.AddRange(segments
                .Where(s => !query.OutlookOnly || IsOutlookSource(s.Source))
                .Select(s => new CalendarLayerItem(
                    $"task-segment:{s.Id}",
                    "task-segments",
                    "task-segment",
                    s.Id,
                    s.Task.Title,
                    s.StartsAt,
                    s.EndsAt,
                    s.Source,
                    s.Status,
                    "#22C55E",
                    s.ConfirmationId.HasValue)));
        }

        if (requestedLayers.Contains("habits"))
        {
            var occurrences = await _db.Set<HabitOccurrenceEntity>()
                .AsNoTracking()
                .Include(o => o.HabitRoutine)
                .Where(o => o.UserId == userId
                    && o.StartsAt < query.End
                    && o.EndsAt > query.Start)
                .ToListAsync(ct);

            items.AddRange(occurrences
                .Where(o => !query.OutlookOnly || IsOutlookSource(o.Source))
                .Select(o => new CalendarLayerItem(
                    $"habit:{o.Id}",
                    "habits",
                    "habit-occurrence",
                    o.Id,
                    o.HabitRoutine.Title,
                    o.StartsAt,
                    o.EndsAt,
                    o.Source,
                    o.Status,
                    "#A855F7",
                    o.ConfirmationId.HasValue)));
        }

        if (requestedLayers.Contains("availability"))
        {
            var windows = await _db.Set<AvailabilityWindowEntity>()
                .AsNoTracking()
                .Where(a => a.UserId == userId
                    && a.StartsAt < query.End
                    && a.EndsAt > query.Start)
                .ToListAsync(ct);

            items.AddRange(windows
                .Where(a => !query.OutlookOnly || IsOutlookSource(a.Source))
                .Select(a => new CalendarLayerItem(
                    $"availability:{a.Id}",
                    "availability",
                    "availability-window",
                    a.Id,
                    a.Title,
                    a.StartsAt,
                    a.EndsAt,
                    a.Source,
                    a.Kind,
                    "#0EA5E9",
                    false)));
        }

        if (requestedLayers.Contains("ai-placeholders"))
        {
            var placeholders = await _db.Set<AiPlanningPlaceholderEntity>()
                .AsNoTracking()
                .Where(p => p.UserId == userId
                    && p.StartsAt < query.End
                    && p.EndsAt > query.Start)
                .ToListAsync(ct);

            items.AddRange(placeholders
                .Where(p => !query.OutlookOnly || IsOutlookSource(p.Source))
                .Select(p => new CalendarLayerItem(
                    $"ai-placeholder:{p.Id}",
                    "ai-placeholders",
                    "ai-planning-placeholder",
                    p.Id,
                    p.Title,
                    p.StartsAt,
                    p.EndsAt,
                    p.Source,
                    p.Status,
                    "#F97316",
                    true)));
        }

        return new CalendarLayerResponse(
            query.Start,
            query.End,
            items
                .OrderBy(i => i.StartsAt)
                .ThenBy(i => i.Layer)
                .ThenBy(i => i.Title)
                .ThenBy(i => i.ObjectId)
                .ToList());
    }

    public async Task<IReadOnlyList<DomainProjectDto>> ListProjectsAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        return await _db.Set<DomainProjectEntity>()
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .Select(p => new DomainProjectDto(p.Id, p.Name, p.Description, p.Status))
            .ToListAsync(ct);
    }

    public async Task<DomainProjectDto> CreateProjectAsync(
        CreateDomainProjectRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        ValidateRequired(request.Name, "Project name", 255);
        var now = DateTimeOffset.UtcNow;
        var entity = new DomainProjectEntity
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description,
            Status = NormalizeShort(request.Status, "Active"),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<DomainProjectEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return new DomainProjectDto(entity.Id, entity.Name, entity.Description, entity.Status);
    }

    public async Task<IReadOnlyList<TaskBookDto>> ListTaskBooksAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        return await _db.Set<TaskBookEntity>()
            .AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.Name)
            .Select(b => new TaskBookDto(b.Id, b.DomainProjectId, b.Name, b.Kind, b.Status))
            .ToListAsync(ct);
    }

    public async Task<TaskBookDto> CreateTaskBookAsync(
        CreateTaskBookRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        ValidateRequired(request.Name, "Task book name", 255);
        if (request.DomainProjectId.HasValue)
        {
            var projectExists = await _db.Set<DomainProjectEntity>()
                .AnyAsync(p => p.Id == request.DomainProjectId.Value && p.UserId == userId, ct);
            if (!projectExists)
                throw new DomainException(02028, "Project does not exist");
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new TaskBookEntity
        {
            UserId = userId,
            DomainProjectId = request.DomainProjectId,
            Name = request.Name.Trim(),
            Kind = NormalizeShort(request.Kind, "task"),
            Status = NormalizeShort(request.Status, "Active"),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<TaskBookEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return new TaskBookDto(entity.Id, entity.DomainProjectId, entity.Name, entity.Kind, entity.Status);
    }

    public async Task<TaskChecklistItemDto> AddChecklistItemAsync(
        Guid taskId,
        AddTaskChecklistItemRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        ValidateRequired(request.Title, "Checklist title", 255);
        var task = await GetTaskAsync(taskId, userId, ct);
        var sortOrder = request.SortOrder
            ?? await _db.Set<TaskChecklistItemEntity>()
                .Where(i => i.TaskId == task.Id && i.UserId == userId)
                .CountAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var entity = new TaskChecklistItemEntity
        {
            TaskId = task.Id,
            UserId = userId,
            Title = request.Title.Trim(),
            SortOrder = sortOrder,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<TaskChecklistItemEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return new TaskChecklistItemDto(entity.Id, entity.TaskId, entity.Title, entity.IsDone, entity.SortOrder);
    }

    public async Task<IReadOnlyList<HabitRoutineDto>> ListHabitsAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var habits = await _db.Set<HabitRoutineEntity>()
            .AsNoTracking()
            .Where(h => h.UserId == userId)
            .OrderBy(h => h.Title)
            .ToListAsync(ct);

        return habits.Select(h => new HabitRoutineDto(
            h.Id,
            h.Title,
            ParseCadence(h.Cadence),
            h.Source,
            h.Status)).ToList();
    }

    public async Task<HabitRoutineDto> CreateHabitAsync(
        CreateHabitRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        ValidateRequired(request.Title, "Habit title", 255);
        var now = DateTimeOffset.UtcNow;
        var entity = new HabitRoutineEntity
        {
            UserId = userId,
            Title = request.Title.Trim(),
            Description = request.Description,
            Cadence = NormalizeShort(request.Cadence, "Daily"),
            Source = NormalizeShort(request.Source, "manual"),
            Status = NormalizeShort(request.Status, "Active"),
            RuleJson = string.IsNullOrWhiteSpace(request.RuleJson) ? "{}" : request.RuleJson!,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<HabitRoutineEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return new HabitRoutineDto(entity.Id, entity.Title, ParseCadence(entity.Cadence), entity.Source, entity.Status);
    }

    public async Task<HabitOccurrenceDto> CreateHabitOccurrenceAsync(
        Guid habitId,
        CreateHabitOccurrenceRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        if (endsAt <= startsAt)
            throw new DomainException(02029, "Habit occurrence end must be after start");

        var habitExists = await _db.Set<HabitRoutineEntity>()
            .AnyAsync(h => h.Id == habitId && h.UserId == userId, ct);
        if (!habitExists)
            throw new DomainException(02030, "Habit does not exist");

        var now = DateTimeOffset.UtcNow;
        var entity = new HabitOccurrenceEntity
        {
            HabitRoutineId = habitId,
            UserId = userId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Status = NormalizeShort(request.Status, "Planned"),
            Source = NormalizeShort(request.Source, "manual"),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<HabitOccurrenceEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return new HabitOccurrenceDto(entity.Id, entity.HabitRoutineId, entity.StartsAt, entity.EndsAt, entity.Status);
    }

    public async Task<IReadOnlyList<AvailabilityWindowDto>> ListAvailabilityAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        return await _db.Set<AvailabilityWindowEntity>()
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.StartsAt)
            .Select(a => new AvailabilityWindowDto(a.Id, a.StartsAt, a.EndsAt, a.Kind, a.Source))
            .ToListAsync(ct);
    }

    public async Task<AvailabilityWindowDto> CreateAvailabilityWindowAsync(
        CreateAvailabilityWindowRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        ValidateRequired(request.Title, "Availability title", 255);
        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        if (endsAt <= startsAt)
            throw new DomainException(02031, "Availability end must be after start");

        var now = DateTimeOffset.UtcNow;
        var entity = new AvailabilityWindowEntity
        {
            UserId = userId,
            Title = request.Title.Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            Kind = NormalizeShort(request.Kind, "available"),
            Source = NormalizeShort(request.Source, "manual"),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<AvailabilityWindowEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return new AvailabilityWindowDto(entity.Id, entity.StartsAt, entity.EndsAt, entity.Kind, entity.Source);
    }

    public async Task<AiPlanningPlaceholderDto> CreateAiPlaceholderAsync(
        CreateAiPlanningPlaceholderRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        ValidateRequired(request.Title, "AI placeholder title", 255);
        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        if (endsAt <= startsAt)
            throw new DomainException(02032, "AI placeholder end must be after start");

        var now = DateTimeOffset.UtcNow;
        var entity = new AiPlanningPlaceholderEntity
        {
            UserId = userId,
            Title = request.Title.Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            Reason = request.Reason,
            Source = NormalizeShort(request.Source, "ai"),
            Status = "Suggested",
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<AiPlanningPlaceholderEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return MapAiPlaceholder(entity);
    }

    public async Task<OperationConfirmationDto> ConfirmAiPlaceholderAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var placeholder = await _db.Set<AiPlanningPlaceholderEntity>()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct)
            ?? throw new DomainException(02033, "AI placeholder does not exist");

        var confirmations = _confirmationService ?? new OperationConfirmationService(_db);
        var payloadJson = JsonSerializer.Serialize(new
        {
            placeholderId = placeholder.Id,
            placeholder.Title,
            placeholder.StartsAt,
            placeholder.EndsAt,
            placeholder.Reason
        });
        var confirmation = await confirmations.CreateAsync(
            new CreateOperationConfirmationRequest(
                userId,
                "calendar.ai_placeholder.confirm",
                $"Confirm AI planning placeholder: {placeholder.Title}",
                OperationRiskLevel.L2PimFactChange,
                placeholder.Source,
                payloadJson,
                JsonSerializer.Serialize(new
                {
                    placeholder.Title,
                    placeholder.StartsAt,
                    placeholder.EndsAt,
                    placeholder.Reason
                }),
                DateTimeOffset.UtcNow.AddHours(12),
                null,
                ["ai-placeholder"],
                ["confirm", "reject"],
                "ai-planning-placeholder",
                placeholder.Id,
                false,
                null,
                payloadJson,
                false,
                null,
                "Create a planned fact only after confirmation",
                null,
                "Reject the confirmation or restore from audit timeline"),
            ct);

        placeholder.ConfirmationId = confirmation.Id;
        placeholder.Status = "PendingConfirmation";
        placeholder.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return confirmation;
    }

    public async Task<TaskExecutionSegmentResponse> CreateSegmentAsync(
        Guid taskId,
        CreateTaskExecutionSegmentRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        if (endsAt <= startsAt)
            throw new DomainException(02024, "Segment end must be after start");

        ValidateShortRequired(request.Status, "Segment status");
        ValidateShortRequired(request.Source, "Segment source");

        var task = await GetTaskAsync(taskId, userId, ct);
        var now = DateTimeOffset.UtcNow;
        var segment = new TaskExecutionSegmentEntity
        {
            TaskId = task.Id,
            UserId = userId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Status = request.Status,
            Source = request.Source,
            PlanningReason = request.PlanningReason,
            CreatedAt = now,
            UpdatedAt = now
        };

        task.IsInbox = false;
        task.DtStart ??= startsAt;
        task.PlannedEnd ??= endsAt;
        task.UpdatedAt = now;

        _db.Set<TaskExecutionSegmentEntity>().Add(segment);
        await _db.SaveChangesAsync(ct);

        return MapSegment(segment, task.Title);
    }

    public async Task<IReadOnlyList<TaskExecutionSegmentResponse>> ListSegmentsAsync(
        Guid taskId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var task = await GetTaskAsync(taskId, userId, ct);
        return await _db.Set<TaskExecutionSegmentEntity>()
            .AsNoTracking()
            .Where(s => s.TaskId == task.Id && s.UserId == userId)
            .OrderBy(s => s.StartsAt)
            .Select(s => new TaskExecutionSegmentResponse(
                s.Id,
                s.TaskId,
                task.Title,
                s.StartsAt,
                s.EndsAt,
                s.Status,
                s.Source,
                s.PlanningReason,
                s.ConfirmationId))
            .ToListAsync(ct);
    }

    public async Task DeleteSegmentAsync(
        Guid taskId,
        Guid segmentId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        _ = await GetTaskAsync(taskId, userId, ct);
        var segment = await _db.Set<TaskExecutionSegmentEntity>()
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.TaskId == taskId && s.UserId == userId, ct)
            ?? throw new DomainException(02025, "Task execution segment does not exist");

        var now = DateTimeOffset.UtcNow;
        segment.DeletedAt = now;
        segment.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<TaskEntity> GetTaskAsync(Guid taskId, Guid userId, CancellationToken ct)
        => await _db.Set<TaskEntity>()
            .FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == userId, ct)
            ?? throw new DomainException(02004, "Task does not exist");

    private static void ValidateShortRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40)
            throw new DomainException(02026, $"{fieldName} must be 1-40 characters");
    }

    private static void ValidateRequired(string? value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new DomainException(02034, $"{fieldName} must be 1-{maxLength} characters");
    }

    private static string NormalizeShort(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (value.Length > 40)
            throw new DomainException(02035, "Value must be 1-40 characters");

        return value.Trim();
    }

    private static HabitCadence ParseCadence(string value)
        => Enum.TryParse<HabitCadence>(value, ignoreCase: true, out var cadence)
            ? cadence
            : HabitCadence.Custom;

    private static HashSet<string> NormalizeLayers(IReadOnlyList<string>? layers)
    {
        var raw = layers?
            .SelectMany(layer => layer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Select(layer => layer.ToLowerInvariant())
            .ToList();

        if (raw is null || raw.Count == 0)
            return new HashSet<string>(DefaultLayers, StringComparer.OrdinalIgnoreCase);

        if (raw.Any(s => s == "all"))
            return new HashSet<string>(DefaultLayers, StringComparer.OrdinalIgnoreCase);

        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["event"] = "events",
            ["events"] = "events",
            ["task"] = "task-segments",
            ["tasks"] = "task-segments",
            ["task-segment"] = "task-segments",
            ["task-segments"] = "task-segments",
            ["task_segments"] = "task-segments",
            ["tasksegments"] = "task-segments",
            ["habit"] = "habits",
            ["habits"] = "habits",
            ["availability"] = "availability",
            ["available"] = "availability",
            ["avail"] = "availability",
            ["ai"] = "ai-placeholders",
            ["ai-placeholder"] = "ai-placeholders",
            ["ai-placeholders"] = "ai-placeholders",
            ["ai_placeholders"] = "ai-placeholders",
            ["aiplaceholder"] = "ai-placeholders",
            ["aiplaceholders"] = "ai-placeholders",
        };

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw)
        {
            if (aliasMap.TryGetValue(token, out var canonical))
                normalized.Add(canonical);
            else if (DefaultLayers.Contains(token))
                normalized.Add(token);
            else
            {
                // unknown token: ignore silently to avoid returning empty for typo (e.g. layers=evnts)
                // if all tokens are unknown the result will be empty and fall back to defaults below
            }
        }

        return normalized.Count > 0
            ? normalized
            : new HashSet<string>(DefaultLayers, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsOutlookSource(string source) => OutlookSources.Contains(source);

    private static AiPlanningPlaceholderDto MapAiPlaceholder(AiPlanningPlaceholderEntity entity)
        => new(
            entity.Id,
            entity.Title,
            entity.StartsAt,
            entity.EndsAt,
            entity.Reason,
            entity.ConfirmationId);

    private static TaskExecutionSegmentResponse MapSegment(TaskExecutionSegmentEntity segment, string taskTitle)
        => new(
            segment.Id,
            segment.TaskId,
            taskTitle,
            segment.StartsAt,
            segment.EndsAt,
            segment.Status,
            segment.Source,
            segment.PlanningReason,
            segment.ConfirmationId);
}
