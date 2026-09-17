using System.Globalization;

namespace Pim.Api.Tiles;

public readonly record struct TileCoordinate(int Z, int X, int Y)
{
    public const int MinZoom = 0;
    public const int MaxZoom = 19;

    public static bool TryParse(string? z, string? x, string? y, out TileCoordinate coordinate)
    {
        coordinate = default;
        if (!int.TryParse(z, NumberStyles.None, CultureInfo.InvariantCulture, out var zoom)
            || !int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var column)
            || !int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || zoom is < MinZoom or > MaxZoom)
        {
            return false;
        }

        var worldSize = 1L << zoom;
        if (column < 0 || row < 0 || column >= worldSize || row >= worldSize)
            return false;

        coordinate = new TileCoordinate(zoom, column, row);
        return true;
    }

    public string CacheRelativePath => Path.Combine(
        Z.ToString(CultureInfo.InvariantCulture),
        X.ToString(CultureInfo.InvariantCulture),
        $"{Y.ToString(CultureInfo.InvariantCulture)}.png");
}
