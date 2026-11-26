using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

public static class AdminAuthorizationExtensions
{
    public static RouteGroupBuilder RequireCurrentActiveAdmin(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var currentUser = services.GetRequiredService<ICurrentUserService>();
            if (currentUser.UserId is not Guid userId) return Results.Unauthorized();

            var db = services.GetRequiredService<PimDbContext>();
            var current = await db.Users.AsNoTracking()
                .Where(x => x.Id == userId)
                .Select(x => new { x.IsActive, x.Role })
                .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
            if (current is null || !current.IsActive || current.Role != AdminBootstrap.AdminRole)
                return Results.Forbid();

            return await next(context);
        });
        return group;
    }
}
