using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class KeyStatsProcessManagerTests
{
    [Fact]
    public void SelectActions_KeepsOneCurrentSessionProcess_AndStopsOthers()
    {
        var processes = new[]
        {
            new KeyStatsProcessInfo(10, 0, false),
            new KeyStatsProcessInfo(20, 1, true),
            new KeyStatsProcessInfo(30, 1, true)
        };

        var plan = KeyStatsProcessManager.BuildConvergencePlan(processes, currentSessionId: 1);

        Assert.Equal(new[] { 10, 30 }, plan.ProcessIdsToStop);
        Assert.False(plan.ShouldStart);
        Assert.Equal(20, plan.KeepProcessId);
    }

    [Fact]
    public void SelectActions_Starts_WhenNoCurrentSessionProcess()
    {
        var processes = new[]
        {
            new KeyStatsProcessInfo(10, 0, false)
        };

        var plan = KeyStatsProcessManager.BuildConvergencePlan(processes, currentSessionId: 1);

        Assert.Equal(new[] { 10 }, plan.ProcessIdsToStop);
        Assert.True(plan.ShouldStart);
        Assert.Null(plan.KeepProcessId);
    }
}
