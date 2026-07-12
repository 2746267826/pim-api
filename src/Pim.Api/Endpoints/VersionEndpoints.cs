namespace Pim.Api.Endpoints;

public sealed record ApiVersionResponse(string Version, IReadOnlyList<string> Capabilities);

public static class VersionEndpoints
{
    public const string MobileItemResultsV1 = "mobileItemResultsV1";
    public static IReadOnlyList<string> Capabilities { get; } = [MobileItemResultsV1];

    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/version", () =>
        {
            var version = typeof(Program).Assembly
                .GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "0.0.0(unknown)";
            return Results.Ok(new ApiVersionResponse(version, Capabilities));
        }).AllowAnonymous();
        return endpoints;
    }
}
