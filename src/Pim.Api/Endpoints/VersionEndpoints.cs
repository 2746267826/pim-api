using Pim.Api.Services;

namespace Pim.Api.Endpoints;

public sealed record ApiVersionResponse(
    string Version,
    IReadOnlyList<string> Capabilities,
    string? LatestVersion,
    DateTimeOffset? CheckedAt,
    string? Error,
    string? WindowsVersion = null,
    string? AndroidVersion = null,
    string? ShellWindowsVersion = null,
    string? ShellAndroidVersion = null);

public static class VersionEndpoints
{
    public const string MobileItemResultsV1 = "mobileItemResultsV1";
    public const string AndroidEmbedV1 = "androidEmbedV1";
    public static IReadOnlyList<string> Capabilities { get; } = [MobileItemResultsV1, AndroidEmbedV1];

    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/version", (GitHubReleaseService gh) =>
        {
            var version = typeof(Program).Assembly
                .GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "0.0.0(unknown)";
            var snap = gh.Snapshot;
            return Results.Ok(new ApiVersionResponse(
                version,
                Capabilities,
                snap.LatestVersion,
                snap.CheckedAt,
                snap.Error,
                snap.WindowsUrl != null ? (snap.WindowsVersion ?? snap.LatestVersion) : null,
                snap.AndroidUrl != null ? (snap.AndroidVersion ?? snap.LatestVersion) : null,
                snap.ShellWindowsUrl != null ? (snap.ShellWindowsVersion ?? snap.LatestVersion) : null,
                snap.ShellAndroidUrl != null ? (snap.ShellAndroidVersion ?? snap.LatestVersion) : null));
        }).AllowAnonymous();
        return endpoints;
    }
}
