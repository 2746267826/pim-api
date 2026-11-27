using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Core.Exceptions;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

/// <summary>
/// R02：事件时区可判定。测试只锁定已确认口径：
/// dtStart/dtEnd 是绝对时刻，IANA 规范化与未知回退分层返回；全天日期独立于绝对时刻；
/// 重复发生项使用完整半开区间相交，并按事件时区墙钟跨 DST；窗口内结果不再被 500 项截断。
/// </summary>
public class R02EventTimezoneTests
{
    private static readonly Guid UserId = Guid.Parse("92929292-9292-9292-9292-929292929292");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void EventResponse_ResolvedWindowsZone_ExposesIanaAndLocalWallClock()
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "resolved@example.com",
            Title = "Resolved",
            DtStart = new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero),
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "China Standard Time"
        };

        var json = JsonDocument.Parse(JsonSerializer.Serialize(EventResponseMapper.Map(entity), WebJson)).RootElement;

        Assert.Equal("Asia/Shanghai", json.GetProperty("timeZoneId").GetString());
        Assert.Equal("China Standard Time", json.GetProperty("sourceTimeZoneId").GetString());
        Assert.Equal("iana", json.GetProperty("timeZoneIdType").GetString());
        Assert.Equal("windows", json.GetProperty("timeZoneSourceType").GetString());
        Assert.Equal("resolved", json.GetProperty("timeZoneResolution").GetString());
        Assert.Equal("2026-10-19T10:00:00", json.GetProperty("localStart").GetString());
        Assert.Equal("2026-10-19T11:00:00", json.GetProperty("localEnd").GetString());
        Assert.Equal("Asia/Shanghai", json.GetProperty("displayTimeZoneId").GetString());
        Assert.Equal("2026-10-19T10:00:00", json.GetProperty("displayLocalStart").GetString());
        Assert.Equal("2026-10-19T11:00:00", json.GetProperty("displayLocalEnd").GetString());
        Assert.Null(json.GetProperty("allDayStartDate").GetString());
    }

    [Fact]
    public void EventResponse_SourceOnly_FallsBackToAccountZoneWithoutInventingIana()
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "source-only@example.com",
            Title = "Source only",
            DtStart = new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero),
            SourceTimeZoneId = "Outlook Custom Zone"
        };

        var json = JsonDocument.Parse(JsonSerializer.Serialize(EventResponseMapper.Map(entity), WebJson)).RootElement;

        Assert.Null(json.GetProperty("timeZoneId").GetString());
        Assert.Equal("Outlook Custom Zone", json.GetProperty("sourceTimeZoneId").GetString());
        Assert.Equal("sourceOnly", json.GetProperty("timeZoneResolution").GetString());
        Assert.Equal("unknown", json.GetProperty("timeZoneSourceType").GetString());
        Assert.Null(json.GetProperty("localStart").GetString());
        Assert.Equal("Asia/Shanghai", json.GetProperty("displayTimeZoneId").GetString());
        Assert.Equal("2026-10-19T10:00:00", json.GetProperty("displayLocalStart").GetString());
        Assert.Equal("2026-10-19T11:00:00", json.GetProperty("displayLocalEnd").GetString());
    }

    [Fact]
    public void EventResponse_Unknown_DoesNotGuessFromAbsoluteOffset()
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "unknown@example.com",
            Title = "Unknown",
            DtStart = new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero)
        };

        var json = JsonDocument.Parse(JsonSerializer.Serialize(EventResponseMapper.Map(entity), WebJson)).RootElement;

        Assert.Null(json.GetProperty("timeZoneId").GetString());
        Assert.Null(json.GetProperty("sourceTimeZoneId").GetString());
        Assert.Equal("unknown", json.GetProperty("timeZoneResolution").GetString());
        Assert.Equal("unknown", json.GetProperty("timeZoneSourceType").GetString());
        Assert.Null(json.GetProperty("localStart").GetString());
        Assert.Equal("Asia/Shanghai", json.GetProperty("displayTimeZoneId").GetString());
        Assert.Equal("2026-10-19T10:00:00", json.GetProperty("displayLocalStart").GetString());
        Assert.Equal("2026-10-19T02:00:00+00:00", json.GetProperty("dtStart").GetString());
    }

    [Fact]
    public void EventQueryContract_AllowsExactly366DaysAndRejectsLongerOrReversedRanges()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var exact = EventQueryContract.Validate(start, start.AddDays(366));
        Assert.Equal(start, exact.Start);
        Assert.Equal(start.AddDays(366), exact.End);

        Assert.Equal(02009, Assert.Throws<DomainException>(
            () => EventQueryContract.Validate(start, start.AddDays(366).AddSeconds(1))).ErrorCode);
        Assert.Equal(02009, Assert.Throws<DomainException>(
            () => EventQueryContract.Validate(start.AddSeconds(1), start)).ErrorCode);
    }

    [Fact]
    public void EventResponse_AllDay_ReturnsStoredDatesInsteadOfUtcDate()
    {
        var entity = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            Uid = "all-day@example.com",
            Title = "All day",
            DtStart = new DateTimeOffset(2026, 7, 11, 16, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 12, 16, 0, 0, TimeSpan.Zero),
            IsAllDay = true,
            AllDayStartDate = new DateOnly(2026, 7, 12),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 13),
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "China Standard Time"
        };

        var json = JsonDocument.Parse(JsonSerializer.Serialize(EventResponseMapper.Map(entity), WebJson)).RootElement;

        Assert.Equal("2026-07-12", json.GetProperty("allDayStartDate").GetString());
        Assert.Equal("2026-07-13", json.GetProperty("allDayEndDateExclusive").GetString());
        Assert.Null(json.GetProperty("localStart").GetString());
        Assert.Equal("2026-07-11T16:00:00+00:00", json.GetProperty("dtStart").GetString());
    }

    [Fact]
    public async Task PagedQuery_AllDayEvent_UsesIndependentDatesForWindowMembership()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Add(calendar);
        db.Add(new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "all-day-window@example.com",
            Title = "All day window",
            DtStart = new DateTimeOffset(2026, 7, 11, 16, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 12, 16, 0, 0, TimeSpan.Zero),
            IsAllDay = true,
            AllDayStartDate = new DateOnly(2026, 7, 12),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 13),
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "Asia/Shanghai"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var july12 = await service.GetEventsPagedAsync(
            null, null,
            new DateTimeOffset(2026, 7, 11, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 12, 16, 0, 0, TimeSpan.Zero),
            1, 100);
        var july11 = await service.GetEventsPagedAsync(
            null, null,
            new DateTimeOffset(2026, 7, 10, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 11, 16, 0, 0, TimeSpan.Zero),
            1, 100);

        Assert.Single(july12.Items);
        Assert.Empty(july11.Items);
        var item = JsonDocument.Parse(JsonSerializer.Serialize(july12.Items[0], WebJson)).RootElement;
        Assert.Equal("2026-07-12", item.GetProperty("allDayStartDate").GetString());
    }

    [Fact]
    public async Task PagedQuery_AllDayEventInUtcPlusFourteen_UsesEventLocalDatesForWindowMembership()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Add(calendar);
        db.Add(new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "all-day-utc-plus-fourteen@example.com",
            Title = "All day across the Beijing boundary",
            DtStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            IsAllDay = true,
            AllDayStartDate = new DateOnly(2026, 7, 13),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 14),
            TimeZoneId = "Pacific/Kiritimati",
            SourceTimeZoneId = "Pacific/Kiritimati"
        });
        await db.SaveChangesAsync();

        var results = await CreateService(db).GetEventsPagedAsync(
            null,
            null,
            new DateTimeOffset(2026, 7, 11, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 12, 16, 0, 0, TimeSpan.Zero),
            1,
            100);

        var item = Assert.Single(results.Items);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero), item.DtStart);
        Assert.Equal("2026-07-13", item.AllDayStartDate?.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task CalendarLayers_AllDayEventUsesIndependentDatesForWindowMembership()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Add(calendar);
        db.Add(new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "all-day-layers@example.com",
            Title = "All day layer",
            DtStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            IsAllDay = true,
            AllDayStartDate = new DateOnly(2026, 7, 13),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 14),
            TimeZoneId = "Pacific/Kiritimati",
            SourceTimeZoneId = "Pacific/Kiritimati"
        });
        await db.SaveChangesAsync();

        var planning = new PlanningModelService(
            db,
            new FixedCurrentUserService(UserId),
            null,
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));
        var layers = await planning.GetCalendarLayersAsync(new CalendarLayerQuery(
            new DateTimeOffset(2026, 7, 11, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 12, 16, 0, 0, TimeSpan.Zero),
            ["events"]));

        var item = Assert.Single(layers.Items.Where(x => x.Layer == "events"));
        Assert.Equal("All day layer", item.Title);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.Zero), item.StartsAt);
    }

    [Fact]
    public async Task LegacyAllDayEventWithoutIndependentDates_IsReturnedWithoutInferringDates()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Add(calendar);
        db.Add(new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "legacy-all-day-without-dates@example.com",
            Title = "Legacy all day",
            DtStart = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            IsAllDay = true,
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "China Standard Time"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var start = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);
        var paged = await service.GetEventsPagedAsync(null, null, start, end, 1, 20);
        var list = await service.GetEventsAsync(start, end, CancellationToken.None);

        var pagedItem = Assert.Single(paged.Items);
        var listItem = Assert.Single(list);
        Assert.True(pagedItem.IsAllDay);
        Assert.Null(pagedItem.AllDayStartDate);
        Assert.Null(pagedItem.AllDayEndDateExclusive);
        Assert.Null(listItem.AllDayStartDate);
        Assert.Null(listItem.AllDayEndDateExclusive);
    }

    [Fact]
    public async Task UpdatingLegacyAllDayMetadata_PreservesAbsoluteBoundsWithoutInventingDates()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        var start = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        var entity = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "legacy-all-day-update@example.com",
            Title = "Before",
            DtStart = start,
            DtEnd = end,
            IsAllDay = true,
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "China Standard Time"
        };
        db.Add(calendar);
        db.Add(entity);
        await db.SaveChangesAsync();

        var updated = await CreateService(db).UpdateEventAsync(
            entity.Id,
            new UpdateEventRequest(
                calendar.Id, "After", null, null, start, end, null,
                IsAllDay: true),
            CancellationToken.None);

        Assert.Equal("After", updated.Title);
        Assert.Equal(start, updated.DtStart);
        Assert.Equal(end, updated.DtEnd);
        Assert.Null(updated.AllDayStartDate);
        Assert.Null(updated.AllDayEndDateExclusive);
        Assert.Equal("Asia/Shanghai", updated.TimeZoneId);
        Assert.Equal("China Standard Time", updated.SourceTimeZoneId);
    }

    [Fact]
    public async Task UpdatingAllDayTitlePreservesAbsoluteBoundsAndSourceTimezone()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        var start = new DateTimeOffset(2026, 7, 12, 4, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 13, 4, 0, 0, TimeSpan.Zero);
        var entity = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "all-day-preserve@example.com",
            Title = "Before",
            DtStart = start,
            DtEnd = end,
            IsAllDay = true,
            AllDayStartDate = new DateOnly(2026, 7, 12),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 13),
            TimeZoneId = "Eastern Standard Time",
            SourceTimeZoneId = "Eastern Standard Time"
        };
        db.Add(calendar);
        db.Add(entity);
        await db.SaveChangesAsync();

        var updated = await CreateService(db).UpdateEventAsync(
            entity.Id,
            new UpdateEventRequest(
                calendar.Id, "After", null, null, start, end, null,
                IsAllDay: true),
            CancellationToken.None);

        Assert.Equal(start, updated.DtStart);
        Assert.Equal(end, updated.DtEnd);
        Assert.Equal(new DateOnly(2026, 7, 12), updated.AllDayStartDate);
        Assert.Equal(new DateOnly(2026, 7, 13), updated.AllDayEndDateExclusive);
        Assert.Equal("America/New_York", updated.TimeZoneId);
        Assert.Equal("Eastern Standard Time", updated.SourceTimeZoneId);
    }

    [Fact]
    public async Task ChangingAllDayTimezoneKeepsDatesAndRecomputesAbsoluteDayBounds()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        var start = new DateTimeOffset(2026, 7, 12, 4, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 13, 4, 0, 0, TimeSpan.Zero);
        var entity = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "all-day-zone-change@example.com",
            Title = "All day",
            DtStart = start,
            DtEnd = end,
            IsAllDay = true,
            AllDayStartDate = new DateOnly(2026, 7, 12),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 13),
            TimeZoneId = "America/New_York",
            SourceTimeZoneId = "Eastern Standard Time"
        };
        db.Add(calendar);
        db.Add(entity);
        await db.SaveChangesAsync();

        var updated = await CreateService(db).UpdateEventAsync(
            entity.Id,
            new UpdateEventRequest(
                calendar.Id, "All day", null, null, start, end, null,
                IsAllDay: true,
                TimeZoneId: "Asia/Tokyo"),
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 7, 12), updated.AllDayStartDate);
        Assert.Equal(new DateOnly(2026, 7, 13), updated.AllDayEndDateExclusive);
        Assert.Equal(new DateTimeOffset(2026, 7, 11, 15, 0, 0, TimeSpan.Zero), updated.DtStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero), updated.DtEnd);
        Assert.Equal("Asia/Tokyo", updated.TimeZoneId);
        Assert.Equal("Asia/Tokyo", updated.SourceTimeZoneId);
    }

    [Fact]
    public async Task UpdatingTimeZone_OmittedPreservesAndExplicitNullClearsToUnknown()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        var start = new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 12, 2, 0, 0, TimeSpan.Zero);
        var entity = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "timezone-null@example.com",
            Title = "Before",
            DtStart = start,
            DtEnd = end,
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "China Standard Time"
        };
        db.Add(calendar);
        db.Add(entity);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var preserved = await service.UpdateEventAsync(
            entity.Id,
            new UpdateEventRequest(calendar.Id, "Preserve", null, null, start, end, null),
            CancellationToken.None);

        Assert.Equal("Asia/Shanghai", preserved.TimeZoneId);
        Assert.Equal("China Standard Time", preserved.SourceTimeZoneId);
        Assert.Equal("resolved", preserved.TimeZoneResolution);

        var cleared = await service.UpdateEventAsync(
            entity.Id,
            new UpdateEventRequest(calendar.Id, "Clear", null, null, start, end, null)
            {
                TimeZoneIdProvided = true
            },
            CancellationToken.None);

        Assert.Null(cleared.TimeZoneId);
        Assert.Null(cleared.SourceTimeZoneId);
        Assert.Equal("unknown", cleared.TimeZoneResolution);
        Assert.Null(cleared.LocalStart);
    }

    [Fact]
    public void Recurrence_OccurrenceStartedBeforeWindowButOverlapping_IsReturned()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = Recurring("FREQ=DAILY", start, start.AddHours(2));

        var result = new RecurrenceService(NullLogger<RecurrenceService>.Instance)
            .ExpandEventsV2(
                new[] { master },
                new DateTimeOffset(2026, 1, 3, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 3, 11, 0, 0, TimeSpan.Zero));

        var occurrence = Assert.Single(result);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero), occurrence.OccurrenceStart);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 11, 0, 0, TimeSpan.Zero), occurrence.OccurrenceEnd);
    }

    [Fact]
    public void Recurrence_OccurrenceStartingAtWindowEnd_IsExcluded()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var master = Recurring("FREQ=DAILY", start, start.AddHours(1));

        var result = new RecurrenceService(NullLogger<RecurrenceService>.Instance)
            .ExpandEventsV2(
                new[] { master },
                new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero));

        Assert.Empty(result);
    }

    [Fact]
    public void Recurrence_NewYorkDaily_KeepsLocalNineAcrossDst()
    {
        var firstLocal = new DateTime(2026, 3, 1, 9, 0, 0);
        var firstUtc = TimeZoneInfo.ConvertTimeToUtc(firstLocal, NewYork());
        var master = Recurring(
            "FREQ=DAILY;COUNT=10",
            new DateTimeOffset(firstUtc, TimeSpan.Zero),
            new DateTimeOffset(firstUtc.AddHours(1), TimeSpan.Zero));
        master.TimeZoneId = "America/New_York";
        master.SourceTimeZoneId = "Eastern Standard Time";

        var result = new RecurrenceService(NullLogger<RecurrenceService>.Instance)
            .ExpandEventsV2(
                new[] { master },
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(10, result.Count);
        Assert.All(result, occurrence =>
        {
            var local = TimeZoneInfo.ConvertTime(occurrence.OccurrenceStart, NewYork());
            Assert.Equal(9, local.Hour);
            Assert.Equal(0, local.Minute);
        });
        Assert.Equal(firstUtc.AddDays(7).AddHours(-1), result[7].OccurrenceStart.UtcDateTime);
        Assert.Equal(firstUtc.AddDays(9).AddHours(-1), result[9].OccurrenceStart.UtcDateTime);
    }

    [Fact]
    public async Task PagedQuery_MoreThanFiveHundredOccurrences_IsComplete()
    {
        await using var db = CreateDb();
        var calendar = new CalendarEntity { UserId = UserId, Name = "Default", IsDefault = true };
        db.Add(calendar);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.Add(new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "hourly@example.com",
            Title = "Hourly",
            DtStart = start,
            DtEnd = start.AddMinutes(30),
            RRule = "FREQ=HOURLY;COUNT=620",
            IsSeriesMaster = true,
            TimeZoneId = "UTC",
            SourceTimeZoneId = "UTC"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var first = await service.GetEventsPagedAsync(
            null, null, start, start.AddDays(30), 1, 100);
        var last = await service.GetEventsPagedAsync(
            null, null, start, start.AddDays(30), 7, 100);

        Assert.Equal(620, first.TotalCount);
        Assert.Equal(7, first.TotalPages);
        Assert.Equal(100, first.Items.Count);
        Assert.Equal(20, last.Items.Count);
    }

    [Fact]
    public void InfiniteRecurrence_FiniteWindowReturnsEveryOccurrencePastFiveHundred()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var master = Recurring("FREQ=HOURLY", start, start.AddMinutes(30));

        var result = new RecurrenceService(NullLogger<RecurrenceService>.Instance)
            .ExpandEventsV2(new[] { master }, start, start.AddDays(30));

        Assert.Equal(720, result.Count);
        Assert.Equal(start, result[0].OccurrenceStart);
        Assert.Equal(start.AddHours(719), result[^1].OccurrenceStart);
    }

    [Fact]
    public void InfiniteRecurrence_366DayHourlyWindowStaysWithinExpansionBudget()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var master = Recurring("FREQ=HOURLY", start, start.AddMinutes(30));

        var result = new RecurrenceService(NullLogger<RecurrenceService>.Instance)
            .ExpandEventsV2(new[] { master }, start, start.AddDays(366));

        Assert.Equal(366 * 24, result.Count);
    }

    [Fact]
    public void Recurrence_SecondLevelSeriesExceedingBudgetReturnsBusinessError()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var master = Recurring("FREQ=SECONDLY", start, start.AddMinutes(1));

        var exception = Assert.Throws<DomainException>(() =>
            new RecurrenceService(NullLogger<RecurrenceService>.Instance)
                .ExpandEventsV2(new[] { master }, start, start.AddDays(1)));

        Assert.Equal(02009, exception.ErrorCode);
        Assert.Contains("安全预算", exception.Message);
    }

    [Fact]
    public void Recurrence_AllDayOccurrenceDatesSurviveExceptionOverlay()
    {
        var shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var firstUtc = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 7, 12), shanghai),
            TimeSpan.Zero);
        var master = Recurring("FREQ=DAILY;COUNT=3", firstUtc, firstUtc.AddDays(1));
        master.IsAllDay = true;
        master.AllDayStartDate = new DateOnly(2026, 7, 12);
        master.AllDayEndDateExclusive = new DateOnly(2026, 7, 13);
        master.TimeZoneId = "Asia/Shanghai";
        master.SourceTimeZoneId = "China Standard Time";
        var exception = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = master.CalendarId,
            Uid = "all-day-exception@example.com",
            Title = "Moved later",
            DtStart = firstUtc.AddDays(8),
            DtEnd = firstUtc.AddDays(9),
            IsAllDay = true,
            AllDayStartDate = new DateOnly(2026, 7, 20),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 21),
            TimeZoneId = "Asia/Shanghai",
            SourceTimeZoneId = "China Standard Time",
            IsException = true,
            SeriesMasterId = master.Id,
            RecurrenceId = firstUtc.AddDays(2).ToString("O"),
        };

        var occurrence = Assert.Single(
            new RecurrenceService(NullLogger<RecurrenceService>.Instance)
                .ExpandEventsV2(
                    new[] { master, exception },
                    firstUtc.AddDays(1),
                    firstUtc.AddDays(2)));

        Assert.Equal(new DateOnly(2026, 7, 13), occurrence.AllDayStartDate);
        Assert.Equal(new DateOnly(2026, 7, 14), occurrence.AllDayEndDateExclusive);
        Assert.Equal(firstUtc.AddDays(1), occurrence.OccurrenceStart);
        Assert.NotEqual(master.Id, occurrence.OccurrenceId);
    }

    private static EventEntity Recurring(string rrule, DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.NewGuid(),
        CalendarId = Guid.NewGuid(),
        Uid = $"recurring-{Guid.NewGuid():N}",
        Title = "Recurring",
        DtStart = start,
        DtEnd = end,
        RRule = rrule,
        IsSeriesMaster = true
    };

    private static TimeZoneInfo NewYork()
        => TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"r02-timezone-{Guid.NewGuid():N}")
            .Options;
        return new PimDbContext(options);
    }

    private static CalendarService CreateService(PimDbContext db)
        => new(
            db,
            new FixedCurrentUserService(UserId),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance));

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
