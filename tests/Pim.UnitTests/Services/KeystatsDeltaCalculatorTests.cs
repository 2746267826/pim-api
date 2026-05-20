using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class KeystatsDeltaCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsDifferenceBetweenConsecutiveSamples()
    {
        var previous = Sample("2026-05-20T05:55:00+00:00", keys: 10, totalClicks: 2);
        var current = Sample("2026-05-20T05:56:00+00:00", keys: 17, totalClicks: 5);

        var delta = KeystatsDeltaCalculator.Calculate(previous, current);

        Assert.Equal("DESKTOP", delta.DeviceId);
        Assert.Equal(current.SampledAtUtc, delta.MinuteStartUtc);
        Assert.Equal(7, delta.KeyPresses);
        Assert.Equal(3, delta.TotalClicks);
        Assert.False(delta.IsGap);
        Assert.False(delta.IsReset);
    }

    [Fact]
    public void Calculate_MarksResetWhenCountersDecrease()
    {
        var previous = Sample("2026-05-20T05:55:00+00:00", keys: 17, totalClicks: 5);
        var current = Sample("2026-05-20T05:56:00+00:00", keys: 10, totalClicks: 2);

        var delta = KeystatsDeltaCalculator.Calculate(previous, current);

        Assert.Equal(0, delta.KeyPresses);
        Assert.Equal(0, delta.TotalClicks);
        Assert.Equal(0, delta.MouseDistance);
        Assert.Equal(0, delta.ScrollDistance);
        Assert.False(delta.IsGap);
        Assert.True(delta.IsReset);
    }

    [Fact]
    public void Calculate_MarksResetWhenIndividualClickCounterDecreases()
    {
        var previous = Sample("2026-05-20T05:55:00+00:00", keys: 10, totalClicks: 5);
        previous.LeftClicks = 5;
        previous.RightClicks = 0;
        var current = Sample("2026-05-20T05:56:00+00:00", keys: 17, totalClicks: 6);
        current.LeftClicks = 4;
        current.RightClicks = 2;

        var delta = KeystatsDeltaCalculator.Calculate(previous, current);

        Assert.Equal(0, delta.KeyPresses);
        Assert.Equal(0, delta.TotalClicks);
        Assert.Equal(0, delta.MouseDistance);
        Assert.Equal(0, delta.ScrollDistance);
        Assert.False(delta.IsGap);
        Assert.True(delta.IsReset);
    }

    [Fact]
    public void Calculate_MarksGapWhenSamplesMoreThanTwoMinutesApart()
    {
        var previous = Sample("2026-05-20T05:55:00+00:00", keys: 10, totalClicks: 2);
        var current = Sample("2026-05-20T05:58:00+00:00", keys: 17, totalClicks: 5);

        var delta = KeystatsDeltaCalculator.Calculate(previous, current);

        Assert.Equal(7, delta.KeyPresses);
        Assert.Equal(3, delta.TotalClicks);
        Assert.True(delta.IsGap);
        Assert.False(delta.IsReset);
    }

    [Fact]
    public void Calculate_MarksGapAndUsesCurrentCountersWhenPreviousIsMissing()
    {
        var current = Sample("2026-05-20T05:55:00+00:00", keys: 17, totalClicks: 5);

        var delta = KeystatsDeltaCalculator.Calculate(previous: null, current);

        Assert.Equal(17, delta.KeyPresses);
        Assert.Equal(5, delta.TotalClicks);
        Assert.Equal(current.MouseDistance, delta.MouseDistance);
        Assert.Equal(current.ScrollDistance, delta.ScrollDistance);
        Assert.True(delta.IsGap);
        Assert.False(delta.IsReset);
    }

    private static KeystatsSampleEntity Sample(string sampledAt, int keys, int totalClicks)
    {
        return new KeystatsSampleEntity
        {
            PimDeviceId = "DESKTOP",
            SampledAtUtc = DateTimeOffset.Parse(sampledAt),
            StatsDate = new DateTime(2026, 5, 20),
            KeyPresses = keys,
            LeftClicks = totalClicks,
            MouseDistance = keys * 10,
            ScrollDistance = totalClicks * 20
        };
    }
}
