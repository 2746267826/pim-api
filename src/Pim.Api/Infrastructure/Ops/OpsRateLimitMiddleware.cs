using System.Text.Json;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly OpsRateLimiter _limiter;
    private readonly ILogger<OpsRateLimitMiddleware> _logger;

    public OpsRateLimitMiddleware(RequestDelegate next, OpsRateLimiter limiter, ILogger<OpsRateLimitMiddleware> logger)
    {
        _next = next;
        _limiter = limiter;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/ops"))
        {
            await _next(ctx);
            return;
        }

        var ip = OpsIpHelper.GetClientIp(ctx);

        if (!_limiter.TryAcquire(ip, out var retryAfter))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = retryAfter.ToString();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { code = 42901, message = "RateLimited" }));
            return;
        }

        try
        {
            await _next(ctx);
        }
        finally
        {
            _limiter.Release(ip);
        }
    }
}
