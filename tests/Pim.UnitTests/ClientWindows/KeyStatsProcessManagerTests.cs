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

    [Fact]
    public void StopResult_NeedsElevation_WhenAnyAccessDenied()
    {
        var results = new[]
        {
            new KeyStatsStopResult(10, Succeeded: true, Error: null),
            new KeyStatsStopResult(20, Succeeded: false, Error: "access-denied")
        };

        Assert.True(KeyStatsProcessManager.NeedsElevation(results));
        Assert.Equal(new[] { 20 }, KeyStatsProcessManager.FailedStopIds(results));
    }

    [Fact]
    public void StopResult_DoesNotNeedElevation_WhenAllSucceeded()
    {
        var results = new[]
        {
            new KeyStatsStopResult(10, Succeeded: true, Error: null)
        };

        Assert.False(KeyStatsProcessManager.NeedsElevation(results));
    }
}
