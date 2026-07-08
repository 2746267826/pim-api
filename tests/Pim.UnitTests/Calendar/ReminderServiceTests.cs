using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class ReminderServiceTests
{
    private static readonly Guid UserId = Guid.Parse("78787878-7878-7878-7878-787878787878");

    [Fact]
    public async Task ReminderStoresTriggerRiskChannelsDndHistoryAndRelatedObject()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var relatedId = Guid.NewGuid();

        var reminder = await service.CreateAsync(new CreateReminderRequest(
            RelatedObjectType: "confirmation",
            RelatedObjectId: relatedId,
            Title: "Review Outlook change",
            Body: "Location changed in Outlook.",
            TriggerReason: "L3 confirmation waiting",
            RiskLevel: "L3ExternalSourceOrWriteback",
            Channels: ["Web", "WindowsToast", "AndroidNotification"],
            DoNotDisturbStart: "22:00",
            DoNotDisturbEnd: "08:00",
            ScheduledAt: new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero)), CancellationToken.None);

        Assert.Equal("Review Outlook change", reminder.Title);
        Assert.Equal(relatedId, reminder.RelatedObjectId);
        Assert.Contains("WindowsToast", reminder.Channels);
        Assert.Equal("Open", reminder.Status);
        Assert.Equal("22:00", reminder.DoNotDisturbStart);
        Assert.Equal("08:00", reminder.DoNotDisturbEnd);
    }

    [Fact]
    public async Task LowRiskActionExecutesAndHighRiskActionReturnsOpenDetail()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var lowRisk = await service.CreateAsync(Request("Low", "L1LowRiskAction"), CancellationToken.None);
        var highRisk = await service.CreateAsync(Request("High", "L3ExternalSourceOrWriteback"), CancellationToken.None);

        var lowAction = await service.HandleActionAsync(lowRisk.Id, "dismiss", CancellationToken.None);
        var highAction = await service.HandleActionAsync(highRisk.Id, "confirm", CancellationToken.None);

        Assert.Equal("Executed", lowAction.Kind);
        Assert.Equal("Dismissed", lowAction.Status);
        Assert.Equal("OpenDetailRequired", highAction.Kind);
        Assert.Contains("/confirmations/", highAction.DetailUrl);
    }

    private static CreateReminderRequest Request(string title, string risk)
        => new(
            "confirmation",
            Guid.NewGuid(),
            title,
            "Body",
            "Test trigger",
            risk,
            ["Web"],
            null,
            null,
            DateTimeOffset.UtcNow);

    private static ReminderService CreateService(PimDbContext db)
        => new(db, new FixedCurrentUserService(UserId));

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ReminderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"reminder-service-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
