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

        var tasksToSchedule = tasks.Select(t => new TaskToSchedule(
            t.Id, t.Title, t.Priority,
            t.EstimatedDuration ?? TimeSpan.FromHours(1),
            t.MinimumSegment, t.Due, 1.0)).ToList();

        var busySlots = events.Select(e => new BusySlot(e.DtStart, e.DtEnd)).ToList();
        var now = DateTimeOffset.UtcNow;
        var searchEnd = now.AddDays(14);

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
