using Pim.Core.Exceptions;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class EventFieldValidatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<object> AllThreeContracts =>
        new()
        {
            new CreateEventRequest(
                Guid.NewGuid(), "Test", null, null, Start, End, null,
                OnlineMeetingProvider: " ",
                OnlineMeetingUrl: " ",
                ExternalLink: " ",
                Organizer: new EventPersonDto(" ", " ")),
            new UpdateEventRequest(
                Guid.NewGuid(), "Test", null, null, Start, End, null,
                OnlineMeetingProvider: " ",
                OnlineMeetingUrl: " ",
                ExternalLink: " ",
                Organizer: new EventPersonDto(" ", " ")),
            new OutlookEventDraft(
                Guid.NewGuid(), "Test", null, null, null, Start, End,
                false, null, null, null, null, null, null, null,
                new EventPersonDto(" ", " "), null, null, " ", " ", " ", null),
        };

    [Theory]
    [MemberData(nameof(AllThreeContracts))]
    public void ValidateAndNormalize_WhitespaceOnlyFieldsBecomeNull_ForAllThreeContracts(object request)
    {
        object normalized = request switch
        {
            CreateEventRequest r => EventFieldValidator.ValidateAndNormalize(r),
            UpdateEventRequest r => EventFieldValidator.ValidateAndNormalize(r),
            OutlookEventDraft r => EventFieldValidator.ValidateAndNormalize(r),
            _ => throw new InvalidOperationException()
        };

        Assert.Null(GetProp(normalized, "OnlineMeetingProvider"));
        Assert.Null(GetProp(normalized, "OnlineMeetingUrl"));
        Assert.Null(GetProp(normalized, "ExternalLink"));
        Assert.Null(GetProp(normalized, "Organizer"));
    }

    [Fact]
    public void CreateAllDayRequiresIndependentDateRange()
    {
        var request = new CreateEventRequest(
            Guid.NewGuid(), "All day", null, null, Start, End, null,
            IsAllDay: true);

        var exception = Assert.Throws<DomainException>(() => EventFieldValidator.ValidateAndNormalize(request));
        Assert.Equal(02009, exception.ErrorCode);
    }

    [Fact]
    public void TimeZoneSourceValueIsNotTrimmedOrInvented()
    {
        var source = " Eastern Standard Time ";
        var request = new CreateEventRequest(
            Guid.NewGuid(), "Timed", null, null, Start, End, null,
            TimeZoneId: source);

        var normalized = EventFieldValidator.ValidateAndNormalize(request);

        Assert.Equal(source, normalized.TimeZoneId);
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("not-a-timezone")]
    public void UnknownTimeZoneId_IsRejectedInsteadOfBeingStoredAsSource(string value)
    {
        var request = new CreateEventRequest(
            Guid.NewGuid(), "Timed", null, null, Start, End, null,
            TimeZoneId: value);

        var exception = Assert.Throws<DomainException>(() =>
            EventFieldValidator.ValidateAndNormalize(request));

        Assert.Equal(02009, exception.ErrorCode);
        Assert.Contains("无法解析为有效时区", exception.Message);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Asia/Shanghai")]
    [InlineData("America/New_York")]
    [InlineData("Eastern Standard Time")]
    public void ResolvableIanaOrWindowsTimeZoneId_IsAccepted(string value)
    {
        var request = new CreateEventRequest(
            Guid.NewGuid(), "Timed", null, null, Start, End, null,
            TimeZoneId: value);

        var normalized = EventFieldValidator.ValidateAndNormalize(request);

        Assert.Equal(value, normalized.TimeZoneId);
    }

    [Fact]
    public void BlankTimeZoneId_IsTreatedAsUnknownAndOmitted()
    {
        var request = new CreateEventRequest(
            Guid.NewGuid(), "Timed", null, null, Start, End, null,
            TimeZoneId: "   ");

        var normalized = EventFieldValidator.ValidateAndNormalize(request);

        Assert.Null(normalized.TimeZoneId);
    }

    [Fact]
    public void OutlookDraftRejectsAllDayFieldsForTimedEvent()
    {
        var draft = new OutlookEventDraft(
            Guid.NewGuid(), "Timed", null, null, null, Start, End,
            false, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            AllDayStartDate: new DateOnly(2026, 7, 22),
            AllDayEndDateExclusive: new DateOnly(2026, 7, 23));

        var exception = Assert.Throws<DomainException>(() =>
            EventFieldValidator.ValidateAndNormalize(draft));

        Assert.Equal(02009, exception.ErrorCode);
    }

    [Fact]
    public void OutlookDraftAllDayRangeIsSymmetricAndOrdered()
    {
        var missingEnd = new OutlookEventDraft(
            Guid.NewGuid(), "All day", null, null, null, Start, End,
            true, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            AllDayStartDate: new DateOnly(2026, 7, 22));
        var reversed = missingEnd with
        {
            AllDayStartDate = new DateOnly(2026, 7, 23),
            AllDayEndDateExclusive = new DateOnly(2026, 7, 23),
        };

        Assert.Equal(02009, Assert.Throws<DomainException>(() =>
            EventFieldValidator.ValidateAndNormalize(missingEnd)).ErrorCode);
        Assert.Equal(02009, Assert.Throws<DomainException>(() =>
            EventFieldValidator.ValidateAndNormalize(reversed)).ErrorCode);

        var valid = missingEnd with { AllDayEndDateExclusive = new DateOnly(2026, 7, 23) };
        var normalized = EventFieldValidator.ValidateAndNormalize(valid);
        Assert.Equal(new DateOnly(2026, 7, 22), normalized.AllDayStartDate);
        Assert.Equal(new DateOnly(2026, 7, 23), normalized.AllDayEndDateExclusive);
    }

    private static object? GetProp(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);
}
