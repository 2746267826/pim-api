using System.Text.Json;
using Pim.Api.Infrastructure;
using Pim.Core.Common;
using Pim.Core.Exceptions;

namespace Pim.Api.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException ex)
        {
            context.Response.StatusCode = ResolveDomainStatusCode(ex.ErrorCode);
            context.Response.ContentType = "application/json";
            var response = ApiResponse<string>.Error(ex.ErrorCode, ex.Message);
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }
        catch (BadHttpRequestException ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            _logger.LogWarning(ex, "Bad HTTP request with correlation id {CorrelationId}: {Message}", correlationId, ex.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            var response = ApiResponse<string>.Error(40000, $"请求参数格式无效: {ex.Message}");
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }
        catch (Exception ex) when (ClientAbort.IsClientAbort(context, ex))
        {
            // 请求方已断开（如地图拖动/缩放取消在途瓦片请求，issue #299）：
            // 响应已无人接收，按 nginx 惯例记 499，且不记 Error —— 否则每次浏览地图都会
            // 向错误日志与错误率指标打入一串 5xx 脉冲，掩盖真实故障。
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            _logger.LogInformation(
                "Client aborted the request with correlation id {CorrelationId}",
                correlationId);
            await SetClientClosedRequestStatusAsync(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            _logger.LogError(ex, "Unhandled exception with correlation id {CorrelationId}", correlationId);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            var response = ApiResponse<string>.Error(01001, "内部服务器错误");
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }
    }

    /// <summary>
    /// 标记 499 并尽力结束响应。
    ///
    /// 中途不能抛出：异常处理中间件一旦二次抛异常，异常会绕过这里继续向外传播，
    /// 结果反而是更响的 500 噪音。请求已断开时写入通常不会成功（也可能抛
    /// <c>ObjectDisposedException</c>），所以整体吞掉。
    /// </summary>
    private static async Task SetClientClosedRequestStatusAsync(HttpContext context)
    {
        try
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            await context.Response.CompleteAsync();
        }
        catch (Exception)
        {
            // 请求方已断开，写响应失败属预期，忽略。
        }
    }

    private static int ResolveDomainStatusCode(int errorCode) => errorCode switch
    {
        40401 or 4004 or 4006 or 5104 or 5300 or 5304 or 5305 => StatusCodes.Status404NotFound,
        40101 => StatusCodes.Status401Unauthorized,
        40301 or 40302 => StatusCodes.Status403Forbidden,
        42901 => StatusCodes.Status429TooManyRequests,
        50301 => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };
}
