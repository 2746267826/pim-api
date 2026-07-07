using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Calendar;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarWorkbenchQueryTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset RangeStart = new(2026, 5, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RangeEnd = new(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> OutlookSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook",
        "outlook-graph",
        "outlook-ics"
    };

    [Fact]
    public async Task DataCenterQueryAsync_MergesActiveItemsConfirmationsAndRecycleBin()
    {
        await using var db = CreateDb();
        var seed = SeedWorkbench(db);
        await db.SaveChangesAsync();
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId));

        var result = await service.QueryAsync(new DataCenterQueryRequest(null, null, null, false, 1, 50));

        Assert.Equal(1, result.Page);
        Assert.Equal(50, result.PageSize);
        Assert.Equal(result.Items.Count, result.TotalCount);
        Assert.Contains(result.Items, item => item.ObjectType == "event" && item.ObjectId == seed.ActiveEventId);
        Assert.Contains(result.Items, item => item.ObjectType == "task" && item.ObjectId == seed.ActiveTaskId);
        Assert.Contains(result.Items, item => item.ObjectType == "task-segment" && item.ObjectId == seed.ActiveSegmentId);
        Assert.Contains(result.Items, item => item.ObjectType == "confirmation" && item.ObjectId == seed.ConfirmationId);
        Assert.Contains(result.Items, item => item.ObjectType == "recycle-bin" && item.ObjectId == seed.DeletedEventId);
        Assert.DoesNotContain(result.Items, item => item.ObjectType == "event" && item.ObjectId == seed.DeletedEventId);
    }

    [Fact]
    public async Task DataCenterQueryAsync_AppliesSearchObjectTypeSourcePendingAndPaging()
    {
        await using var db = CreateDb();
        var seed = SeedWorkbench(db);
        db.OperationConfirmations.Add(new OperationConfirmationEntity
        {
            RequestedByUserId = UserId,
            OperationType = "calendar.expired",
            Summary = "Expired stale confirmation",
            RiskLevel = OperationRiskLevel.L2PimFactChange.ToString(),
            Source = "calendar",
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        });
        await db.SaveChangesAsync();
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId));

        var searchResult = await service.QueryAsync(new DataCenterQueryRequest("focus", null, null, false, 1, 50));
        Assert.Contains(searchResult.Items, item => item.ObjectId == seed.ActiveEventId);
        Assert.Contains(searchResult.Items, item => item.ObjectId == seed.ActiveSegmentId);

        var objectTypeResult = await service.QueryAsync(new DataCenterQueryRequest(null, "confirmation", null, false, 1, 50));
        Assert.Contains(objectTypeResult.Items, item => item.ObjectId == seed.ConfirmationId);
        Assert.Contains(objectTypeResult.Items, item => item.Summary == "Expired stale confirmation");

        var sourceResult = await service.QueryAsync(new DataCenterQueryRequest(null, null, "outlook-ics", false, 1, 50));
        var outlookItem = Assert.Single(sourceResult.Items);
        Assert.Equal(seed.ActiveEventId, outlookItem.ObjectId);

        var pendingResult = await service.QueryAsync(new DataCenterQueryRequest(null, null, null, true, 1, 50));
        var pendingConfirmation = Assert.Single(pendingResult.Items);
        Assert.Equal("confirmation", pendingConfirmation.ObjectType);
        Assert.Equal(seed.ConfirmationId, pendingConfirmation.ObjectId);
        Assert.Equal(OperationConfirmationStatus.Pending.ToString(), pendingConfirmation.Status);

        var pageResult = await service.QueryAsync(new DataCenterQueryRequest(null, null, null, false, 2, 2));
        Assert.Equal(2, pageResult.Page);
        Assert.Equal(2, pageResult.PageSize);
        Assert.Equal(2, pageResult.Items.Count);
        Assert.True(pageResult.TotalCount > pageResult.Items.Count);
    }

    [Fact]
    public async Task GetCalendarLayersAsync_ReturnsRequestedLayersAndFiltersOutlookOnly()
    {
        await using var db = CreateDb();
        var seed = SeedWorkbench(db);
        await db.SaveChangesAsync();
        var service = new PlanningModelService(db, new FixedCurrentUserService(UserId));

        var response = await service.GetCalendarLayersAsync(
            new CalendarLayerQuery(RangeStart, RangeEnd, new[] { "events", "task-segments" }));

        Assert.Equal(RangeStart, response.Start);
        Assert.Equal(RangeEnd, response.End);
        Assert.Contains(response.Items, item =>
            item.Layer == "events"
            && item.ObjectType == "event"
            && item.ObjectId == seed.ActiveEventId
            && item.Color == "#2563EB");
        Assert.Contains(response.Items, item =>
            item.Layer == "task-segments"
            && item.ObjectType == "task-segment"
            && item.ObjectId == seed.ActiveSegmentId
            && item.Color == "#22C55E");
        Assert.DoesNotContain(response.Items, item => item.ObjectId == seed.DeletedEventId);

        var segmentOnly = await service.GetCalendarLayersAsync(
            new CalendarLayerQuery(RangeStart, RangeEnd, new[] { "task-segments" }));
        Assert.All(segmentOnly.Items, item => Assert.Equal("task-segments", item.Layer));

        var outlookOnly = await service.GetCalendarLayersAsync(
            new CalendarLayerQuery(RangeStart, RangeEnd, null, OutlookOnly: true));
        Assert.Contains(outlookOnly.Items, item => item.ObjectId == seed.ActiveEventId);
        Assert.DoesNotContain(outlookOnly.Items, item => item.ObjectId == seed.ActiveSegmentId);
        Assert.All(outlookOnly.Items, item => Assert.Contains(item.Source, OutlookSources));
    }

    [Fact]
    public void CalendarWorkbenchEndpointPaths_AreStable()
    {
        Assert.Equal("/api/v1/calendar/layers", CalendarEndpointPaths.CalendarLayers);
        Assert.Equal("/api/v1/calendar/data-center/query", CalendarEndpointPaths.DataCenterQuery);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-workbench-query-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static WorkbenchSeed SeedWorkbench(PimDbContext db)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = "Work",
            Kind = "calendar",
            Color = "#2563EB"
        };
        var activeEvent = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "outlook-focus@pim",
            Title = "Outlook focus block",
            Description = "Focus from Outlook",
            DtStart = new DateTimeOffset(2026, 5, 26, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook-ics",
            Status = "CONFIRMED"
        };
        var deletedEvent = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "deleted@pim",
            Title = "Deleted event",
            DtStart = new DateTimeOffset(2026, 5, 26, 11, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero),
            Source = "manual",
            DeletedAt = new DateTimeOffset(2026, 5, 26, 12, 30, 0, TimeSpan.Zero)
        };
        var task = new TaskEntity
        {
            UserId = UserId,
            Uid = "manual-focus-task@pim",
            Title = "Manual focus task",
            Description = "Task linked to a segment",
            DtStart = new DateTimeOffset(2026, 5, 26, 13, 0, 0, TimeSpan.Zero),
            PlannedEnd = new DateTimeOffset(2026, 5, 26, 14, 0, 0, TimeSpan.Zero),
            Status = "NEEDS-ACTION"
        };
        var segment = new TaskExecutionSegmentEntity
        {
            Task = task,
            TaskId = task.Id,
            UserId = UserId,
            StartsAt = new DateTimeOffset(2026, 5, 26, 13, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 5, 26, 13, 30, 0, TimeSpan.Zero),
            Status = "planned",
            Source = "manual",
            PlanningReason = "focus window"
        };
        var confirmation = new OperationConfirmationEntity
        {
            RequestedByUserId = UserId,
            OperationType = "calendar.batch_delete",
            Summary = "Pending cleanup confirmation",
            RiskLevel = OperationRiskLevel.L4BatchOrDestructiveGovernance.ToString(),
            Source = "outlook-graph",
            Status = OperationConfirmationStatus.Pending.ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            CreatedAt = new DateTimeOffset(2026, 5, 26, 8, 0, 0, TimeSpan.Zero)
        };

        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().AddRange(activeEvent, deletedEvent);
        db.Set<TaskEntity>().Add(task);
        db.Set<TaskExecutionSegmentEntity>().Add(segment);
        db.OperationConfirmations.Add(confirmation);

        return new WorkbenchSeed(activeEvent.Id, task.Id, segment.Id, confirmation.Id, deletedEvent.Id);
    }

    private sealed record WorkbenchSeed(
        Guid ActiveEventId,
        Guid ActiveTaskId,
        Guid ActiveSegmentId,
        Guid ConfirmationId,
        Guid DeletedEventId);

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
