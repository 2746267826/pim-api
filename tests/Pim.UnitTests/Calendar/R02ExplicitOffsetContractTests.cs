using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

/// <summary>
/// R02：JSON 写入和查询窗口都不能把“没有偏移的墙钟”交给服务器本地时区解释。
/// 写入必须显式给出绝对时刻；查询允许日期/无偏移值，但按契约固定解释为 UTC。
/// </summary>
public sealed class R02ExplicitOffsetContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid CalendarId = Guid.Parse("92929292-9292-9292-9292-929292929292");

    [Theory]
    [InlineData("2026-09-01T09:00:00Z", "2026-09-01T09:00:00Z")]
    [InlineData("2026-09-01T17:00:00+08:00", "2026-09-01T09:00:00Z")]
    [InlineData("2026-09-01T04:00:00-05:00", "2026-09-01T09:00:00Z")]
    [InlineData("2026-09-01T09:00:00.1234567Z", "2026-09-01T09:00:00.1234567Z")]
    public void CreateEventJson_WithExplicitOffset_PreservesTheAbsoluteInstant(
        string start,
        string expectedUtc)
    {
        var request = DeserializeCreate(start);

        Assert.Equal(DateTimeOffset.Parse(expectedUtc).UtcDateTime, request.DtStart.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, request.DtStart.Offset);
    }

    [Theory]
    [InlineData(typeof(CreateEventRequest))]
    [InlineData(typeof(UpdateEventRequest))]
    [InlineData(typeof(OutlookEventDraft))]
    public void AllEventWriteContracts_RejectDateOnlyAndOffsetlessTimes(Type contractType)
    {
        foreach (var start in new[]
                 {
                     "2026-09-01",
                     "2026-09-01T09:00:00",
                     "2026-09-01 09:00:00",
                 })
        {
            var json = $$"""
                {
                  "calendarId": "{{CalendarId}}",
                  "title": "No offset",
                  "dtStart": "{{start}}",
                  "dtEnd": "2026-09-01T10:00:00Z"
                }
                """;

            var exception = Record.Exception(() => JsonSerializer.Deserialize(json, contractType, WebJson));

            Assert.IsType<JsonException>(exception);
        }
    }

    [Fact]
    public void CreateEventJson_MalformedString_IsAJsonError()
    {
        var exception = Record.Exception(() => DeserializeCreate("not-a-time"));

        Assert.IsType<JsonException>(exception);
    }

    [Theory]
    [InlineData("2026-09-01", "2026-09-02", "2026-09-01T00:00:00Z", "2026-09-02T00:00:00Z")]
    [InlineData("2026-09-01T08:00:00", "2026-09-01T10:00:00", "2026-09-01T08:00:00Z", "2026-09-01T10:00:00Z")]
    [InlineData("2026-09-01T16:00:00+08:00", "2026-09-01T18:00:00+08:00", "2026-09-01T08:00:00Z", "2026-09-01T10:00:00Z")]
    public void QueryRange_DateAndOffsetlessValuesAreUtc_ExplicitOffsetsAreAbsolute(
        string start,
        string end,
        string expectedStart,
        string expectedEnd)
    {
        var (parsedStart, parsedEnd) = EventQueryContract.ParseQueryRange(start, end);

        Assert.Equal(DateTimeOffset.Parse(expectedStart), parsedStart);
        Assert.Equal(DateTimeOffset.Parse(expectedEnd), parsedEnd);
        Assert.Equal(TimeSpan.Zero, parsedStart.Offset);
        Assert.Equal(TimeSpan.Zero, parsedEnd.Offset);
    }

    [Fact]
    public void QueryRange_AllowsExactly366DaysAndRejectsTheNextSecond()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var exact = EventQueryContract.ParseQueryRange(
            start.ToString("O"),
            start.AddDays(EventQueryContract.MaxRangeDays).ToString("O"));
        Assert.Equal(start, exact.Start);
        Assert.Equal(start.AddDays(EventQueryContract.MaxRangeDays), exact.End);

        Assert.Equal(
            02009,
            Assert.Throws<DomainException>(() => EventQueryContract.ParseQueryRange(
                start.ToString("O"),
                start.AddDays(EventQueryContract.MaxRangeDays).AddSeconds(1).ToString("O"))).ErrorCode);
    }

    [Theory]
    [InlineData(null, "2026-09-02")]
    [InlineData("2026-09-01", null)]
    [InlineData("", "2026-09-02")]
    [InlineData("2026-09-01", "not-a-time")]
    [InlineData("2026-02-31", "2026-09-02")]
    public void QueryRange_MissingOrMalformedValuesReturnBusiness400(string? start, string? end)
    {
        Assert.Equal(
            02009,
            Assert.Throws<DomainException>(() => EventQueryContract.ParseQueryRange(start, end)).ErrorCode);
    }

    [Fact]
    public void QueryRange_ReversedRangeReturnsBusiness400()
    {
        Assert.Equal(
            02009,
            Assert.Throws<DomainException>(() => EventQueryContract.ParseQueryRange(
                "2026-09-02T00:00:00Z",
                "2026-09-01T00:00:00Z")).ErrorCode);
    }

    private static CreateEventRequest DeserializeCreate(string? start)
    {
        var json = $$"""
            {
              "calendarId": "{{CalendarId}}",
              "title": "Offset contract",
              "dtStart": {{JsonSerializer.Serialize(start)}},
              "dtEnd": "2026-09-01T10:00:00Z"
            }
            """;
        return JsonSerializer.Deserialize<CreateEventRequest>(json, WebJson)
            ?? throw new InvalidOperationException("CreateEventRequest did not deserialize");
    }
}
