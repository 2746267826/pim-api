using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pim.Api.Infrastructure.Ops;

/// <summary>
/// <c>/metrics</c> 的鉴权过滤器：admin JWT 或 OpsKey（X-PIM-Ops-Key / Bearer）。
/// 指标端点不承载用户数据，但必须保持鉴权；独立成类是为了让传输门禁与鉴权
/// 可以在同一套真实中间件里做集成测试，而不是在 Program.cs 里复制一份逻辑。
/// </summary>
public static class MetricsAuthorizationFilter
{
    public static async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.IsInRole("admin"))
        {
            return await next(context);
        }

        var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
        var validator = new OpsKeyValidator(
            configuration["PIM_OPS_KEY"] ?? configuration["Ops:Key"]);
        var key = http.Request.Headers["X-PIM-Ops-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            var authorization = http.Request.Headers.Authorization.FirstOrDefault();
            if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                key = authorization["Bearer ".Length..];
            }
        }

        if (validator.HasKeys && validator.IsValid(key))
        {
            return await next(context);
        }

        return Results.Text(
            "{\"code\":40101,\"message\":\"MetricsAuthRequired\"}",
            "application/json",
            statusCode: StatusCodes.Status401Unauthorized);
    }
}
