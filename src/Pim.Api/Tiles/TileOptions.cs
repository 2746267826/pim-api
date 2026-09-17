namespace Pim.Api.Tiles;

public sealed class TileOptions
{
    public string UpstreamBaseUrl { get; set; } = "https://tile.openstreetmap.org";
    public string CacheDirectory { get; set; } = Path.Combine("/data", "pim", "cache", "tiles");
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
