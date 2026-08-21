using System.Security.Claims;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _cfg;

    public OpsKeyMiddleware(RequestDelegate next, IConfiguration cfg)
    {
        _next = next;
        _cfg = cfg;
    }

    private OpsKeyValidator CreateValidator() => new(
        _cfg["PIM_OPS_KEY"] ?? _cfg["Ops:Key"],
        _cfg["PIM_OPS_ALLOWED_CIDRS"] ?? _cfg["Ops:AllowedCidrs"]);

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/ops"))
        {
            await _next(ctx);
            return;
        }

        var validator = CreateValidator();

        if (!validator.HasKeys)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { code = 50301, message = "OpsDisabled" });
            return;
        }

        var key = ctx.Request.Headers["X-PIM-Ops-Key"].FirstOrDefault();
        if (!validator.IsValid(key))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { code = 40101, message = "OpsKeyMissingOrInvalid" });
            return;
        }

        var ip = OpsIpHelper.GetClientIp(ctx);
        if (!validator.IsIpAllowed(ip))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { code = 40301, message = "IpNotAllowed" });
            return;
        }

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "ops-reader") }, "OpsKey");
        // Also add "role" claim for compatibility with JWT role mapping
        identity.AddClaim(new Claim("role", "ops-reader"));
        ctx.User.AddIdentity(identity);
        await _next(ctx);
    }
}
