using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class SchedulingHelpersTests
{
    [Fact]
    public void ComputeFreeSlots_NoBusySlots_ReturnsFullRange()
    {
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);

        var free = SchedulingHelpers.ComputeFreeSlots(new(), start, end);

        Assert.Single(free);
        Assert.Equal(start, free[0].Start);
        Assert.Equal(end, free[0].End);
    }

    [Fact]
    public void ComputeFreeSlots_OneBusyInMiddle_ReturnsTwoFreeSlots()
    {
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);
        var busy = new List<BusySlot>
        {
            new(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 15, 13, 0, 0, TimeSpan.Zero))
        };

        var free = SchedulingHelpers.ComputeFreeSlots(busy, start, end);

        Assert.Equal(2, free.Count);
        Assert.Equal(start, free[0].Start);
        Assert.Equal(busy[0].Start, free[0].End);
        Assert.Equal(busy[0].End, free[1].Start);
        Assert.Equal(end, free[1].End);
    }

    [Fact]
    public void ComputeFreeSlots_BusyOutsideRange_DoesNotAffectFree()
    {
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);
        var busy = new List<BusySlot>
        {
            new(new DateTimeOffset(2026, 5, 15, 6, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 15, 7, 0, 0, TimeSpan.Zero))
        };

        var free = SchedulingHelpers.ComputeFreeSlots(busy, start, end);

        Assert.Single(free);
    }

    [Fact]
    public void ComputeFreeSlots_OverlappingBusy_MergesCorrectly()
    {
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);
        var busy = new List<BusySlot>
        {
            new(new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero)),
            new(new DateTimeOffset(2026, 5, 15, 11, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 15, 14, 0, 0, TimeSpan.Zero))
        };

        var free = SchedulingHelpers.ComputeFreeSlots(busy, start, end);

        Assert.Equal(2, free.Count);
        Assert.Equal(start, free[0].Start);
        Assert.Equal(busy[0].Start, free[0].End);
        Assert.Equal(busy[1].End, free[1].Start);
    }
}

public class GreedySchedulerTests
{
    [Fact]
    public async Task SolveAsync_NoTasks_ReturnsEmptySolution()
    {
        var scheduler = new GreedyScheduler();
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);

        var solution = await scheduler.SolveAsync(new(), new(), start, end, new(), CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal("greedy", solution!.AlgorithmName);
        Assert.Empty(solution.Slots);
    }

    [Fact]
    public async Task SolveAsync_SingleTask_AssignsToFirstSlot()
    {
        var scheduler = new GreedyScheduler();
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);
        var tasks = new List<TaskToSchedule>
        {
            new(Guid.NewGuid(), "Task1", 1, TimeSpan.FromHours(1), null, null)
        };

        var solution = await scheduler.SolveAsync(tasks, new(), start, end, new(), CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Single(solution!.Slots);
        Assert.Equal("Task1", solution.Slots[0].Title);
    }

    [Fact]
    public async Task SolveAsync_MultipleTasks_SchedulesByPriority()
    {
        var scheduler = new GreedyScheduler();
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);
        var tasks = new List<TaskToSchedule>
        {
            new(Guid.NewGuid(), "Low", 0, TimeSpan.FromHours(1), null, null),
            new(Guid.NewGuid(), "High", 2, TimeSpan.FromHours(1), null, null)
        };

        var solution = await scheduler.SolveAsync(tasks, new(), start, end, new(), CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal(2, solution!.Slots.Count);
        Assert.Equal("High", solution.Slots[0].Title);
    }
}

public class GeneticSchedulerTests
{
    [Fact]
    public async Task SolveAsync_SingleTask_ReturnsAssignment()
    {
        var scheduler = new GeneticScheduler();
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);
        var tasks = new List<TaskToSchedule>
        {
            new(Guid.NewGuid(), "Task", 1, TimeSpan.FromHours(1), null, null)
        };

        var solution = await scheduler.SolveAsync(tasks, new(), start, end, new(), CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal("genetic", solution!.AlgorithmName);
        Assert.NotEmpty(solution.Slots);
    }

    [Fact]
    public async Task SolveAsync_NoFreeTime_ReturnsEmptySlots()
    {
        var scheduler = new GeneticScheduler();
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero);
        var busy = new List<BusySlot>
        {
            new(start, end)
        };
        var tasks = new List<TaskToSchedule>
        {
            new(Guid.NewGuid(), "Task", 1, TimeSpan.FromHours(1), null, null)
        };

        var solution = await scheduler.SolveAsync(tasks, busy, start, end, new(), CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Empty(solution!.Slots);
    }
}

public class CspSchedulerTests
{
    [Fact]
    public async Task SolveAsync_AssignsTasksWithConstraints()
    {
        var scheduler = new CspScheduler();
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 18, 0, 0, TimeSpan.Zero);
        var tasks = new List<TaskToSchedule>
        {
            new(Guid.NewGuid(), "Task1", 1, TimeSpan.FromHours(2), null, null),
            new(Guid.NewGuid(), "Task2", 0, TimeSpan.FromHours(1), null, null)
        };

        var solution = await scheduler.SolveAsync(tasks, new(), start, end, new(), CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal("csp", solution!.AlgorithmName);
    }

    [Fact]
    public async Task SolveAsync_RelaxesConstraintsForOversized()
    {
        var scheduler = new CspScheduler();
        var start = new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero);
        var tasks = new List<TaskToSchedule>
        {
            new(Guid.NewGuid(), "Big", 1, TimeSpan.FromHours(3), TimeSpan.FromMinutes(15),
                new DateTimeOffset(2026, 5, 15, 10, 0, 0, TimeSpan.Zero))
        };

        var solution = await scheduler.SolveAsync(tasks, new(), start, end, new(), CancellationToken.None);

        Assert.NotNull(solution);
        Assert.NotEmpty(solution!.Slots);
    }
}
