using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Middleware;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Xunit;

namespace Pim.UnitTests.Api;

public class ExceptionMiddlewareTests
{
    [Theory]
    [InlineData(4004)]
    [InlineData(4006)]
    [InlineData(5104)]
    [InlineData(5300)]
    [InlineData(5304)]
    [InlineData(5305)]
    public async Task InvokeAsync_MapsKnownNotFoundDomainErrorsTo404(int errorCode)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new DomainException(errorCode, "Not found"),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.Equal(errorCode, response.Code);
    }

    [Fact]
    public async Task InvokeAsync_MapsValidationDomainErrorsTo400()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new DomainException(4003, "快速记录状态无效"),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.Equal(4003, response.Code);
    }

    private static async Task<ApiResponse<string>> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<ApiResponse<string>>(context.Response.Body);
        return Assert.IsType<ApiResponse<string>>(response);
    }

    /// <summary>
    /// 请求方已断开且异常链含取消类异常 → 499，且不落 Error 级日志（issue #299）。
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ClientDisconnectWithCancellation_MapsTo499()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(canceled: true);
        var logger = new ListLogger<ExceptionMiddleware>();
        var middleware = new ExceptionMiddleware(
            _ => throw new TaskCanceledException("The operation was canceled.", new TimeoutException()),
            logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Empty(logger.Entries.Where(entry => entry.Level == LogLevel.Error));
    }

    /// <summary>
    /// 关键反例：异常是取消类，但请求并未断开（服务端自身取消/上游超时）
    /// —— 必须仍旧记 500，否则真实故障会被"洗白"成客户端中断。
    /// </summary>
    [Fact]
    public async Task InvokeAsync_CancellationWithoutDisconnect_RemainsServerError()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var logger = new ListLogger<ExceptionMiddleware>();
        var middleware = new ExceptionMiddleware(
            _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout elapsing.",
                new TimeoutException()),
            logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Error));
    }

    /// <summary>
    /// 关键反例：请求确实已断开，但异常与取消无关（例如空引用）
    /// —— 这是真实 bug，必须仍是 500 + Error 日志。
    /// </summary>
    [Fact]
    public async Task InvokeAsync_DisconnectWithoutCancellation_RemainsServerError()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(canceled: true);
        var logger = new ListLogger<ExceptionMiddleware>();
        var middleware = new ExceptionMiddleware(
            _ => throw new InvalidOperationException("boom"),
            logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Single(logger.Entries.Where(entry => entry.Level == LogLevel.Error));
    }
}
