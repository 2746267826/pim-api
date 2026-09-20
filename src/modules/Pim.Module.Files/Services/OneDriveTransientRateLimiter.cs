using System.Collections.Concurrent;
using Pim.Core.Exceptions;

namespace Pim.Module.Files.Services;

/// <summary>
/// 瞬态内容读取的每用户固定窗口限流（单例）。覆盖 read_file_text / extracted-text
/// 等会触发服务器瞬态下载的出口（设计文档 §13）。
/// </summary>
public sealed class OneDriveTransientRateLimiter
{
    public const int DefaultLimitPerMinute = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset WindowStart, int Count)> _counters = new();
    private readonly TimeProvider _clock;
    private readonly int _limit;

    public OneDriveTransientRateLimiter(TimeProvider? clock = null, int? limit = null)
    {
        _clock = clock ?? TimeProvider.System;
        _limit = limit ?? DefaultLimitPerMinute;
    }

    /// <summary>占用一个配额；超限抛 5341。</summary>
    public void AssertAllowed(Guid userId)
    {
        var now = _clock.GetUtcNow();
        var allowed = true;
        _counters.AddOrUpdate(
            userId,
            _ => (now, 1),
            (_, window) =>
            {
                if (now - window.WindowStart >= Window)
                {
                    return (now, 1);
                }
                if (window.Count >= _limit)
                {
                    allowed = false;
                    return window;
                }
                return (window.WindowStart, window.Count + 1);
            });

        if (!allowed)
        {
            throw new DomainException(5341, $"读取过于频繁，每分钟最多 {_limit} 次，请稍后再试");
        }
    }
}
