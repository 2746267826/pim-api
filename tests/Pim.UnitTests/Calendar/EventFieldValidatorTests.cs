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

    private static object? GetProp(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);
}
