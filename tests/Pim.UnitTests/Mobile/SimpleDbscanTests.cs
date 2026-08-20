using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class SimpleDbscanTests
{
    [Fact]
    public void Run_AssignsBorderPointToCoreNeighborhood()
    {
        // eps=15、minPts=3：(10,0) 邻域含自身+(0,0)+(20,0) 共 3 点 → 核心；
        // (0,0) 与 (20,0) 邻域各仅 2 点 < minPts → 非核心的边界点，应被核心点扩展归簇。
        var points = new[]
        {
            new SimpleDbscan.Point(0, 0, 0),
            new SimpleDbscan.Point(1, 10, 0),
            new SimpleDbscan.Point(2, 20, 0)
        };

        var result = SimpleDbscan.Run(points, eps: 15, minPts: 3);

        var cluster = Assert.Single(result.Clusters);
        Assert.Equal(new[] { 0, 1, 2 }, cluster.OrderBy(index => index).ToArray());
        Assert.Empty(result.Noise);
    }

    [Fact]
    public void Run_MinPtsOneFormsSinglePointCluster()
    {
        // eps 邻域含自身：minPts=1 时单点即核心点，自成簇。
        var points = new[] { new SimpleDbscan.Point(0, 0, 0) };

        var result = SimpleDbscan.Run(points, eps: 10, minPts: 1);

        var cluster = Assert.Single(result.Clusters);
        Assert.Equal(new[] { 0 }, cluster);
        Assert.Empty(result.Noise);
    }

    [Fact]
    public void Run_SpreadOutPointsAreAllNoise()
    {
        var points = new[]
        {
            new SimpleDbscan.Point(0, 0, 0),
            new SimpleDbscan.Point(1, 100, 0),
            new SimpleDbscan.Point(2, 200, 0)
        };

        var result = SimpleDbscan.Run(points, eps: 10, minPts: 2);

        Assert.Empty(result.Clusters);
        Assert.Equal(new[] { 0, 1, 2 }, result.Noise.OrderBy(index => index).ToArray());
    }

    [Fact]
    public void Run_DistantDenseGroupsFormSeparateClusters()
    {
        var points = new List<SimpleDbscan.Point>();
        for (var i = 0; i < 5; i++)
            points.Add(new SimpleDbscan.Point(points.Count, i * 2, 0));
        for (var i = 0; i < 5; i++)
            points.Add(new SimpleDbscan.Point(points.Count, 1000 + i * 2, 0));

        var result = SimpleDbscan.Run(points, eps: 10, minPts: 3);

        Assert.Equal(2, result.Clusters.Count);
        Assert.All(result.Clusters, cluster => Assert.Equal(5, cluster.Count));
        Assert.Empty(result.Noise);
    }
}
