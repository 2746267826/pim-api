using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class ScheduleWorkbenchFoundationParityTests
{
    [Fact]
    public void FoundationRiskLevelsAndEntitiesRemainPresent()
    {
        PimDbContext.RegisterModuleAssembly(typeof(TaskExecutionSegmentEntity).Assembly);

        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L0AutomaticArtifact"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L1LowRiskAction"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L2PimFactChange"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L3ExternalSourceOrWriteback"));
        Assert.True(Enum.IsDefined(typeof(OperationRiskLevel), "L4BatchOrDestructiveGovernance"));

        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        using var db = new PimDbContext(options);

        Assert.NotNull(db.Model.FindEntityType(typeof(TaskExecutionSegmentEntity)));
        Assert.NotNull(db.Model.FindEntityType(typeof(OutlookSyncBatchEntity)));
    }
}
