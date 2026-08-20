using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookGraphDeltaSyncTests
{
    private static readonly Guid UserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task DeltaSyncFollowsNextLinkAndStoresDeltaLink()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        SeedCalendarAndConnection(db, protector);
        var graph = new FakeMicrosoftGraphClient();
        graph.DeltaPages.Enqueue(new GraphDeltaPage(
            [
                GraphEventFactory.Create("graph-1", "First"),
            ],
            "https://graph.microsoft.com/v1.0/me/calendarView/delta?$skiptoken=next",
            null));
        graph.DeltaPages.Enqueue(new GraphDeltaPage(
            [
                GraphEventFactory.Create("graph-2", "Second"),
            ],
            null,
            "https://graph.microsoft.com/v1.0/me/calendarView/delta?$deltatoken=done"));
        var service = CreateService(db, graph, protector);

        var batch = await service.SyncAsync(UserId, CancellationToken.None);

        Assert.Equal(2, batch.ReadCount);
        Assert.Contains(batch.Steps, x => x.Name == "Follow nextLink");
        Assert.Contains(batch.Steps, x => x.Name == "Store deltaLink");
        Assert.DoesNotContain(batch.Steps, x => (x.Detail ?? "").Contains("deltatoken=done"));
        Assert.DoesNotContain(batch.Steps, x => (x.Detail ?? "").Contains("skiptoken=next"));
        var connection = await db.Set<OutlookConnectionEntity>().SingleAsync(c => c.UserId == UserId);
        Assert.Contains("$deltatoken=done", connection.DeltaLink);
    }

    [Fact]
    public async Task OutlookCoreDiffCreatesL3ConfirmationBeforeLocalMutation()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var calendar = SeedCalendarAndConnection(db, protector);
        db.Set<EventEntity>().Add(new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Uid = "existing@outlook",
            OutlookEventId = "graph-1",
            OutlookChangeKey = "old-change",
            Title = "Original title",
            Location = "Old room",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
            Source = "outlook"
        });
        await db.SaveChangesAsync();
        var graph = new FakeMicrosoftGraphClient();
        graph.DeltaPages.Enqueue(new GraphDeltaPage(
            [
                GraphEventFactory.Create("graph-1", "Original title", location: "New room", changeKey: "new-change"),
            ],
            null,
            "delta-link"));
        var service = CreateService(db, graph, protector);

        var batch = await service.SyncAsync(UserId, CancellationToken.None);

        Assert.Equal(1, batch.ConfirmationCount);
        Assert.Equal(0, batch.UpdatedCount);
        var stored = await db.Set<EventEntity>().SingleAsync(e => e.OutlookEventId == "graph-1");
        Assert.Equal("Old room", stored.Location);
    }

    private static OutlookSyncService CreateService(
        PimDbContext db,
        IMicrosoftGraphClient graph,
        ISecretProtector protector)
        => new(
            db,
            new StubHttpClientFactory(),
            new OperationConfirmationService(db),
            new OutlookTokenService(db, protector),
            graph);

    private static CalendarEntity SeedCalendarAndConnection(PimDbContext db, ISecretProtector protector)
    {
        var calendar = new CalendarEntity
        {
            UserId = UserId,
            Name = "Outlook",
            IsDefault = true
        };
        db.Set<CalendarEntity>().Add(calendar);
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
        db.SaveChanges();
        return calendar;
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-delta-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
