using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Ai;
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
    private readonly IAiGateway? _aiGateway;

    public PlanningModelService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IOperationConfirmationService? confirmationService = null,
        RecurrenceService? recurrence = null,
        IAiGateway? aiGateway = null)
    {
        _db = db;
        _currentUser = currentUser;
        _confirmationService = confirmationService;
        _recurrence = recurrence ?? new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        _aiGateway = aiGateway;
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
        var books = await _db.Set<TaskBookEntity>()
            .AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);

        var counts = await _db.Set<TaskEntity>()
            .AsNoTracking()
            .Where(t => t.UserId == userId
                && t.TaskBookId != null
                && t.DeletedAt == null)
            .GroupBy(t => t.TaskBookId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var countsByBook = counts
            .Where(g => g.Key.HasValue)
            .ToDictionary(g => g.Key!.Value, g => g.Count);

        return books
            .Select(b => new TaskBookDto(
                b.Id,
                b.DomainProjectId,
                b.Name,
                b.Kind,
                b.Status,
                countsByBook.GetValueOrDefault(b.Id)))
            .ToList();
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

    public async Task<TaskChecklistItemDto> UpdateChecklistItemAsync(
        Guid taskId,
        Guid itemId,
        UpdateTaskChecklistItemRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var task = await GetTaskAsync(taskId, userId, ct);
        var item = await _db.Set<TaskChecklistItemEntity>()
            .FirstOrDefaultAsync(i => i.Id == itemId
                && i.TaskId == task.Id
                && i.UserId == userId
                && i.DeletedAt == null, ct)
            ?? throw new DomainException(02037, "Checklist item does not exist");

        if (request.Title is not null)
        {
            ValidateRequired(request.Title, "Checklist title", 255);
            item.Title = request.Title.Trim();
        }

        if (request.IsDone.HasValue)
            item.IsDone = request.IsDone.Value;

        item.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new TaskChecklistItemDto(item.Id, item.TaskId, item.Title, item.IsDone, item.SortOrder);
    }

    public async Task<string> DeleteChecklistItemAsync(
        Guid taskId,
        Guid itemId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var task = await GetTaskAsync(taskId, userId, ct);
        var item = await _db.Set<TaskChecklistItemEntity>()
            .FirstOrDefaultAsync(i => i.Id == itemId
                && i.TaskId == task.Id
                && i.UserId == userId
                && i.DeletedAt == null, ct)
            ?? throw new DomainException(02037, "Checklist item does not exist");

        var now = DateTimeOffset.UtcNow;
        item.DeletedAt = now;
        item.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return taskId.ToString();
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

    /// <summary>列出当前用户的排程建议（默认仅 Suggested 状态）。</summary>
    public async Task<IReadOnlyList<AiPlanPlaceholderViewDto>> ListAiPlaceholdersAsync(
        string? status, CancellationToken ct = default)
    {
        var userId = UserId;
        var query = _db.Set<AiPlanningPlaceholderEntity>()
            .Where(p => p.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(p => p.Status == status);
        else
            query = query.Where(p => p.Status == "Suggested");

        var items = await query
            .OrderBy(p => p.StartsAt)
            .Take(50)
            .ToListAsync(ct);
        return items.Select(MapAiPlaceholderView).ToList();
    }

    /// <summary>忽略一条排程建议（不进入确认流）。</summary>
    public async Task<AiPlanPlaceholderViewDto> DismissAiPlaceholderAsync(
        Guid id, CancellationToken ct = default)
    {
        var userId = UserId;
        var placeholder = await _db.Set<AiPlanningPlaceholderEntity>()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct)
            ?? throw new DomainException(02033, "AI placeholder does not exist");

        if (placeholder.Status is "Suggested" or "Dismissed")
        {
            placeholder.Status = "Dismissed";
            placeholder.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return MapAiPlaceholderView(placeholder);
    }

    /// <summary>
    /// 生成排程建议：优先调用 AI 网关（AI 关闭、调用失败或输出非法时回退规则引擎）。
    /// 建议一律为 Suggested 状态，经既有确认流写回日历——AI 只提建议，不碰核心事实。
    /// </summary>
    public async Task<GenerateAiPlanResponse> GenerateAiPlanAsync(
        GenerateAiPlanRequest request, CancellationToken ct = default)
    {
        var userId = UserId;
        var horizonDays = Math.Clamp(request.HorizonDays ?? 7, 1, 30);
        var now = DateTimeOffset.UtcNow;
        var horizonEnd = now.AddDays(horizonDays);

        var tasksQuery = _db.Set<TaskEntity>()
            .Where(t => t.UserId == userId
                        && t.EstimatedDuration.HasValue
                        && t.CompletedAt == null
                        && t.DeletedAt == null
                        && t.Status != "CANCELLED");
        if (request.TaskIds is { Count: > 0 })
            tasksQuery = tasksQuery.Where(t => request.TaskIds.Contains(t.Id));

        var tasks = await tasksQuery
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.Due)
            .Take(10)
            .ToListAsync(ct);

        if (tasks.Count == 0)
            return new GenerateAiPlanResponse("none", []);

        if (_aiGateway is not null)
        {
            var aiResult = await TryGenerateWithAiAsync(userId, tasks, now, horizonEnd, ct);
            if (aiResult is not null)
                return aiResult;
        }

        return await GenerateWithEngineAsync(userId, tasks, ct);
    }

    private async Task<GenerateAiPlanResponse?> TryGenerateWithAiAsync(
        Guid userId, List<TaskEntity> tasks, DateTimeOffset now, DateTimeOffset horizonEnd,
        CancellationToken ct)
    {
        var windows = await _db.Set<AvailabilityWindowEntity>()
            .Where(w => w.UserId == userId && w.DeletedAt == null && w.EndsAt > now)
            .OrderBy(w => w.StartsAt)
            .Take(30)
            .ToListAsync(ct);
        var events = await _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == userId && e.DtEnd > now && e.DtStart < horizonEnd)
            .OrderBy(e => e.DtStart)
            .Take(30)
            .ToListAsync(ct);

        var payload = new
        {
            now,
            horizonEnd,
            tasks = tasks.Select(t => new
            {
                t.Title,
                durationMinutes = (int)(t.EstimatedDuration ?? TimeSpan.FromHours(1)).TotalMinutes,
                t.Priority,
                t.Due
            }),
            availableWindows = windows
                .Where(w => string.Equals(w.Kind, "available", StringComparison.OrdinalIgnoreCase))
                .Select(w => new { w.StartsAt, w.EndsAt }),
            busyEvents = events.Select(e => new { e.DtStart, e.DtEnd })
        };

        AiResult result;
        try
        {
            result = await _aiGateway!.CompleteAsync(new AiGatewayRequest(
                Module: "calendar",
                Purpose: "calendar.ai_plan",
                SourceObjectType: "user",
                SourceObjectId: userId.ToString(),
                Messages:
                [
                    new AiMessage(AiMessageRole.System,
                        "你是排程助手。根据用户的待办任务、可用时段与已有日程，给出排程建议。"
                        + "只输出一个 JSON 数组，不要输出其他任何文字。数组元素形如 "
                        + "{\"title\":\"任务标题\",\"start\":\"ISO8601 开始\",\"end\":\"ISO8601 结束\",\"reason\":\"一句话理由\"}。"
                        + "规则：start/end 必须在 now 与 horizonEnd 之间、避开 busyEvents、"
                        + "若给了 availableWindows 则只能排在窗口内、end 必须晚于 start、最多 10 条。"),
                    new AiMessage(AiMessageRole.User, JsonSerializer.Serialize(payload))
                ],
                Model: null,
                SchemaName: null,
                SchemaVersion: null,
                MaxOutputTokens: 2000,
                MaxAttempts: 2,
                Metadata: new Dictionary<string, string> { ["endpoint"] = "calendar/ai-placeholders/generate" }), ct);
        }
        catch
        {
            return null;
        }

        if (result.Status != AiRequestStatus.Succeeded || string.IsNullOrWhiteSpace(result.ResponseText))
            return null;

        var items = ParseAiPlanJson(result.ResponseText, now, horizonEnd);
        if (items.Count == 0)
            return null;

        var created = await PersistPlaceholdersAsync(userId, items, "ai", ct);
        return new GenerateAiPlanResponse("ai", created);
    }

    /// <summary>防御式解析 AI 输出：定位首个 [ 与末个 ]，逐条校验时间与条数。</summary>
    internal static List<AiPlanItem> ParseAiPlanJson(string text, DateTimeOffset now, DateTimeOffset horizonEnd)
    {
        var startIdx = text.IndexOf('[');
        var endIdx = text.LastIndexOf(']');
        if (startIdx < 0 || endIdx <= startIdx)
            return [];

        var items = new List<AiPlanItem>();
        try
        {
            using var doc = JsonDocument.Parse(text[startIdx..(endIdx + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("title", out var titleEl) || titleEl.ValueKind != JsonValueKind.String)
                    continue;
                var hasStart = el.TryGetProperty("start", out var startEl) || el.TryGetProperty("startsAt", out startEl);
                var hasEnd = el.TryGetProperty("end", out var endEl) || el.TryGetProperty("endsAt", out endEl);
                if (!hasStart || !hasEnd)
                    continue;
                if (!DateTimeOffset.TryParse(startEl.GetString(), out var startAt)
                    || !DateTimeOffset.TryParse(endEl.GetString(), out var endAt))
                    continue;
                if (endAt <= startAt)
                    continue;
                if (startAt < now.AddHours(-1) || endAt > horizonEnd.AddDays(1))
                    continue;

                var title = titleEl.GetString()?.Trim();
                if (string.IsNullOrEmpty(title) || title.Length > 255)
                    continue;
                var reason = el.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                    ? reasonEl.GetString() ?? "AI 排程建议"
                    : "AI 排程建议";
                items.Add(new AiPlanItem(title, startAt, endAt, reason));
                if (items.Count >= 10) break;
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return items;
    }

    private async Task<GenerateAiPlanResponse> GenerateWithEngineAsync(
        Guid userId, List<TaskEntity> tasks, CancellationToken ct)
    {
        var engine = new SchedulingEngine(_db);
        var solutions = await engine.GeneratePlansAsync(userId, tasks.Select(t => t.Id).ToList(), ct);
        var solution = solutions.FirstOrDefault(s => s.AlgorithmName.Contains("greedy", StringComparison.OrdinalIgnoreCase))
                       ?? solutions.FirstOrDefault();
        if (solution is null || solution.Slots.Count == 0)
            return new GenerateAiPlanResponse("none", []);

        var items = solution.Slots.Take(10)
            .Select(s => new AiPlanItem(s.Title, s.Start, s.End, $"规则引擎建议（{solution.AlgorithmName}）"))
            .ToList();
        var created = await PersistPlaceholdersAsync(userId, items, "rule-engine", ct);
        return new GenerateAiPlanResponse("rule-engine", created);
    }

    private async Task<IReadOnlyList<AiPlanPlaceholderViewDto>> PersistPlaceholdersAsync(
        Guid userId, List<AiPlanItem> items, string source, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var created = new List<AiPlanPlaceholderViewDto>();
        foreach (var item in items)
        {
            var entity = new AiPlanningPlaceholderEntity
            {
                UserId = userId,
                Title = item.Title,
                StartsAt = item.StartsAt.ToUniversalTime(),
                EndsAt = item.EndsAt.ToUniversalTime(),
                Reason = item.Reason,
                Source = source,
                Status = "Suggested",
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Set<AiPlanningPlaceholderEntity>().Add(entity);
            created.Add(MapAiPlaceholderView(entity));
        }
        await _db.SaveChangesAsync(ct);
        return created;
    }

    internal sealed record AiPlanItem(string Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Reason);

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

    private static AiPlanPlaceholderViewDto MapAiPlaceholderView(AiPlanningPlaceholderEntity entity)
        => new(
            entity.Id,
            entity.Title,
            entity.StartsAt,
            entity.EndsAt,
            entity.Reason,
            entity.Status,
            entity.Source,
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
