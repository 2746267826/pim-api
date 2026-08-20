using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookGraphWritebackTests
{
    private static readonly Guid UserId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    [Fact]
    public async Task ConfirmedWritebackPatchesGraphWithChangeKeyAndRecordsAudit()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var graph = new FakeMicrosoftGraphClient();
        var confirmationService = new OperationConfirmationService(db);
        var service = new OutlookSyncService(
            db,
            new StubHttpClientFactory(),
            confirmationService,
            new OutlookTokenService(db, protector),
            graph);
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = "Outlook",
            IsDefault = true
        };
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "writeback@outlook",
            OutlookEventId = "graph-1",
            OutlookChangeKey = "change-1",
            Title = "Planning block",
            Location = "Focus room",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook"
        };
        db.Set<CalendarEntity>().Add(calendar);
        db.Set<EventEntity>().Add(evt);
        db.Set<OutlookConnectionEntity>().Add(new OutlookConnectionEntity
        {
            UserId = UserId,
            ClientId = "client-id",
            AccessTokenEncrypted = Encoding.UTF8.GetBytes(protector.Protect("access-token")),
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes(protector.Protect("refresh-token")),
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Status = "connected",
            TokenHealth = "healthy"
        });
        await db.SaveChangesAsync();

        var confirmation = await service.CreateOutlookWritebackConfirmationAsync(
            UserId,
            evt,
            "write_to_outlook");
        await confirmationService.ConfirmSecondLevelAsync(confirmation.Id, UserId);

        await service.ExecuteConfirmedWriteAsync(confirmation.Id);

        Assert.Contains(graph.PatchRequests, x =>
            x.EventId == "graph-1" && x.ChangeKey == "change-1" && x.Body.Contains("location"));
        Assert.NotEmpty(await db.Set<AuditVersionEntity>().ToListAsync());
    }

    [Fact]
    public async Task ConfirmedWritebackRejectsEventOwnedByAnotherUser()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var graph = new FakeMicrosoftGraphClient();
        var confirmationService = new OperationConfirmationService(db);
        var service = new OutlookSyncService(
            db,
            new StubHttpClientFactory(),
            confirmationService,
            new OutlookTokenService(db, protector),
            graph);
        var otherUserId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var otherCalendar = new CalendarEntity
        {
            UserId = otherUserId,
            Name = "Other user calendar",
            IsDefault = true
        };
        var otherEvent = new EventEntity
        {
            Calendar = otherCalendar,
            CalendarId = otherCalendar.Id,
            Uid = "other@outlook",
            OutlookEventId = "graph-other",
            OutlookChangeKey = "change-other",
            Title = "Other user event",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook"
        };
        db.Set<CalendarEntity>().Add(otherCalendar);
        db.Set<EventEntity>().Add(otherEvent);
        db.Set<OutlookConnectionEntity>().Add(new OutlookConnectionEntity
        {
            UserId = UserId,
            ClientId = "client-id",
            AccessTokenEncrypted = Encoding.UTF8.GetBytes(protector.Protect("access-token")),
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes(protector.Protect("refresh-token")),
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Status = "connected",
            TokenHealth = "healthy"
        });
        await db.SaveChangesAsync();

        var payloadJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            provider = "outlook",
            eventId = otherEvent.Id,
            graphEventId = "graph-other",
            changeKey = "change-other",
            action = "write_to_outlook"
        });
        var confirmation = await confirmationService.CreateAsync(
            new CreateOperationConfirmationRequest(
                UserId,
                "outlook.writeback",
                "Write other user event to Outlook.",
                Pim.Core.Operations.OperationRiskLevel.L3ExternalSourceOrWriteback,
                "outlook",
                payloadJson,
                "{}",
                DateTimeOffset.UtcNow.AddHours(2),
                otherEvent.Id.ToString("N"),
                ["title"],
                ["review", "write_to_outlook", "skip"],
                "event",
                otherEvent.Id,
                true));
        await confirmationService.ConfirmSecondLevelAsync(confirmation.Id, UserId);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ExecuteConfirmedWriteAsync(confirmation.Id));
        Assert.Equal(02001, ex.ErrorCode);

        Assert.Empty(graph.PatchRequests);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-writeback-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
