using Microsoft.Extensions.Options;
using Pim.Api.Services;

namespace Pim.Api.Modules.ClientShell;

public static class ClientShellModule
{
    public static IServiceCollection AddClientShell(this IServiceCollection services, IConfiguration cfg)
    {
        // Priority: ClientShell > ShellClient (later Configure wins for overlapping keys)
        services.Configure<ClientShellOptions>(cfg.GetSection("ShellClient"));
        services.Configure<ClientShellOptions>(cfg.GetSection("ClientShell"));
        return services;
    }

    public static IEndpointRouteBuilder MapClientShell(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/client/shell/latest", (IOptions<ClientShellOptions> opts, GitHubReleaseService gh) =>
        {
            var snap = gh.Snapshot;
            if (snap.LatestVersion != null) return Results.Ok(new { windowsVersion = snap.LatestVersion, windowsUrl = snap.WindowsUrl, androidVersion = snap.LatestVersion, androidUrl = snap.AndroidUrl, checkedAt = snap.CheckedAt, error = snap.Error });
            var o = opts.Value;
            return Results.Ok(new { windowsVersion = o.WindowsVersion, windowsUrl = o.WindowsUrl, androidVersion = o.AndroidVersion, androidUrl = o.AndroidUrl, checkedAt = snap.CheckedAt, error = snap.Error });
        }).AllowAnonymous();
        return app;
    }
}
