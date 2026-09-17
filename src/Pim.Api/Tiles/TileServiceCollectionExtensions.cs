using Microsoft.Extensions.Options;

namespace Pim.Api.Tiles;

public static class TileServiceCollectionExtensions
{
    public static IServiceCollection AddTileServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TileOptions>()
            .Bind(configuration.GetSection("Tiles"))
            .Validate(options => Uri.TryCreate(options.UpstreamBaseUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp),
                "Tiles:UpstreamBaseUrl must be an absolute HTTP(S) URL")
            .Validate(options => options.CacheTtl > TimeSpan.Zero, "Tiles:CacheTtl must be positive")
            .Validate(options => options.RequestTimeout > TimeSpan.Zero, "Tiles:RequestTimeout must be positive");

        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient<TileService>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TileOptions>>().Value;
            client.BaseAddress = new Uri(options.UpstreamBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            client.Timeout = options.RequestTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "PimPlatform/1.0 (+https://github.com/2746267826/pim-platform)");
        });
        return services;
    }
}
