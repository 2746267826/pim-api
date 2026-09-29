using System;
using System.Diagnostics;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 体检服务所在进程的**本次启动时刻**（WO-RELIABILITY-WINDOW-20260928 REQ-1）。
/// 它和体检时刻一起决定考核线：<c>max(体检时刻 − 考核窗, 进程启动时刻)</c>。
/// </summary>
public interface IProcessStartTimeProvider
{
    /// <summary>本次进程启动时刻（UTC）。进程重启后自动变为新时刻 —— 这就是"重启即自动重置账本"。</summary>
    DateTimeOffset ProcessStartedAtUtc { get; }
}

/// <summary>
/// 进程启动时刻的默认实现：读操作系统记录的当前进程启动时间，**只在内存中持有、不落库**
/// （AC-1.5：模拟新进程启动后账本必须以新时刻为准）。
///
/// <para>
/// 取值优先用 <see cref="Process.StartTime"/>（真实进程启动时刻，容器重启后随之更新）；
/// 平台不支持 / 取不到时退化为"注册这个实例的时刻"——
/// 因此生产必须在宿主启动时构造它（见 <c>ServiceCollectionExtensions</c> 里传实例的注册方式），
/// 不能做成懒加载：晚 3 小时构造会让账本起点也晚 3 小时。
/// </para>
/// </summary>
public sealed class ProcessStartTimeProvider : IProcessStartTimeProvider
{
    public ProcessStartTimeProvider(TimeProvider? timeProvider = null)
    {
        ProcessStartedAtUtc = ResolveProcessStartUtc(timeProvider ?? TimeProvider.System);
    }

    /// <inheritdoc />
    public DateTimeOffset ProcessStartedAtUtc { get; }

    private static DateTimeOffset ResolveProcessStartUtc(TimeProvider timeProvider)
    {
        try
        {
            var startTime = Process.GetCurrentProcess().StartTime;
            var utc = startTime.Kind switch
            {
                DateTimeKind.Utc => startTime,
                DateTimeKind.Local => startTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(startTime, DateTimeKind.Local).ToUniversalTime()
            };

            // 系统时钟回拨等异常情况下 StartTime 可能落在未来：不能让它把考核线推到未来，
            // 否则窗内永远为空、所有违规都被折进欠账。此时退化为"当前时刻"。
            var now = timeProvider.GetUtcNow();
            return utc > now ? now : new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or NotSupportedException)
        {
            return timeProvider.GetUtcNow();
        }
    }
}
