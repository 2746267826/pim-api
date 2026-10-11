using Microsoft.Extensions.Options;
using Pim.Api.Services;
using Pim.Core.Common;

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
        IResult Handle(IOptions<ClientShellOptions> opts, GitHubReleaseService gh)
        {
            var snap = gh.Snapshot;
            // Prefer per-component versions parsed from asset filenames; fallback to tag for backward compat
            // Only consider snapshot valid if at least one component has a URL (prevents version without URL)
            var hasSnapshot = snap.WindowsUrl != null || snap.AndroidUrl != null || snap.ShellWindowsUrl != null || snap.ShellAndroidUrl != null;
            if (hasSnapshot)
            {
                return Results.Ok(new ClientShellLatestResponse(
                    snap.WindowsUrl != null ? (snap.WindowsVersion ?? snap.LatestVersion) : null,
                    snap.WindowsUrl,
                    snap.AndroidUrl != null ? (snap.AndroidVersion ?? snap.LatestVersion) : null,
                    snap.AndroidUrl,
                    snap.ShellWindowsUrl != null ? (snap.ShellWindowsVersion ?? snap.LatestVersion) : null,
                    snap.ShellWindowsUrl,
                    snap.ShellAndroidUrl != null ? (snap.ShellAndroidVersion ?? snap.LatestVersion) : null,
                    snap.ShellAndroidUrl,
                    snap.CheckedAt,
                    snap.Error));
            }
            var o = opts.Value;
            return Results.Ok(new ClientShellLatestResponse(
                o.WindowsVersion,
                o.WindowsUrl,
                o.AndroidVersion,
                o.AndroidUrl,
                o.ShellWindowsVersion,
                o.ShellWindowsUrl,
                o.ShellAndroidVersion,
                o.ShellAndroidUrl,
                snap.CheckedAt,
                snap.Error));
        }

        app.MapGet("/api/client/shell/latest", Handle).AllowAnonymous()
        .Produces<ClientShellLatestResponse>(200)
        .Produces<ApiResponse<string>>(500);
        // Compatibility: Android previously used /api/v1/client/shell/latest via Retrofit baseUrl.
        // Keep both so old APKs (e.g. 2026.08.343) and new absolute-path clients succeed.
        app.MapGet("/api/v1/client/shell/latest", Handle).AllowAnonymous()
        .Produces<ClientShellLatestResponse>(200)
        .Produces<ApiResponse<string>>(500);
        return app;
    }
}

/// <summary>契约注解用：客户端壳更新响应（与原匿名对象 JSON 等价：camelCase 属性、同字段同序）。</summary>
public sealed record ClientShellLatestResponse(
    string? WindowsVersion,
    string? WindowsUrl,
    string? AndroidVersion,
    string? AndroidUrl,
    string? ShellWindowsVersion,
    string? ShellWindowsUrl,
    string? ShellAndroidVersion,
    string? ShellAndroidUrl,
    DateTimeOffset? CheckedAt,
    string? Error);
