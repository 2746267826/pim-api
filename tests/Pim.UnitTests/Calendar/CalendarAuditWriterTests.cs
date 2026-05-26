using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class CalendarAuditWriterTests
{
    [Fact]
    public async Task RecordSuccessAsync_WritesCalendarAuditWithMetadata()
    {
        await using var db = CreateDb();
        var writer = new CalendarAuditWriter(new AuditLogService(db));
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var resourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        await writer.RecordSuccessAsync(
            userId,
            "calendar.events.delete",
            "calendar_event",
            resourceId,
            new Dictionary<string, string>
            {
                ["title"] = "Focus block",
                ["operationId"] = "22222222-2222-2222-2222-222222222222",
                ["affectedCount"] = "1"
            });

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(userId, audit.UserId);
        Assert.Equal("calendar.events.delete", audit.Action);
        Assert.Equal("calendar_event", audit.ResourceType);
        Assert.Equal(resourceId.ToString(), audit.ResourceId);
        Assert.Equal("calendar", audit.Source);
        Assert.Contains("Focus block", audit.MetadataJson);
        Assert.Contains("affectedCount", audit.MetadataJson);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"calendar-audit-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }
}
