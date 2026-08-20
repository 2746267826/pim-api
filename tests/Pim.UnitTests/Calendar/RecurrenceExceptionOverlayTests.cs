using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class RecurrenceExceptionOverlayTests
{
    private static readonly RecurrenceService Service = new(NullLogger<RecurrenceService>.Instance);

    [Fact]
    public void Master_With_TwoExceptions_ReplacesAndMarksCancelled()
    {
        var masterId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var masterStart = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
        var masterEnd = masterStart.AddHours(1);
        var master = new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master-uid",
            Title = "Weekly sync",
            DtStart = masterStart,
            DtEnd = masterEnd,
            RRule = "FREQ=WEEKLY;COUNT=4",
            IsSeriesMaster = true,
        };

        // Expected occurrences: Jan 5, 12, 19, 26
        var secondOccurrenceId = new DateTimeOffset(2026, 1, 12, 10, 0, 0, TimeSpan.Zero).ToString("O");
        var thirdOccurrenceId = new DateTimeOffset(2026, 1, 19, 10, 0, 0, TimeSpan.Zero).ToString("O");

        var modifiedException = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "master-uid",
            Title = "Weekly sync - rescheduled",
            DtStart = new DateTimeOffset(2026, 1, 12, 11, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 12, 12, 0, 0, TimeSpan.Zero),
            IsException = true,
            SeriesMasterId = masterId,
            RecurrenceId = secondOccurrenceId,
            Status = "CONFIRMED",
        };

        var cancelledException = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "master-uid",
            Title = "Weekly sync - cancelled",
            DtStart = new DateTimeOffset(2026, 1, 19, 10, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 19, 11, 0, 0, TimeSpan.Zero),
            IsException = true,
            SeriesMasterId = masterId,
            RecurrenceId = thirdOccurrenceId,
            Status = "CANCELLED",
        };

        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var result = Service.ExpandEventsV2(new[] { master, modifiedException, cancelledException }, rangeStart, rangeEnd);

        // Should still be 4 items after overlay
        Assert.Equal(4, result.Count);

        // Order by start time
        var ordered = result.OrderBy(r => r.OccurrenceStart).ToList();

        // First occurrence unchanged (from master)
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero), ordered[0].OccurrenceStart);
        Assert.False(ordered[0].IsException);
        Assert.Equal(masterId, ordered[0].SeriesMasterId);

        // Second occurrence replaced by modified exception
        var second = ordered[1];
        Assert.Equal(modifiedException.Id, second.OccurrenceId);
        Assert.Equal(new DateTimeOffset(2026, 1, 12, 11, 0, 0, TimeSpan.Zero), second.OccurrenceStart);
        Assert.Equal(secondOccurrenceId, second.RecurrenceId);
        Assert.True(second.IsException);
        Assert.False(second.IsCancelled);
        Assert.Equal("CONFIRMED", second.Entity.Status);
        Assert.False(EventResponseMapper.MapExpanded(second).IsCancelled);

        // Third occurrence is cancelled exception (isCancelled = true)
        var third = ordered[2];
        Assert.Equal(cancelledException.Id, third.OccurrenceId);
        Assert.Equal(thirdOccurrenceId, third.RecurrenceId);
        Assert.True(third.IsException);
        Assert.True(third.IsCancelled);
        Assert.Equal("CANCELLED", third.Entity.Status);
        Assert.True(EventResponseMapper.MapExpanded(third).IsCancelled);

        // Fourth unchanged
        Assert.Equal(new DateTimeOffset(2026, 1, 26, 10, 0, 0, TimeSpan.Zero), ordered[3].OccurrenceStart);
        Assert.False(ordered[3].IsException);
        Assert.False(ordered[3].IsCancelled);
    }

    [Fact]
    public void ExceptionOutsideGeneratedRange_StillIncludedIfInWindow()
    {
        var masterId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var masterStart = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
        var master = new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "master-uid2",
            Title = "Weekly",
            DtStart = masterStart,
            DtEnd = masterStart.AddHours(1),
            RRule = "FREQ=WEEKLY;COUNT=2",
            IsSeriesMaster = true,
        };

        // Master generates Jan5, Jan12. Range only Jan5 + an extra exception Jan19 that is out of generated but in window
        var extraRecurrenceId = new DateTimeOffset(2026, 1, 19, 10, 0, 0, TimeSpan.Zero).ToString("O");
        var extraException = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "master-uid2",
            Title = "Extra",
            DtStart = new DateTimeOffset(2026, 1, 19, 10, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 19, 11, 0, 0, TimeSpan.Zero),
            IsException = true,
            SeriesMasterId = masterId,
            RecurrenceId = extraRecurrenceId,
            Status = "CONFIRMED",
        };

        var result = Service.ExpandEventsV2(new[] { master, extraException },
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero));

        // 2 generated + 1 extra outside generation but in window = 3
        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.RecurrenceId == extraRecurrenceId && r.IsException);
    }

    [Fact]
    public void ExpandEvents_LegacyWrapper_DelegatesToV2()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "legacy",
            Title = "Legacy",
            DtStart = start,
            DtEnd = start.AddHours(1),
            RRule = "FREQ=DAILY;COUNT=2",
            IsSeriesMaster = true,
        };
        var v2 = Service.ExpandEventsV2(new[] { master }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        var legacy = Service.ExpandEvents(new[] { master }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(v2.Count, legacy.Count);
        Assert.Equal(v2[0].OccurrenceStart, legacy[0].OccurrenceStart);
    }

    [Fact]
    public void RecurrenceId_IsIsoStringOfOriginalStart()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "id-check",
            Title = "Check",
            DtStart = start,
            DtEnd = start.AddHours(1),
            RRule = "FREQ=DAILY;COUNT=2",
            IsSeriesMaster = true,
        };
        var result = Service.ExpandEventsV2(new[] { master }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        foreach (var r in result)
        {
            Assert.Equal(r.OccurrenceStart.ToString("O"), r.RecurrenceId);
        }
    }

    [Fact]
    public void IsCancelled_Field_ReflectsStatusCancelled()
    {
        var masterId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var masterStart = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
        var master = new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "cancel-check",
            Title = "Check Cancelled",
            DtStart = masterStart,
            DtEnd = masterStart.AddHours(1),
            RRule = "FREQ=WEEKLY;COUNT=2",
            IsSeriesMaster = true,
        };
        var recurrenceId = new DateTimeOffset(2026, 1, 12, 10, 0, 0, TimeSpan.Zero).ToString("O");
        var cancelled = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "cancel-check",
            Title = "Cancelled occ",
            DtStart = new DateTimeOffset(2026, 1, 12, 10, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 12, 11, 0, 0, TimeSpan.Zero),
            IsException = true,
            SeriesMasterId = masterId,
            RecurrenceId = recurrenceId,
            Status = "CANCELLED",
        };
        var confirmed = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "cancel-check2",
            Title = "Confirmed",
            DtStart = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 5, 11, 0, 0, TimeSpan.Zero),
            IsException = true,
            SeriesMasterId = masterId,
            RecurrenceId = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero).ToString("O"),
            Status = "CONFIRMED",
        };
        var result = Service.ExpandEventsV2(new[] { master, cancelled, confirmed },
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var cancelledOcc = result.First(r => r.RecurrenceId == recurrenceId);
        Assert.True(cancelledOcc.IsCancelled);
        Assert.True(cancelledOcc.IsException);
        var mapped = EventResponseMapper.MapExpanded(cancelledOcc);
        Assert.True(mapped.IsCancelled);
        Assert.Equal("CANCELLED", mapped.Status);
        var confirmedOcc = result.First(r => r.OccurrenceId == confirmed.Id);
        Assert.False(confirmedOcc.IsCancelled);
        Assert.False(EventResponseMapper.MapExpanded(confirmedOcc).IsCancelled);
    }

    [Fact]
    public void DuplicateRecurrenceId_DoesNotThrow_PicksLatestUpdatedAt()
    {
        var masterId = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var masterStart = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
        var master = new EventEntity
        {
            Id = masterId,
            CalendarId = calendarId,
            Uid = "dup-check",
            Title = "Master",
            DtStart = masterStart,
            DtEnd = masterStart.AddHours(1),
            RRule = "FREQ=WEEKLY;COUNT=2",
            IsSeriesMaster = true,
        };
        var recurrenceId = new DateTimeOffset(2026, 1, 12, 10, 0, 0, TimeSpan.Zero).ToString("O");
        var older = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "dup-check",
            Title = "Older",
            DtStart = new DateTimeOffset(2026, 1, 12, 11, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 12, 12, 0, 0, TimeSpan.Zero),
            IsException = true,
            SeriesMasterId = masterId,
            RecurrenceId = recurrenceId,
            Status = "CONFIRMED",
            UpdatedAt = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
        };
        var newer = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = "dup-check",
            Title = "Newer",
            DtStart = new DateTimeOffset(2026, 1, 12, 13, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 1, 12, 14, 0, 0, TimeSpan.Zero),
            IsException = true,
            SeriesMasterId = masterId,
            RecurrenceId = recurrenceId,
            Status = "CANCELLED",
            UpdatedAt = new DateTimeOffset(2026, 1, 11, 0, 0, 0, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
        };
        // Should not throw despite duplicate RecurrenceId
        var result = Service.ExpandEventsV2(new[] { master, older, newer },
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        // Still 2 occurrences after overlay, duplicate collapsed to one
        Assert.Equal(2, result.Count);
        var dupOcc = result.First(r => r.RecurrenceId == recurrenceId);
        // Picks latest UpdatedAt => newer (CANCELLED)
        Assert.Equal(newer.Id, dupOcc.OccurrenceId);
        Assert.Equal("Newer", dupOcc.Entity.Title);
        Assert.True(dupOcc.IsCancelled);
    }
}
