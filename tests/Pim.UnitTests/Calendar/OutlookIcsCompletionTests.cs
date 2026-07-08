using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookIcsCompletionTests
{
    private static readonly Guid UserId = Guid.Parse("56565656-5656-5656-5656-565656565656");

    [Fact]
    public void ExportSelectedObjectsIncludesUid()
    {
        var evt = new EventEntity
        {
            Uid = "selected@example.com",
            Title = "Selected",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero)
        };

        var content = new IcsService().ExportEvents([evt]);

        Assert.Contains("UID:selected@example.com", content);
        Assert.Contains("SUMMARY:Selected", content);
    }

    [Fact]
    public async Task ImportPreviewAggregatesDuplicateReason()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().Add(new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "duplicate@example.com",
            SourceUid = "duplicate@example.com",
            Title = "Duplicate",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero)
        });
        await db.SaveChangesAsync();
        var service = CreateCalendarService(db);
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:duplicate@example.com
        SUMMARY:Duplicate
        DTSTART:20260708T090000Z
        DTEND:20260708T100000Z
        END:VEVENT
        END:VCALENDAR
        """;

        var importPreview = await service.ImportOutlookIcsAsync(
            ics,
            calendar.Id,
            new OutlookIcsService(),
            CancellationToken.None);

        Assert.Equal(1, importPreview.SkippedReasons["duplicate"]);
        Assert.Contains(importPreview.Samples, x => x.Reason.StartsWith("duplicate", StringComparison.Ordinal));
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-ics-completion-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarService CreateCalendarService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId), new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
