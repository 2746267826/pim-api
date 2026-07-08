using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class ReminderNotificationPayloadTests
{
    private static readonly Guid UserId = Guid.Parse("89898989-8989-8989-8989-898989898989");

    [Fact]
    public async Task NotificationPayloadIncludesRiskRelatedObjectDetailUrlAndActions()
    {
        await using var db = CreateDb();
        var service = new ReminderService(db, new FixedCurrentUserService(UserId));
        var relatedId = Guid.NewGuid();
        var reminder = await service.CreateAsync(new CreateReminderRequest(
            "confirmation",
            relatedId,
            "Review Outlook writeback",
            "Confirm before updating Outlook.",
            "L3 confirmation waiting",
            "L3ExternalSourceOrWriteback",
            ["WindowsToast"],
            null,
            null,
            new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero)), CancellationToken.None);

        var payload = await service.BuildNotificationPayloadAsync(reminder.Id, "WindowsToast", CancellationToken.None);

        Assert.Equal(reminder.Id, payload.ReminderId);
        Assert.Equal("Review Outlook writeback", payload.Title);
        Assert.Equal("L3ExternalSourceOrWriteback", payload.RiskLevel);
        Assert.Equal("confirmation", payload.RelatedObjectType);
        Assert.Equal(relatedId, payload.RelatedObjectId);
        Assert.Equal($"/confirmations/{relatedId}", payload.DetailUrl);
        Assert.Equal(["open", "snooze", "dismiss"], payload.Actions);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ReminderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"reminder-payload-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
