using System.Text;
using Pim.Api.Middleware;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 幂等中间件的响应捕获流（契约语义 §2）：缓存上限 1 MiB。
///
/// <para>
/// 两个不可退让的性质：
/// 1. **未超限**时只缓冲、不向客户端写 —— 下游中途抛异常时外层还能改写状态码；
/// 2. **超限**时不再继续占用内存，已缓冲部分立刻交给客户端，后续内容直通，
///    并打上 <c>Overflowed</c>（中间件据此放弃缓存，避免「2xx + 空正文」的重放）。
/// </para>
/// </summary>
public sealed class IdempotencyCaptureStreamTests
{
    [Fact]
    public async Task UnderLimit_BuffersOnly_UntilCopiedToClient()
    {
        using var inner = new MemoryStream();
        using var capture = new IdempotencyMiddleware.CappedCaptureStream(inner, limit: 16);

        await capture.WriteAsync(Encoding.UTF8.GetBytes("hello"));

        Assert.False(capture.Overflowed);
        Assert.Equal(0, inner.Length);                       // 还没写给客户端
        Assert.Equal("hello", capture.CapturedText());

        await capture.CopyToClientAsync(inner, CancellationToken.None);

        Assert.Equal("hello", Encoding.UTF8.GetString(inner.ToArray()));
    }

    [Fact]
    public async Task OverLimit_FlushesBufferedContentAndPassesThroughUntruncated()
    {
        using var inner = new MemoryStream();
        using var capture = new IdempotencyMiddleware.CappedCaptureStream(inner, limit: 8);

        await capture.WriteAsync(Encoding.UTF8.GetBytes("12345"));  // 5
        await capture.WriteAsync(Encoding.UTF8.GetBytes("67890"));  // 10 → 超限
        await capture.WriteAsync(Encoding.UTF8.GetBytes("abc"));    // 直通

        Assert.True(capture.Overflowed);
        Assert.Null(capture.CapturedText());                 // 没有可缓存的正文
        Assert.Equal(0, capture.Length);                     // 缓冲已释放，内存不再增长
        // 客户端收到的仍是**完整**内容，没有被截断
        Assert.Equal("1234567890abc", Encoding.UTF8.GetString(inner.ToArray()));
    }

    [Fact]
    public void SyncWrite_OverLimit_AlsoPassesThrough()
    {
        using var inner = new MemoryStream();
        using var capture = new IdempotencyMiddleware.CappedCaptureStream(inner, limit: 4);

        capture.Write(Encoding.UTF8.GetBytes("1234"));
        capture.Write(Encoding.UTF8.GetBytes("5"));

        Assert.True(capture.Overflowed);
        Assert.Equal("12345", Encoding.UTF8.GetString(inner.ToArray()));
    }

    /// <summary>
    /// review P2-1：未溢出时正文只在 _buffer 里。此时把 Flush 转发给内层会提前启动响应，
    /// 之后的正文就只能靠中间件回写；更糟的是旧实现会因 HasStarted 跳过回写（2xx + 空正文）。
    /// </summary>
    [Fact]
    public async Task Flush_DoesNotForwardWhileBuffering()
    {
        var inner = new FlushCountingStream();
        using var capture = new IdempotencyMiddleware.CappedCaptureStream(inner, limit: 16);

        await capture.WriteAsync(Encoding.UTF8.GetBytes("hello"));
        capture.Flush();
        await capture.FlushAsync(CancellationToken.None);

        Assert.Equal(0, inner.Flushes);                       // 没有提前启动响应

        await capture.WriteAsync(Encoding.UTF8.GetBytes("0123456789ab"));  // 溢出 → 直通
        capture.Flush();
        Assert.True(capture.Overflowed);
        Assert.Equal(1, inner.Flushes);                       // 溢出后恢复直通语义
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int Flushes { get; private set; }
        public override void Flush() => Flushes++;
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExactlyAtLimit_IsNotOverflow()
    {
        using var inner = new MemoryStream();
        using var capture = new IdempotencyMiddleware.CappedCaptureStream(inner, limit: 4);

        await capture.WriteAsync(Encoding.UTF8.GetBytes("1234"));

        Assert.False(capture.Overflowed);
        Assert.Equal(0, inner.Length);
    }
}
