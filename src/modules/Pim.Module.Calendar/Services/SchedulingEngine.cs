using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class SchedulingEngine
{
    private readonly PimDbContext _db;
    private readonly List<ISchedulingAlgorithm> _algorithms;

    public SchedulingEngine(PimDbContext db)
    {
        _db = db;
        _algorithms = new List<ISchedulingAlgorithm>
        {
            new GreedyScheduler(),
            new CspScheduler(),
            new GeneticScheduler()
        };
    }

    public async Task<List<ScheduleSolution>> GeneratePlansAsync(
        Guid userId, List<Guid> taskIds, CancellationToken ct)
    {
        var tasks = await _db.Set<TaskEntity>()
            .Where(t => taskIds.Contains(t.Id) && t.EstimatedDuration.HasValue)
            .ToListAsync(ct);

        var events = await _db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == userId)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var searchEnd = now.AddDays(14);

        var tasksToSchedule = tasks.Select(t => new TaskToSchedule(
            t.Id, t.Title, t.Priority,
            t.EstimatedDuration ?? TimeSpan.FromHours(1),
            t.MinimumSegment, t.Due, 1.0)).ToList();

        var busySlots = events.Select(e => new BusySlot(e.DtStart, e.DtEnd)).ToList();

        // 可用时段参与排程：非 available 类型直接视为忙时；
        // 若用户定义了 available 窗口，则窗口之外的时间一律不可排程。
        var windows = await _db.Set<AvailabilityWindowEntity>()
            .Where(w => w.UserId == userId && w.DeletedAt == null && w.EndsAt > now)
            .ToListAsync(ct);

        busySlots.AddRange(windows
            .Where(w => !string.Equals(w.Kind, "available", StringComparison.OrdinalIgnoreCase))
            .Select(w => new BusySlot(w.StartsAt, w.EndsAt)));

        var available = windows
            .Where(w => string.Equals(w.Kind, "available", StringComparison.OrdinalIgnoreCase))
            .Select(w => new TimeSlot(w.StartsAt < now ? now : w.StartsAt, w.EndsAt > searchEnd ? searchEnd : w.EndsAt))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
        if (available.Count > 0)
        {
            busySlots.AddRange(InvertAvailableWindows(available, now, searchEnd));
        }

        var weights = await GetUserWeightsAsync(userId);

        var solutions = new List<ScheduleSolution>();
        foreach (var algo in _algorithms)
        {
            var solution = await algo.SolveAsync(
                tasksToSchedule, busySlots, now, searchEnd, weights, ct);
            if (solution is not null) solutions.Add(solution);
        }

        return solutions;
    }

    /// <summary>把 [start, end) 内未被 available 窗口覆盖的部分转成忙时。</summary>
    internal static List<BusySlot> InvertAvailableWindows(
        List<TimeSlot> available, DateTimeOffset start, DateTimeOffset end)
    {
        var busy = new List<BusySlot>();
        var cursor = start;
        foreach (var window in available)
        {
            if (window.Start > cursor)
                busy.Add(new BusySlot(cursor, window.Start));
            if (window.End > cursor)
                cursor = window.End;
            if (cursor >= end)
                break;
        }
        if (cursor < end)
            busy.Add(new BusySlot(cursor, end));
        return busy;
    }

    private async Task<Dictionary<string, double>> GetUserWeightsAsync(Guid userId)
    {
        var feedbacks = await _db.Set<SchedulingFeedbackEntity>()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .Take(50)
            .ToListAsync();

        if (feedbacks.Count < 5)
            return new Dictionary<string, double>
            {
                ["priority"] = 0.5,
                ["coverage"] = 0.3,
                ["compactness"] = 0.2
            };

        return new Dictionary<string, double>
        {
            ["priority"] = 0.6,
            ["coverage"] = 0.25,
            ["compactness"] = 0.15
        };
    }
}
