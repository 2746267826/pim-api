using Pim.Api.Tiles;
using Xunit;

namespace Pim.UnitTests.Tiles;

public sealed class TileCoordinateTests
{
    [Theory]
    [InlineData("0", "0", "0")]
    [InlineData("1", "1", "1")]
    [InlineData("10", "845", "396")]
    [InlineData("19", "524287", "524287")]
    public void TryParse_accepts_coordinates_inside_tile_world(string z, string x, string y)
    {
        Assert.True(TileCoordinate.TryParse(z, x, y, out var coordinate));
        Assert.Equal(int.Parse(z), coordinate.Z);
        Assert.Equal(int.Parse(x), coordinate.X);
        Assert.Equal(int.Parse(y), coordinate.Y);
    }

    [Theory]
    [InlineData("-1", "0", "0")]
    [InlineData("20", "0", "0")]
    [InlineData("2", "4", "0")]
    [InlineData("2", "0", "4")]
    [InlineData("2", "-1", "0")]
    [InlineData("2", "0", "-1")]
    [InlineData("abc", "0", "0")]
    [InlineData("2", "1.5", "0")]
    [InlineData("2", "0", "oops")]
    public void TryParse_rejects_invalid_or_out_of_range_coordinates(string z, string x, string y)
    {
        Assert.False(TileCoordinate.TryParse(z, x, y, out _));
    }
}
