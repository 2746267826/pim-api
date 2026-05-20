using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcTrackerCompleteCaptureTests
{
    [Fact]
    public void Model_IncludesCompleteCaptureEntities()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new PimDbContext(options);

        Assert.NotNull(db.Model.FindEntityType(typeof(AwBucketEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(KeystatsSampleEntity)));
    }

    [Fact]
    public void SchemaSql_CreatesAwEventsTableBeforeAlteringIt()
    {
        var createIndex = PcTrackerSchemaInitializer.SchemaSql.IndexOf(
            "CREATE TABLE IF NOT EXISTS pc_aw_events",
            StringComparison.Ordinal);
        var alterIndex = PcTrackerSchemaInitializer.SchemaSql.IndexOf(
            "ALTER TABLE pc_aw_events",
            StringComparison.Ordinal);

        Assert.True(createIndex >= 0, "Schema SQL must create pc_aw_events for partial existing databases.");
        Assert.True(alterIndex >= 0, "Schema SQL must keep ALTER statements for upgrade safety.");
        Assert.True(createIndex < alterIndex, "pc_aw_events must be created before it is altered.");
    }
}
