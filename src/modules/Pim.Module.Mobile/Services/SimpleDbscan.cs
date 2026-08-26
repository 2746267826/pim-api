namespace Pim.Module.Mobile.Services;

/// <summary>
/// 通用二维平面 DBSCAN 聚类（静态工具）。
/// 邻域用 O(n²) 线性扫描：数据量为 GPS 点集（千级以内），不建 R-tree 已可接受；
/// 若未来数据量上到万级，可替换为网格索引而不改调用方契约。
/// </summary>
public static class SimpleDbscan
{
    public sealed record Point(int Index, double X, double Y);

    public sealed record Result(IReadOnlyList<IReadOnlyList<int>> Clusters, IReadOnlyList<int> Noise);

    /// <summary>
    /// 标准 DBSCAN：未访问点 → 取 eps 邻域（含自身）；邻域数 &gt;= minPts 为核心点，
    /// BFS 扩展簇；边界点归入首个触达簇；其余为噪声。
    /// </summary>
    public static Result Run(IReadOnlyList<Point> points, double eps, int minPts)
    {
        var count = points.Count;
        var visited = new bool[count];
        var clusterId = new int[count];
        Array.Fill(clusterId, -1);
        var clusters = new List<IReadOnlyList<int>>();

        for (var index = 0; index < count; index++)
        {
            if (visited[index])
                continue;

            visited[index] = true;
            var neighbors = RegionQuery(points, index, eps);
            if (neighbors.Count < minPts)
                continue; // 噪声：不建簇，也不归簇

            var id = clusters.Count;
            var members = new List<int>();
            var queued = new HashSet<int>(neighbors);
            var queue = new Queue<int>(neighbors);
            while (queue.Count > 0)
            {
                var candidate = queue.Dequeue();
                if (!visited[candidate])
                {
                    visited[candidate] = true;
                    var candidateNeighbors = RegionQuery(points, candidate, eps);
                    if (candidateNeighbors.Count >= minPts)
                    {
                        foreach (var neighbor in candidateNeighbors)
                        {
                            if (queued.Add(neighbor))
                                queue.Enqueue(neighbor);
                        }
                    }
                }

                if (clusterId[candidate] == -1)
                {
                    clusterId[candidate] = id;
                    members.Add(candidate);
                }
            }
            members.Sort();

            clusters.Add(members);
        }

        var noise = new List<int>();
        for (var index = 0; index < count; index++)
        {
            if (clusterId[index] == -1)
                noise.Add(index);
        }

        return new Result(clusters, noise);
    }

    private static List<int> RegionQuery(IReadOnlyList<Point> points, int index, double eps)
    {
        var point = points[index];
        var epsSquared = eps * eps;
        var result = new List<int>();
        for (var candidate = 0; candidate < points.Count; candidate++)
        {
            var dx = points[candidate].X - point.X;
            var dy = points[candidate].Y - point.Y;
            if (dx * dx + dy * dy <= epsSquared)
                result.Add(candidate);
        }

        return result;
    }
}
