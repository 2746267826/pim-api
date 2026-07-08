using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class PlanningObjectModelTests
{
    [Fact]
    public void ModelContainsAllApprovedPlanningObjects()
    {
        PimDbContext.RegisterModuleAssembly(typeof(TaskExecutionSegmentEntity).Assembly);

        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        using var db = new PimDbContext(options);

        Assert.NotNull(db.Model.FindEntityType(typeof(DomainProjectEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(TaskBookEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(TaskChecklistItemEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(HabitRoutineEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(HabitOccurrenceEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(AvailabilityWindowEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(AiPlanningPlaceholderEntity)));
    }

    [Fact]
    public void TaskHasProjectBookHierarchyStateAndReviewMetadata()
    {
        var type = typeof(TaskEntity);
        Assert.NotNull(type.GetProperty("DomainProjectId"));
        Assert.NotNull(type.GetProperty("TaskBookId"));
        Assert.NotNull(type.GetProperty("ParentTaskId"));
        Assert.NotNull(type.GetProperty("StateReason"));
        Assert.NotNull(type.GetProperty("ReviewOutcome"));
        Assert.NotNull(type.GetProperty("Source"));
    }
}
