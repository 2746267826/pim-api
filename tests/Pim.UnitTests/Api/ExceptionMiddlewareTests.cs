using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Middleware;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Module.Files.Providers;
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

    [Theory]
    [InlineData(404, 404)]
    [InlineData(429, 429)]
    [InlineData(401, 502)]
    [InlineData(500, 502)]
    public async Task InvokeAsync_MapsOneDriveGraphExceptionsToUpstreamSemantics(int graphStatus, int expected)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new OneDriveGraphException(graphStatus, null, "graph down"),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(expected, context.Response.StatusCode);
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

    /// <summary>
    /// 按**消费方视角**反序列化（camelCase）。裸的 case-sensitive 反序列化会让
    /// 「服务端输出 PascalCase」这类线上缺陷在测试里隐形——正是 issue #342 次生缺陷的成因。
    /// </summary>
    private static async Task<ApiResponse<string>> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<ApiResponse<string>>(
            context.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Assert.IsType<ApiResponse<string>>(response);
    }

    /// <summary>
    /// issue #342 次生缺陷回归：错误响应必须是 **camelCase**（`code` / `message`），
    /// 与全站成功响应、以及前端 `client.ts` 只读 `message/detail/title` 的约定一致。
    /// 用 PascalCase 时前端取不到 message，只能回退显示「HTTP 400」，用户看不到原因。
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WritesErrorMessageInCamelCaseForTheClient()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new DomainException(5333, "OneDrive 暂未返回下载直链，请稍后重试"),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var root = document.RootElement;

        // 小写字段必须存在，且能被前端读到
        Assert.True(root.TryGetProperty("code", out var code), "错误响应缺少 camelCase 的 code 字段");
        Assert.Equal(5333, code.GetInt32());
        Assert.True(root.TryGetProperty("message", out var message), "错误响应缺少 camelCase 的 message 字段");
        Assert.Equal("OneDrive 暂未返回下载直链，请稍后重试", message.GetString());
        // PascalCase 不得再出现，避免两套字段并存的歧义
        Assert.False(root.TryGetProperty("Code", out _), "错误响应不应再输出 PascalCase 的 Code");
        Assert.False(root.TryGetProperty("Message", out _), "错误响应不应再输出 PascalCase 的 Message");
    }

    /// <summary>Graph 失败（上游不可用）的错误体同样必须是 camelCase 可读文案。</summary>
    [Fact]
    public async Task InvokeAsync_GraphFailure_WritesCamelCaseMessage()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new OneDriveGraphException(500, null, "graph down"),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.True(document.RootElement.TryGetProperty("message", out var message));
        Assert.False(string.IsNullOrWhiteSpace(message.GetString()));
        Assert.False(document.RootElement.TryGetProperty("Message", out _));
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
