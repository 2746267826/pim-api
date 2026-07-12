using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class KeyStatsHealthProbeTests
{
    [Fact]
    public void Evaluate_ReturnsMissingProcess_WhenNoProcesses()
    {
        var result = KeyStatsHealthProbe.Evaluate(
            processes: Array.Empty<KeyStatsProcessInfo>(),
            currentSessionId: 1,
            snapshot: null,
            previousSnapshot: null,
            apiError: null);

        Assert.Equal(KeyStatsDetailState.MissingProcess, result.DetailState);
        Assert.False(result.CanUpload);
        Assert.Equal("Unavailable", result.DaemonSourceState);
    }

    [Fact]
    public void Evaluate_ReturnsApiUnreachable_WhenApiError()
    {
        var processes = new[]
        {
            new KeyStatsProcessInfo(100, 1, IsCurrentUserSession: true)
        };

        var result = KeyStatsHealthProbe.Evaluate(
            processes,
            currentSessionId: 1,
            snapshot: null,
            previousSnapshot: null,
            apiError: "Connection refused");

        Assert.Equal(KeyStatsDetailState.ApiUnreachable, result.DetailState);
        Assert.False(result.CanUpload);
    }

    [Fact]
    public void Evaluate_ReturnsStaleZero_WhenAllCountersZeroAndNoGrowth()
    {
        var processes = new[]
        {
            new KeyStatsProcessInfo(100, 1, IsCurrentUserSession: true)
        };
        var snapshot = new KeyStatsCounterSnapshot(
            KeyPresses: 0,
            LeftClicks: 0,
            RightClicks: 0,
            MiddleClicks: 0,
            SideBackClicks: 0,
            SideForwardClicks: 0,
            MouseDistance: 0,
            ScrollDistance: 0);

        var result = KeyStatsHealthProbe.Evaluate(
            processes,
            currentSessionId: 1,
            snapshot,
            previousSnapshot: snapshot,
            apiError: null);

        Assert.Equal(KeyStatsDetailState.ApiOkButStaleZero, result.DetailState);
        Assert.False(result.CanUpload);
        Assert.Equal("stale-zero", result.SkipReason);
    }

    [Fact]
    public void Evaluate_ReturnsAvailable_WhenCountersNonZero()
    {
        var processes = new[]
        {
            new KeyStatsProcessInfo(100, 1, IsCurrentUserSession: true)
        };
        var snapshot = new KeyStatsCounterSnapshot(
            KeyPresses: 12,
            LeftClicks: 3,
            RightClicks: 0,
            MiddleClicks: 0,
            SideBackClicks: 0,
            SideForwardClicks: 0,
            MouseDistance: 100,
            ScrollDistance: 0);

        var result = KeyStatsHealthProbe.Evaluate(
            processes,
            currentSessionId: 1,
            snapshot,
            previousSnapshot: null,
            apiError: null);

        Assert.Equal(KeyStatsDetailState.Available, result.DetailState);
        Assert.True(result.CanUpload);
        Assert.Equal("Available", result.DaemonSourceState);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public void Evaluate_ReturnsAvailable_WhenCountersGrewFromPrevious()
    {
        var processes = new[]
        {
            new KeyStatsProcessInfo(100, 1, IsCurrentUserSession: true)
        };
        var previous = new KeyStatsCounterSnapshot(1, 0, 0, 0, 0, 0, 0, 0);
        var current = previous with { KeyPresses = 2 };

        var result = KeyStatsHealthProbe.Evaluate(
            processes,
            currentSessionId: 1,
            current,
            previous,
            apiError: null);

        Assert.Equal(KeyStatsDetailState.Available, result.DetailState);
        Assert.True(result.CanUpload);
    }

    [Fact]
    public void Evaluate_FlagsForeignSessionProcesses()
    {
        var processes = new[]
        {
            new KeyStatsProcessInfo(100, 0, IsCurrentUserSession: false),
            new KeyStatsProcessInfo(200, 1, IsCurrentUserSession: true)
        };
        var snapshot = new KeyStatsCounterSnapshot(5, 1, 0, 0, 0, 0, 10, 0);

        var result = KeyStatsHealthProbe.Evaluate(
            processes,
            currentSessionId: 1,
            snapshot,
            previousSnapshot: null,
            apiError: null);

        Assert.True(result.HasForeignSessionProcess);
        Assert.Equal(2, result.ProcessCount);
    }
}
