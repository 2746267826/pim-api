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

    private static int ResolveDomainStatusCode(int errorCode)
        => errorCode is 4004 or 4006 or 5104 or 5300 or 5304 or 5305 or 40401
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status400BadRequest;
}
