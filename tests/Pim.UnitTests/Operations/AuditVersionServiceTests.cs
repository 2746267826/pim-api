using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Operations;

public class AuditVersionServiceTests
{
    [Fact]
    public async Task RecordAsyncWritesBeforeAfterAuditVersion()
    {
        await using var db = CreateDb();
        var service = new AuditVersionService(db);
        var objectId = Guid.NewGuid();
        var confirmationId = Guid.NewGuid();

        var recorded = await service.RecordAsync(
            "event",
            objectId,
            new { title = "Before" },
            new { title = "After" },
            ["title"],
            confirmationId,
            "pim",
            CancellationToken.None);

        var timeline = await service.GetTimelineAsync("event", objectId, CancellationToken.None);
        var item = Assert.Single(timeline.Items);
        Assert.Equal(recorded.Id, item.Id);
        Assert.Equal(confirmationId, item.ConfirmationId);
        Assert.Contains("\"title\":\"Before\"", item.BeforeJson);
        Assert.Contains("\"title\":\"After\"", item.AfterJson);
        Assert.Contains("title", item.ChangedFieldsJson);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new PimDbContext(options);
    }
}
