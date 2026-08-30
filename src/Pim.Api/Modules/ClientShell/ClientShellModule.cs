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
            // Prefer per-component versions parsed from asset filenames; fallback to tag for backward compat
            // Only consider snapshot valid if at least one component has a URL (prevents version without URL)
            var hasSnapshot = snap.WindowsUrl != null || snap.AndroidUrl != null || snap.ShellWindowsUrl != null || snap.ShellAndroidUrl != null
                || snap.WindowsVersion != null || snap.AndroidVersion != null || snap.ShellWindowsVersion != null || snap.ShellAndroidVersion != null
                || snap.LatestVersion != null;
            if (hasSnapshot)
            {
                return Results.Ok(new
                {
                    windowsVersion = snap.WindowsUrl != null ? (snap.WindowsVersion ?? snap.LatestVersion) : null,
                    windowsUrl = snap.WindowsUrl,
                    androidVersion = snap.AndroidUrl != null ? (snap.AndroidVersion ?? snap.LatestVersion) : null,
                    androidUrl = snap.AndroidUrl,
                    shellWindowsVersion = snap.ShellWindowsUrl != null ? (snap.ShellWindowsVersion ?? snap.LatestVersion) : null,
                    shellWindowsUrl = snap.ShellWindowsUrl,
                    shellAndroidVersion = snap.ShellAndroidUrl != null ? (snap.ShellAndroidVersion ?? snap.LatestVersion) : null,
                    shellAndroidUrl = snap.ShellAndroidUrl,
                    checkedAt = snap.CheckedAt,
                    error = snap.Error
                });
            }
            var o = opts.Value;
            return Results.Ok(new
            {
                windowsVersion = o.WindowsVersion,
                windowsUrl = o.WindowsUrl,
                androidVersion = o.AndroidVersion,
                androidUrl = o.AndroidUrl,
                shellWindowsVersion = o.ShellWindowsVersion,
                shellWindowsUrl = o.ShellWindowsUrl,
                shellAndroidVersion = o.ShellAndroidVersion,
                shellAndroidUrl = o.ShellAndroidUrl,
                checkedAt = snap.CheckedAt,
                error = snap.Error
            });
        }).AllowAnonymous();
        return app;
    }
}
