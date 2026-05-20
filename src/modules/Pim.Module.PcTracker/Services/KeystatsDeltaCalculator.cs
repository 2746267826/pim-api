using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public record KeystatsMinuteDelta(
    string DeviceId,
    DateTimeOffset MinuteStartUtc,
    int KeyPresses,
    int TotalClicks,
    double MouseDistance,
    double ScrollDistance,
    bool IsGap,
    bool IsReset);

public static class KeystatsDeltaCalculator
{
    public static KeystatsMinuteDelta Calculate(KeystatsSampleEntity? previous, KeystatsSampleEntity current)
    {
        if (previous is null || previous.StatsDate != current.StatsDate)
        {
            return new KeystatsMinuteDelta(
                current.PimDeviceId,
                current.SampledAtUtc,
                current.KeyPresses,
                TotalClicks(current),
                current.MouseDistance,
                current.ScrollDistance,
                IsGap: true,
                IsReset: false);
        }

        var keyPresses = current.KeyPresses - previous.KeyPresses;
        var leftClicks = current.LeftClicks - previous.LeftClicks;
        var rightClicks = current.RightClicks - previous.RightClicks;
        var middleClicks = current.MiddleClicks - previous.MiddleClicks;
        var sideBackClicks = current.SideBackClicks - previous.SideBackClicks;
        var sideForwardClicks = current.SideForwardClicks - previous.SideForwardClicks;
        var mouseDistance = current.MouseDistance - previous.MouseDistance;
        var scrollDistance = current.ScrollDistance - previous.ScrollDistance;

        if (keyPresses < 0
            || leftClicks < 0
            || rightClicks < 0
            || middleClicks < 0
            || sideBackClicks < 0
            || sideForwardClicks < 0
            || mouseDistance < 0
            || scrollDistance < 0)
        {
            return new KeystatsMinuteDelta(
                current.PimDeviceId,
                current.SampledAtUtc,
                KeyPresses: 0,
                TotalClicks: 0,
                MouseDistance: 0,
                ScrollDistance: 0,
                IsGap: (current.SampledAtUtc - previous.SampledAtUtc).TotalMinutes > 2,
                IsReset: true);
        }

        return new KeystatsMinuteDelta(
            current.PimDeviceId,
            current.SampledAtUtc,
            keyPresses,
            leftClicks + rightClicks + middleClicks + sideBackClicks + sideForwardClicks,
            mouseDistance,
            scrollDistance,
            (current.SampledAtUtc - previous.SampledAtUtc).TotalMinutes > 2,
            IsReset: false);
    }

    private static int TotalClicks(KeystatsSampleEntity sample)
    {
        return sample.LeftClicks
            + sample.RightClicks
            + sample.MiddleClicks
            + sample.SideBackClicks
            + sample.SideForwardClicks;
    }
}
