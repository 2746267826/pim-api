using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookSourceGovernanceTests
{
    private static readonly Guid UserId = Guid.Parse("34343434-3434-3434-3434-343434343434");

    [Fact]
    public async Task OutlookOnlyCalendarLayersExcludeManualSources()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().AddRange(
            Event(calendar, "manual-1", "Manual", "manual"),
            Event(calendar, "outlook-1", "Outlook", "outlook"));
        await db.SaveChangesAsync();
        var service = new PlanningModelService(db, new FixedCurrentUserService(UserId));

        var outlookOnly = await service.GetCalendarLayersAsync(new CalendarLayerQuery(
            new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero),
            ["events"],
            OutlookOnly: true));

        Assert.Contains(outlookOnly.Items, x => x.Source == "outlook");
        Assert.DoesNotContain(outlookOnly.Items, x => x.Source == "manual");
    }

    [Fact]
    public async Task StopSyncPreviewUsesL4Risk()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        var evt = Event(calendar, "outlook-1", "Outlook", "outlook");
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().Add(evt);
        await db.SaveChangesAsync();
        var service = new OutlookConflictService(
            db,
            new FixedCurrentUserService(UserId),
            new OperationConfirmationService(db));

        var stopSyncPreview = await service.RequestStopSyncPreviewAsync(evt.Id, CancellationToken.None);

        Assert.Equal(OperationRiskLevel.L4BatchOrDestructiveGovernance, stopSyncPreview.RiskLevel);
        Assert.True(stopSyncPreview.RequiresStrictConfirmation);
    }

    [Fact]
    public async Task DataCenterOutlookSourceIncludesGraphIds()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().Add(Event(calendar, "outlook-1", "Outlook", "outlook"));
        await db.SaveChangesAsync();
        var service = new DataCenterQueryService(db, new FixedCurrentUserService(UserId));

        var result = await service.QueryAsync(new DataCenterQueryRequest(
            Search: null,
            ObjectType: "event",
            Source: "outlook",
            PendingOnly: false));

        var item = Assert.Single(result.Items);
        Assert.Contains("GraphEventId=outlook-1", item.Summary);
    }

    private static EventEntity Event(CalendarEntity calendar, string outlookId, string title, string source)
        => new()
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = outlookId + "@pim",
            OutlookEventId = source == "outlook" ? outlookId : null,
            OutlookChangeKey = source == "outlook" ? "change-key" : null,
            Title = title,
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
            Source = source
        };

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-source-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
