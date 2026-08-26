using System;
using System.Collections.Generic;
using Bogus;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 跨天/时区边界场景生成器
/// 覆盖：跨天、业务日04:00切割、DST、0/1ms、null EndUtc
/// 使用 new Faker().Random = new Randomizer(seed) 保证可复现
/// </summary>
public static class CrossDayBoundaryGenerator
{
    /// <summary>
    /// 会话从23:59到00:01跨天（跨自然日）
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)> GenerateCrossMidnightSession(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseDate = DateTimeOffset.Parse("2026-07-06T23:59:00+08:00");
        var offsetSec = faker.Random.Int(-120, 120);
        var start = baseDate.AddSeconds(offsetSec);
        var end = start.AddSeconds(faker.Random.Int(60, 300)); // 1-5min跨天
        return new List<(string, DateTimeOffset, DateTimeOffset)>
        {
            (PickPackage(faker), start, end)
        };
    }

    /// <summary>
    /// 会话正好在04:00业务日切割点（PC业务日[04:00,次日04:00)）
    /// 生成在03:59-04:01的会话
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end, string expectedBusinessDay)> GenerateBusinessDayBoundarySessions(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<(string, DateTimeOffset, DateTimeOffset, string)>();
        var base04 = DateTimeOffset.Parse("2026-07-06T04:00:00+08:00");
        for (int i = 0; i < 5; i++)
        {
            var offset = faker.Random.Int(-600, 600);
            var start = base04.AddSeconds(offset);
            var duration = faker.Random.Int(1, 1200);
            var end = start.AddSeconds(duration);
            // business day按04:00切割，本地+08
            var local = start.ToOffset(TimeSpan.FromHours(8));
            var businessDay = local.Hour < 4 ? local.Date.AddDays(-1).ToString("yyyy-MM-dd") : local.Date.ToString("yyyy-MM-dd");
            list.Add((PickPackage(faker), start, end, businessDay));
        }
        return list;
    }

    /// <summary>
    /// DST切换日的会话（使用不含DST的Asia/Shanghai模拟：用Europe/Berlin在2026-03-29 DST日）
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)> GenerateDstSessions(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<(string, DateTimeOffset, DateTimeOffset)>();
        // Europe/Berlin DST 2026-03-29 02:00 ->03:00
        var dstDay = DateTimeOffset.Parse("2026-03-29T00:00:00+01:00");
        for (int i = 0; i < 5; i++)
        {
            var offset = faker.Random.Int(0, 86400 - 3600);
            var start = dstDay.AddSeconds(offset);
            var duration = faker.Random.Int(1, 3600);
            var end = start.AddSeconds(duration);
            // 若落在DST丢失的 02:00-03:00 区间，则跳过/平移以体现DST
            if (start.Hour == 2) start = start.AddHours(1);
            if (end.Hour == 2) end = end.AddHours(1);
            list.Add((PickPackage(faker), start, end));
        }
        return list;
    }

    /// <summary>
    /// 0毫秒时长会话
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)> GenerateZeroDurationSessions(int count = 5, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T12:00:00+08:00");
        var list = new List<(string, DateTimeOffset, DateTimeOffset)>();
        for (int i = 0; i < count; i++)
        {
            var offset = faker.Random.Int(0, 36000);
            var t = baseTime.AddSeconds(offset);
            list.Add((PickPackage(faker), t, t));
        }
        return list;
    }

    /// <summary>
    /// 1毫秒时长会话
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)> GenerateOneMillisecondSessions(int count = 5, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T12:00:00+08:00");
        var list = new List<(string, DateTimeOffset, DateTimeOffset)>();
        for (int i = 0; i < count; i++)
        {
            var offset = faker.Random.Int(0, 36000);
            var t = baseTime.AddSeconds(offset);
            list.Add((PickPackage(faker), t, t.AddMilliseconds(1)));
        }
        return list;
    }

    /// <summary>
    /// 含null EndUtc的会话（进行中的会话）
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset? end)> GenerateNullEndSessions(int count = 5, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T12:00:00+08:00");
        var list = new List<(string, DateTimeOffset, DateTimeOffset?)>();
        for (int i = 0; i < count; i++)
        {
            var offset = faker.Random.Int(0, 36000);
            var start = baseTime.AddSeconds(offset);
            DateTimeOffset? end = faker.Random.Bool(0.5f) ? null : start.AddSeconds(faker.Random.Int(1, 3600));
            list.Add((PickPackage(faker), start, end));
        }
        return list;
    }

    /// <summary>
    /// 批量生成跨天边界混合场景（每seed产生N条含上述五类边缘）
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)> GenerateMixedBoundarySessions(int count = 30, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");
        var list = new List<(string, DateTimeOffset, DateTimeOffset)>();
        for (int i = 0; i < count; i++)
        {
            var type = faker.Random.Int(0, 5);
            DateTimeOffset start, end;
            switch (type)
            {
                case 0: // 跨天
                    start = DateTimeOffset.Parse("2026-07-06T23:58:00+08:00").AddSeconds(faker.Random.Int(-120, 180));
                    end = start.AddSeconds(faker.Random.Int(60, 300));
                    break;
                case 1: // 04:00附近
                    start = DateTimeOffset.Parse("2026-07-06T04:00:00+08:00").AddSeconds(faker.Random.Int(-300, 300));
                    end = start.AddSeconds(faker.Random.Int(1, 1800));
                    break;
                case 2: // 0ms
                    start = baseTime.AddSeconds(faker.Random.Int(0, 86400));
                    end = start;
                    break;
                case 3: // 1ms
                    start = baseTime.AddSeconds(faker.Random.Int(0, 86400));
                    end = start.AddMilliseconds(1);
                    break;
                default:
                    start = baseTime.AddSeconds(faker.Random.Int(0, 82800));
                    end = start.AddSeconds(faker.Random.Int(1, 3600));
                    var dayEnd = baseTime.AddDays(1);
                    if (end > dayEnd) end = dayEnd;
                    break;
            }
            list.Add((PickPackage(faker), start, end));
        }
        return list;
    }

    private static string PickPackage(Faker faker)
    {
        var pkgs = new[] { "com.tencent.mobileqq", "com.tencent.mm", "com.ss.android.ugc.aweme", "com.sina.weibo", "com.alibaba.taobao" };
        return faker.PickRandom(pkgs);
    }

    private static TimeZoneInfo? TryGetTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return null; }
    }
}
