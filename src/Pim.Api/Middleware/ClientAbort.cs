namespace Pim.Api.Middleware;

/// <summary>
/// 识别「请求方已断开」导致的取消，把它与真实的服务端/上游故障区分开（issue #299）。
///
/// 浏览器拖动/缩放地图时会主动取消在途的瓦片请求，这类中断在服务端表现为
/// <see cref="OperationCanceledException"/>（内层常见 <c>SocketException(125)</c> 或
/// <c>ObjectDisposedException</c>）。请求方已经拿不到响应了，把它记成 500 只会污染错误日志与错误率。
/// </summary>
public static class ClientAbort
{
    /// <summary>
    /// 是否应判定为客户端中断。
    ///
    /// 两个条件缺一不可：请求确已断开，且异常链中确有取消类异常。
    /// 只凭异常类型判断会把「服务端自身超时/主动取消」也算成客户端中断，
    /// 从而掩盖真实故障；只凭断开标志判断则会把并发发生的真实异常一并吞掉。
    /// </summary>
    public static bool IsClientAbort(HttpContext context, Exception exception)
        => context.RequestAborted.IsCancellationRequested && ContainsCancellation(exception);

    /// <summary>异常链（含 <see cref="Exception.InnerException"/>）中是否出现取消类异常。</summary>
    public static bool ContainsCancellation(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
                return true;
        }

        return false;
    }
}
