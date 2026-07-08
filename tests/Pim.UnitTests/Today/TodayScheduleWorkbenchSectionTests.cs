using Pim.Api.Today;
using Xunit;

namespace Pim.UnitTests.Today;

public class TodayScheduleWorkbenchSectionTests
{
    [Fact]
    public void TodayRegistryIncludesScheduleWorkbenchSections()
    {
        var providersSource = File.ReadAllText(RepoPath("src", "Pim.Api", "Today", "TodaySectionProviders.cs"));
        var programSource = File.ReadAllText(RepoPath("src", "Pim.Api", "Program.cs"));

        foreach (var kind in new[]
        {
            "calendar.schedule",
            "calendar.tasks",
            "calendar.habits",
            "calendar.availability",
            "calendar.ai_placeholders",
            "operations.confirmations",
            "sync.outlook",
            "reminders.queue",
            "reports.available",
            "endpoints.status",
            "pc.activity",
            "pc.quality",
            "pc.classification_suggestions"
        })
        {
            Assert.Contains(kind, providersSource);
        }

        foreach (var providerName in new[]
        {
            nameof(CalendarScheduleTodaySectionProvider),
            nameof(CalendarTasksTodaySectionProvider),
            "CalendarHabitsTodaySectionProvider",
            "CalendarAvailabilityTodaySectionProvider",
            "CalendarAiPlaceholdersTodaySectionProvider",
            "OperationsConfirmationsTodaySectionProvider",
            "OutlookSyncTodaySectionProvider",
            "RemindersQueueTodaySectionProvider",
            "ReportsAvailableTodaySectionProvider",
            "EndpointsStatusTodaySectionProvider"
        })
        {
            Assert.Contains($"ITodaySectionProvider, {providerName}", programSource);
        }
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}
