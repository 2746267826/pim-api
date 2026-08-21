using System.Security.Claims;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly OpsKeyValidator _validator;

    public OpsKeyMiddleware(RequestDelegate next, IConfiguration cfg)
    {
        _next = next;
        _validator = new OpsKeyValidator(
            cfg["PIM_OPS_KEY"] ?? cfg["Ops:Key"],
            cfg["PIM_OPS_ALLOWED_CIDRS"] ?? cfg["Ops:AllowedCidrs"]);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/ops"))
        {
            await _next(ctx);
            return;
        }

        if (!_validator.HasKeys)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { code = 50301, message = "OpsDisabled" });
            return;
        }

        var key = ctx.Request.Headers["X-PIM-Ops-Key"].FirstOrDefault();
        if (!_validator.IsValid(key))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { code = 40101, message = "OpsKeyMissingOrInvalid" });
            return;
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (!_validator.IsIpAllowed(ip))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { code = 40301, message = "IpNotAllowed" });
            return;
        }

        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", "ops-reader") }, "OpsKey"));
        await _next(ctx);
    }
}
