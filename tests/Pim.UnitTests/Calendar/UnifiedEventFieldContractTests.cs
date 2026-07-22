using System.Linq;
using System.Reflection;
using Pim.Module.Calendar.DTOs;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class UnifiedEventFieldContractTests
{
    private static readonly string[] CommonFields =
    [
        "CalendarId", "Title", "Description", "DescriptionFormat", "Location",
        "DtStart", "DtEnd", "IsAllDay", "TimeZoneId", "ShowAs", "Importance",
        "Sensitivity", "Categories", "IsReminderOn", "ReminderMinutesBeforeStart",
        "Organizer", "Attendees", "IsOnlineMeeting", "OnlineMeetingProvider",
        "OnlineMeetingUrl", "ExternalLink", "AttachmentReferences"
    ];

    [Fact]
    public void EventWriteContracts_ExposeTheSameCommonFields()
    {
        foreach (var type in new[] { typeof(CreateEventRequest), typeof(UpdateEventRequest), typeof(OutlookEventDraft) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.All(CommonFields, name => Assert.Contains(name, names));
        }
    }

    [Fact]
    public void EventResponse_DoesNotExposeExternalMetadataJson()
    {
        var names = typeof(EventResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ExternalMetadataJson", names);
    }

    [Fact]
    public void EventResponse_ExposesOutlookAdditionalInfo()
    {
        var names = typeof(EventResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("OutlookAdditionalInfo", names);
    }
}
