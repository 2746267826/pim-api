using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Xunit;

namespace Pim.UnitTests.Calendar;

[Trait("Category", "Integration")]
public class ScheduleWorkbenchModelTests
{
    [Fact]
    public void PimDbContext_ConfiguresScheduleWorkbenchEntities()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        using var db = CreateDb();

        var segment = db.Model.FindEntityType(typeof(TaskExecutionSegmentEntity));
        Assert.NotNull(segment);
        Assert.Equal("task_execution_segments", segment.GetTableName());
        Assert.NotNull(segment.FindProperty(nameof(TaskExecutionSegmentEntity.StartsAt)));
        Assert.NotNull(segment.FindProperty(nameof(TaskExecutionSegmentEntity.EndsAt)));
        Assert.Contains(segment.GetIndexes(), index =>
            index.Properties.Select(p => p.Name).SequenceEqual([
                nameof(TaskExecutionSegmentEntity.UserId),
                nameof(TaskExecutionSegmentEntity.TaskId),
                nameof(TaskExecutionSegmentEntity.StartsAt)
            ]));

        var batch = db.Model.FindEntityType(typeof(OutlookSyncBatchEntity));
        Assert.NotNull(batch);
        Assert.Equal("outlook_sync_batches", batch.GetTableName());
        Assert.Contains(batch.GetIndexes(), index =>
            index.Properties.Select(p => p.Name).SequenceEqual([
                nameof(OutlookSyncBatchEntity.UserId),
                nameof(OutlookSyncBatchEntity.Provider),
                nameof(OutlookSyncBatchEntity.StartedAt)
            ]));

        var connection = db.Model.FindEntityType(typeof(OutlookConnectionEntity));
        Assert.NotNull(connection);
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.ClientId)));
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.TenantId)));
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.Scopes)));
        Assert.NotNull(connection.FindProperty(nameof(OutlookConnectionEntity.TokenHealth)));
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=localhost;Database=schedule_workbench_model_tests")
            .Options;
        return new PimDbContext(options);
    }
}
