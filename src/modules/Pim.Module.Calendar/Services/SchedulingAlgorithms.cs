namespace Pim.Module.Calendar.Services;

public record TimeSlot(DateTimeOffset Start, DateTimeOffset End);
public record TaskToSchedule(Guid TaskId, string Title, int Priority, TimeSpan Duration,
    TimeSpan? MinSegment, DateTimeOffset? Deadline, double UserPreferenceWeight = 1.0);
public record BusySlot(DateTimeOffset Start, DateTimeOffset End);
public record ScheduleSolution(string AlgorithmName, List<ScheduledSlot> Slots, Dictionary<string, double> Metrics);
public record ScheduledSlot(Guid TaskId, string Title, DateTimeOffset Start, DateTimeOffset End);

public static class SchedulingHelpers
{
    public static List<TimeSlot> ComputeFreeSlots(
        List<BusySlot> busy, DateTimeOffset start, DateTimeOffset end)
    {
        var free = new List<TimeSlot>();
        var sorted = busy.OrderBy(b => b.Start).ToList();
        var cursor = start;
        foreach (var b in sorted)
        {
            if (b.End <= cursor) continue;
            if (b.Start > cursor) free.Add(new TimeSlot(cursor, b.Start));
            cursor = b.End > cursor ? b.End : cursor;
        }
        if (cursor < end) free.Add(new TimeSlot(cursor, end));
        return free;
    }
}

public interface ISchedulingAlgorithm
{
    string Name { get; }
    Task<ScheduleSolution?> SolveAsync(
        List<TaskToSchedule> tasks,
        List<BusySlot> busySlots,
        DateTimeOffset searchStart,
        DateTimeOffset searchEnd,
        Dictionary<string, double> userWeights,
        CancellationToken ct);
}

public class GreedyScheduler : ISchedulingAlgorithm
{
    public string Name => "greedy";

    public Task<ScheduleSolution?> SolveAsync(
        List<TaskToSchedule> tasks,
        List<BusySlot> busySlots,
        DateTimeOffset searchStart,
        DateTimeOffset searchEnd,
        Dictionary<string, double> userWeights,
        CancellationToken ct)
    {
        var sorted = tasks.OrderByDescending(t => t.Priority)
            .ThenBy(t => t.Deadline ?? DateTimeOffset.MaxValue)
            .ToList();

        var mutableFreeSlots = SchedulingHelpers.ComputeFreeSlots(busySlots, searchStart, searchEnd).ToList();
        var result = new List<ScheduledSlot>();

        foreach (var task in sorted)
        {
            var remaining = task.Duration;
            for (var i = 0; i < mutableFreeSlots.Count && remaining > TimeSpan.Zero; i++)
            {
                var slot = mutableFreeSlots[i];
                var slotDuration = slot.End - slot.Start;
                if (slotDuration <= TimeSpan.Zero) continue;

                var alloc = remaining < slotDuration ? remaining : slotDuration;
                result.Add(new ScheduledSlot(task.TaskId, task.Title, slot.Start, slot.Start + alloc));
                remaining -= alloc;

                if (alloc >= slotDuration)
                {
                    mutableFreeSlots.RemoveAt(i);
                    i--;
                }
                else
                {
                    mutableFreeSlots[i] = new TimeSlot(slot.Start + alloc, slot.End);
                }
            }
        }

        return Task.FromResult<ScheduleSolution?>(
            new ScheduleSolution(Name, result, new Dictionary<string, double>
            {
                ["tasks_scheduled"] = result.Select(s => s.TaskId).Distinct().Count(),
                ["total_tasks"] = tasks.Count,
                ["slots_allocated"] = result.Count
            }));
    }
}

public class CspScheduler : ISchedulingAlgorithm
{
    public string Name => "csp";
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    public Task<ScheduleSolution?> SolveAsync(
        List<TaskToSchedule> tasks,
        List<BusySlot> busySlots,
        DateTimeOffset searchStart,
        DateTimeOffset searchEnd,
        Dictionary<string, double> userWeights,
        CancellationToken ct)
    {
        var freeSlots = SchedulingHelpers.ComputeFreeSlots(busySlots, searchStart, searchEnd);

        // Phase 1: Try to assign tasks greedily
        var solution = new List<ScheduledSlot>();
        var unscheduled = new List<TaskToSchedule>();
        var assignedFreeSlots = freeSlots.Select(s =>
            new { Slot = s, Remaining = s.End - s.Start, Start = s.Start }).ToList();

        var sorted = tasks.OrderByDescending(t => t.Priority)
            .ThenBy(t => t.Deadline ?? DateTimeOffset.MaxValue).ToList();

        foreach (var task in sorted)
        {
            var placed = false;
            foreach (var slot in assignedFreeSlots.Where(s => s.Remaining >= task.Duration))
            {
                solution.Add(new ScheduledSlot(task.TaskId, task.Title,
                    slot.Start, slot.Start + task.Duration));
                var newRemaining = slot.Remaining - task.Duration;
                var idx = assignedFreeSlots.IndexOf(slot);
                assignedFreeSlots[idx] = new { slot.Slot, Remaining = newRemaining,
                    Start = slot.Start + task.Duration };
                placed = true;
                break;
            }

            if (!placed) unscheduled.Add(task);
        }

        // Phase 2: Try constraint relaxation for unscheduled tasks (reduce min segment)
        foreach (var task in unscheduled)
        {
            var relaxedDuration = task.MinSegment ?? TimeSpan.FromMinutes(15);
            foreach (var slot in assignedFreeSlots.Where(s => s.Remaining >= relaxedDuration).ToList())
            {
                solution.Add(new ScheduledSlot(task.TaskId, task.Title,
                    slot.Start, slot.Start + relaxedDuration));
                var idx = assignedFreeSlots.IndexOf(slot);
                assignedFreeSlots[idx] = new { slot.Slot, Remaining = slot.Remaining - relaxedDuration, Start = slot.Start + relaxedDuration };
                break;
            }
        }

        return Task.FromResult<ScheduleSolution?>(
            new ScheduleSolution(Name, solution, new Dictionary<string, double>
            {
                ["tasks_scheduled"] = solution.Count,
                ["total_tasks"] = tasks.Count,
                ["constraint_relaxations"] = unscheduled.Count
            }));
    }
}

public class GeneticScheduler : ISchedulingAlgorithm
{
    public string Name => "genetic";
    private const int PopulationSize = 50;
    private const int Generations = 100;
    private const double MutationRate = 0.1;

    public Task<ScheduleSolution?> SolveAsync(
        List<TaskToSchedule> tasks,
        List<BusySlot> busySlots,
        DateTimeOffset searchStart,
        DateTimeOffset searchEnd,
        Dictionary<string, double> userWeights,
        CancellationToken ct)
    {
        var rng = Random.Shared;
        var freeSlots = SchedulingHelpers.ComputeFreeSlots(busySlots, searchStart, searchEnd);
        var population = new List<List<ScheduledSlot>>();

        // Initialize population
        for (int i = 0; i < PopulationSize; i++)
        {
            population.Add(RandomSchedule(tasks, freeSlots, rng));
        }

        // Evolve
        for (int gen = 0; gen < Generations && !ct.IsCancellationRequested; gen++)
        {
            var fitnesses = population.Select(p =>
                Fitness(p, tasks, userWeights)).ToList();
            var newPop = new List<List<ScheduledSlot>>();

            // Elitism: keep top 5
            var elite = population.Zip(fitnesses)
                .OrderByDescending(x => x.Second)
                .Take(5).Select(x => x.First).ToList();
            newPop.AddRange(elite);

            // Crossover + Mutation
            while (newPop.Count < PopulationSize)
            {
                var parent1 = SelectParent(population, fitnesses, rng);
                var parent2 = SelectParent(population, fitnesses, rng);
                var child = Crossover(parent1, parent2, rng);
                if (rng.NextDouble() < MutationRate)
                    Mutate(child, freeSlots, rng);
                newPop.Add(child);
            }

            population = newPop;
        }

        var best = population.Zip(population.Select(p => Fitness(p, tasks, userWeights)))
            .OrderByDescending(x => x.Second).First().First;

        return Task.FromResult<ScheduleSolution?>(
            new ScheduleSolution(Name, best, new Dictionary<string, double>
            {
                ["fitness"] = Fitness(best, tasks, userWeights),
                ["tasks_scheduled"] = best.Count
            }));
    }

    private List<ScheduledSlot> RandomSchedule(
        List<TaskToSchedule> tasks, List<TimeSlot> freeSlots, Random rng)
    {
        var result = new List<ScheduledSlot>();
        var shuffled = new List<TaskToSchedule>(tasks);
        var n = shuffled.Count;
        while (n > 1)
        {
            n--;
            var k = rng.Next(n + 1);
            (shuffled[k], shuffled[n]) = (shuffled[n], shuffled[k]);
        }
        var remainingSlots = freeSlots.Select(s =>
            (Start: s.Start, Remaining: s.End - s.Start)).ToList();

        foreach (var task in shuffled)
        {
            var candidates = remainingSlots
                .Where(s => s.Remaining >= task.Duration).ToList();
            if (!candidates.Any()) continue;
            var slot = candidates[rng.Next(candidates.Count)];
            result.Add(new ScheduledSlot(task.TaskId, task.Title,
                slot.Start, slot.Start + task.Duration));
            var idx = remainingSlots.IndexOf(slot);
            remainingSlots[idx] = (slot.Start + task.Duration,
                slot.Remaining - task.Duration);
        }
        return result;
    }

    private double Fitness(List<ScheduledSlot> slots,
        List<TaskToSchedule> tasks, Dictionary<string, double> weights)
    {
        var scheduledIds = slots.Select(s => s.TaskId).ToHashSet();
        var coverage = (double)scheduledIds.Count / tasks.Count;
        var prioritySum = tasks.Where(t => scheduledIds.Contains(t.TaskId))
            .Sum(t => t.Priority);
        var totalPriority = tasks.Sum(t => t.Priority);
        var priorityScore = totalPriority > 0 ? prioritySum / totalPriority : 0;
        var priorityWeight = weights.GetValueOrDefault("priority", 0.5);
        var coverageWeight = weights.GetValueOrDefault("coverage", 0.5);
        return priorityWeight * priorityScore + coverageWeight * coverage;
    }

    private List<ScheduledSlot> SelectParent(
        List<List<ScheduledSlot>> pop, List<double> fitnesses, Random rng)
    {
        var total = fitnesses.Sum();
        var r = rng.NextDouble() * total;
        var cumulative = 0.0;
        for (int i = 0; i < pop.Count; i++)
        {
            cumulative += fitnesses[i];
            if (r <= cumulative) return pop[i];
        }
        return pop.Last();
    }

    private List<ScheduledSlot> Crossover(
        List<ScheduledSlot> a, List<ScheduledSlot> b, Random rng)
    {
        var split = rng.Next(Math.Min(a.Count, b.Count));
        return a.Take(split).Concat(b.Skip(split)).ToList();
    }

    private void Mutate(List<ScheduledSlot> schedule,
        List<TimeSlot> freeSlots, Random rng)
    {
        if (schedule.Count > 0)
        {
            var idx = rng.Next(schedule.Count);
            schedule.RemoveAt(idx);
        }
    }
}
