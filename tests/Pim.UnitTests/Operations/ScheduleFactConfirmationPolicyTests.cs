using Pim.Core.Operations;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Operations;

public class ScheduleFactConfirmationPolicyTests
{
    [Theory]
    [InlineData("title")]
    [InlineData("dtStart")]
    [InlineData("dtEnd")]
    [InlineData("location")]
    [InlineData("status")]
    [InlineData("recurrence")]
    [InlineData("task-segment")]
    [InlineData("habit-rule")]
    public void PimCoreFactChangesRequireL2(string changedField)
    {
        var decision = ScheduleFactConfirmationPolicy.Classify("pim", [changedField]);

        Assert.Equal(OperationRiskLevel.L2PimFactChange, decision.RiskLevel);
        Assert.False(decision.RequiresSecondLevelConfirmation);
        Assert.False(decision.RequiresStrictConfirmation);
    }

    [Fact]
    public void OutlookCoreFactChangesRequireL3AndSecondLevelConfirmation()
    {
        var decision = ScheduleFactConfirmationPolicy.Classify("outlook", ["location"]);

        Assert.Equal(OperationRiskLevel.L3ExternalSourceOrWriteback, decision.RiskLevel);
        Assert.True(decision.RequiresSecondLevelConfirmation);
        Assert.False(decision.RequiresStrictConfirmation);
    }

    [Theory]
    [InlineData("stop-sync")]
    [InlineData("batch-delete")]
    [InlineData("bulk-writeback")]
    [InlineData("recurrence-wide-delete")]
    public void DestructiveGovernanceRequiresL4AndStrictConfirmation(string changedField)
    {
        var decision = ScheduleFactConfirmationPolicy.Classify("pim", [changedField]);

        Assert.Equal(OperationRiskLevel.L4BatchOrDestructiveGovernance, decision.RiskLevel);
        Assert.False(decision.RequiresSecondLevelConfirmation);
        Assert.True(decision.RequiresStrictConfirmation);
    }
}
