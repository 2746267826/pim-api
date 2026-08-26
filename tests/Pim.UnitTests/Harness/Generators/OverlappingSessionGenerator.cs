using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 重叠会话生成器
/// 生成同一设备同一时间段多个app同时前台的脏数据
/// 这是导致"600小时"bug的根因场景
/// </summary>
public static class OverlappingSessionGenerator
{
    private static readonly string[] SamplePackages =
    {
        "com.tencent.mobileqq",
        "com.tencent.mm",
        "com.ss.android.ugc.aweme",
        "com.sina.weibo",
        "com.alibaba.taobao",
        "com.netease.cloudmusic",
        "com.baidu.BaiduMap",
        "com.autonavi.minimap",
        "com.microsoft.office.outlook",
        "com.zhihu.android"
    };

    /// <summary>
    /// 生成N个可能重叠的会话
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)> Generate(
        int count, int maxOverlapFactor = 10, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);

        var sessions = new List<(string, DateTimeOffset, DateTimeOffset)>();
        var baseTime = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");

        var dayEnd = baseTime.AddDays(1);
        for (int i = 0; i < count; i++)
        {
            var offsetSeconds = faker.Random.Int(0, 82800);
            var durationSeconds = faker.Random.Int(1, 3600);

            var start = baseTime.AddSeconds(offsetSeconds);
            var end = start.AddSeconds(durationSeconds);

            if (sessions.Count > 0 && faker.Random.Bool(0.5f))
            {
                var existing = faker.PickRandom(sessions);
                start = existing.Item2.AddSeconds(faker.Random.Double(-300, 300));
                end = start.AddSeconds(faker.Random.Int(1, 1800));
            }

            // clamp to single business day to avoid cross-day hour aggregation artifact (>3600 per hour across days)
            if (start < baseTime) start = baseTime;
            if (end > dayEnd) end = dayEnd;
            if (end <= start) end = start.AddSeconds(faker.Random.Int(1, 60));

            var pkg = faker.PickRandom(SamplePackages);
            sessions.Add((pkg, start, end));
        }

        return sessions;
    }

    /// <summary>
    /// 生成极端重叠场景：10个app同时前台1小时
    /// 这会直接触发600小时bug
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)>
        GenerateExtremeOverlap(int overlapCount = 10)
    {
        var baseTime = DateTimeOffset.Parse("2026-07-06T13:00:00+08:00");
        var sessions = new List<(string, DateTimeOffset, DateTimeOffset)>();

        for (int i = 0; i < overlapCount; i++)
        {
            var pkg = SamplePackages[i % SamplePackages.Length];
            sessions.Add((pkg, baseTime, baseTime.AddHours(1)));
        }

        return sessions;
    }

    /// <summary>
    /// 生成跨天重叠场景：会话从23:59到00:01
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)>
        GenerateCrossDayOverlap()
    {
        var baseTime = DateTimeOffset.Parse("2026-07-06T23:59:00+08:00");
        return new List<(string, DateTimeOffset, DateTimeOffset)>
        {
            ("com.tencent.mobileqq", baseTime, baseTime.AddMinutes(2)),
            ("com.tencent.mm", baseTime.AddSeconds(-30), baseTime.AddSeconds(90)),
            ("com.ss.android.ugc.aweme", baseTime.AddSeconds(-60), baseTime.AddMinutes(3)),
        };
    }

    // FsCheck integration - uncomment when using [Property] tests
    // public static Arbitrary<List<(string, DateTimeOffset, DateTimeOffset)>> FsCheckArbitrary()
    // {
    //     var gen = Gen.Elements(new[] { 5, 10, 20, 30, 50 })
    //         .Select(count => Generate(count, maxOverlapFactor: 5));
    //     return gen.ToArbitrary();
    // }
}
