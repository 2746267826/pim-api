using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;
using Pim.UnitTests.Harness.RealDb;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// File corpus generator covering huge file, 0-byte, duplicate name, nested path.
/// Reproducibility via new Faker().Random = new Randomizer(seed).
/// </summary>
public static class FileCorpusGenerator
{
    public sealed record FileEntry(
        string Id,
        string Name,
        string Path,
        long SizeBytes,
        string Hash,
        string OwnerId,
        DateTimeOffset CreatedAt,
        string MimeType,
        int Depth);

    private static readonly string[] Extensions = { ".pdf", ".docx", ".xlsx", ".png", ".jpg", ".mp4", ".zip", ".txt", ".md", ".json" };
    private static readonly string[] MimeTypes = { "application/pdf", "image/png", "video/mp4", "text/plain", "application/zip", "application/json" };

    /// <summary>
    /// Generate random file corpus.
    /// </summary>
    public static List<FileEntry> Generate(int count = 50, int seed = 42)
    {
        new Faker().Random = new Randomizer(seed);
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<FileEntry>(count);
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
        for (int i = 0; i < count; i++)
        {
            var depth = faker.Random.Int(0, 4);
            var name = faker.System.FileName().Replace("/", "_").Replace("\\", "_");
            // ensure extension
            var ext = faker.PickRandom(Extensions);
            if (!name.Contains('.')) name += ext;
            var path = BuildNestedPath(faker, name, depth);
            var size = faker.Random.Long(1, 10 * 1024 * 1024);
            list.Add(new FileEntry(
                Guid.NewGuid().ToString("N"),
                name,
                path,
                size,
                faker.Random.Hash(32),
                faker.Random.Guid().ToString("N"),
                baseTime.AddDays(faker.Random.Int(0, 364)).AddSeconds(faker.Random.Int(0, 86399)),
                faker.PickRandom(MimeTypes),
                depth));
        }
        return list;
    }

    /// <summary>
    /// Generate huge files (near 5GB) and 0-byte files mixed.
    /// </summary>
    public static List<FileEntry> GenerateHugeAndEmpty(int count = 20, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<FileEntry>(count);
        var baseTime = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.FromHours(8));
        for (int i = 0; i < count; i++)
        {
            var isHuge = i % 2 == 0;
            long size = isHuge
                ? faker.Random.Long(2L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024)
                : 0L;
            // occasionally near-limit huge (4.9GB) vs small huge
            if (isHuge && faker.Random.Bool(0.3f))
                size = faker.Random.Long(100 * 1024 * 1024, 500 * 1024 * 1024);
            var name = isHuge ? $"huge_{i:D3}{faker.PickRandom(Extensions)}" : $"empty_{i:D3}{faker.PickRandom(Extensions)}";
            var depth = faker.Random.Int(0, 2);
            var path = BuildNestedPath(faker, name, depth);
            list.Add(new FileEntry(
                Guid.NewGuid().ToString("N"),
                name,
                path,
                size,
                size == 0 ? "d41d8cd98f00b204e9800998ecf8427e" : faker.Random.Hash(32),
                faker.Random.Guid().ToString("N"),
                baseTime.AddHours(faker.Random.Int(0, 720)),
                isHuge ? "application/octet-stream" : "text/plain",
                depth));
        }
        return list;
    }

    /// <summary>
    /// Generate duplicate name collisions (same filename in different folders + exact path dup).
    /// </summary>
    public static List<FileEntry> GenerateDuplicates(int count = 20, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<FileEntry>();
        var baseTime = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.FromHours(8));
        var duplicateNames = new[] { "report.pdf", "data.xlsx", "photo.jpg", "archive.zip", "notes.txt" };
        // same name different folder
        foreach (var dupName in duplicateNames)
        {
            for (int j = 0; j < 3; j++)
            {
                var depth = faker.Random.Int(1, 3);
                var path = BuildNestedPath(faker, dupName, depth);
                list.Add(new FileEntry(
                    Guid.NewGuid().ToString("N"),
                    dupName,
                    path,
                    faker.Random.Long(100, 5 * 1024 * 1024),
                    faker.Random.Hash(32),
                    faker.Random.Guid().ToString("N"),
                    baseTime.AddDays(faker.Random.Int(0, 30)),
                    faker.PickRandom(MimeTypes),
                    depth));
            }
        }
        // exact path duplicates (simulating conflict)
        var conflictPath = "/shared/conflict/report.pdf";
        for (int k = 0; k < 2; k++)
        {
            list.Add(new FileEntry(
                Guid.NewGuid().ToString("N"),
                "report.pdf",
                conflictPath,
                faker.Random.Long(1000, 100000),
                faker.Random.Hash(32),
                faker.Random.Guid().ToString("N"),
                baseTime.AddDays(k),
                "application/pdf",
                2));
        }
        // fill remainder with random if needed
        while (list.Count < count)
        {
            var extra = Generate(1, faker.Random.Int(1, 999999)).First();
            list.Add(extra);
        }
        return list.Take(count).OrderBy(_ => faker.Random.Int(0, 10000)).ToList();
    }

    /// <summary>
    /// Generate deeply nested paths (depth 5-10) to stress path handling.
    /// </summary>
    public static List<FileEntry> GenerateNestedPaths(int count = 20, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<FileEntry>(count);
        var baseTime = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.FromHours(8));
        for (int i = 0; i < count; i++)
        {
            var depth = faker.Random.Int(5, 10);
            // nested path with unicode and spaces occasionally
            var useSpace = faker.Random.Bool(0.2f);
            var name = useSpace ? $"my file {i:D3}{faker.PickRandom(Extensions)}" : $"file_{i:D3}{faker.PickRandom(Extensions)}";
            var path = BuildNestedPath(faker, name, depth);
            // inject unicode folder for some
            if (faker.Random.Bool(0.15f))
                path = path.Replace("/folder_", "/文件夹_");
            list.Add(new FileEntry(
                Guid.NewGuid().ToString("N"),
                name,
                path,
                faker.Random.Long(0, 1024 * 1024),
                faker.Random.Hash(32),
                faker.Random.Guid().ToString("N"),
                baseTime.AddHours(faker.Random.Int(0, 720)),
                faker.PickRandom(MimeTypes),
                depth));
        }
        return list;
    }

    /// <summary>
    /// Try to sample file corpus from DB; fallback to synthetic <see cref="Generate"/> on failure.
    /// </summary>
    public static List<FileEntry> FromDb(int seed = 42)
    {
        // 真库采样（历史上通过 `docker exec ... psql -d pim_prod` 直连生产容器）已移除：
        // 1. 测试采样不应该依赖硬编码的生产容器，也不应该把生产数据带进测试；
        // 2. 该路径对数据库错误一律回退合成数据，会把"口令错误/库不存在/权限不足"伪装成正常回退。
        // 需要真库属性的场景走 `PimDbFixture`（PIM_TEST_CONN）驱动的 RealDb 用例；
        // 这里保持确定性合成数据，保证离线与 CI 可复现。
        return Generate(50, seed);
    }

    private static string BuildNestedPath(Faker faker, string fileName, int depth)
    {
        if (depth <= 0) return $"/{fileName}";
        var segments = new List<string>();
        for (int d = 0; d < depth; d++)
        {
            var seg = faker.Random.Bool(0.5f) ? $"folder_{faker.Random.Int(1, 99):D2}" : faker.Lorem.Word();
            // sanitize
            seg = seg.Replace("/", "_").Replace("\\", "_").Replace(",", "_");
            segments.Add(seg);
        }
        return "/" + string.Join("/", segments) + "/" + fileName;
    }
}
