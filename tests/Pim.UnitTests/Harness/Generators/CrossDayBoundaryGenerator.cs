using System;
using System.Collections.Generic;
using Bogus;
using Pim.Core.Invariants;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 跨天/时区边界场景生成器
/// 覆盖：跨天、业务日04:00切割、DST、0/1ms、null EndUtc
/// 现已扩展支持 S3 (INV-P18: 单日时长警戒线与硬上限) 与 S8 (INV-C19: 本地日凌晨事件跨日界一致性)
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

    /// <summary>
    /// S3 (INV-P18): 生成单日活跃时长超过警告线 (14.4h = 51,840s) 但未突破硬上限 (24h = 86,400s) 的记录
    /// 最小可复现样例：单日活跃时长 16.5 小时 (59,400秒)
    /// </summary>
    public static List<DailyActiveDuration> GenerateS3WarningViolation(int seed = 42)
    {
        return new List<DailyActiveDuration>
        {
            new()
            {
                Date = "2026-07-06",
                DeviceId = "device_s3_warn",
                ActiveDurationSeconds = 16.5 * 3600.0 // 16.5h > 14.4h
            }
        };
    }

    /// <summary>
    /// S3 (INV-P18): 生成单日活跃时长突破硬上限 (24.0h = 86,400s) 的记录（严重 bug，如并发累加导致的 600 小时 bug）
    /// 最小可复现样例：单日活跃时长 28.0 小时 (100,800秒)
    /// </summary>
    public static List<DailyActiveDuration> GenerateS3HardCapViolation(int seed = 42)
    {
        return new List<DailyActiveDuration>
        {
            new()
            {
                Date = "2026-07-06",
                DeviceId = "device_s3_hard_cap",
                ActiveDurationSeconds = 28.0 * 3600.0 // 28h > 24h
            }
        };
    }

    /// <summary>
    /// S3 (INV-P18): 生成完全合规的单日活跃时长记录 (<= 14.4h)（绿尺子基线）
    /// </summary>
    public static List<DailyActiveDuration> GenerateS3NormalDurations(int count = 7, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseDate = new DateTime(2026, 7, 1);
        const string deviceId = "device_s3_normal";

        var list = new List<DailyActiveDuration>(count);
        for (int i = 0; i < count; i++)
        {
            var hours = faker.Random.Double(2.0, 12.0);
            list.Add(new DailyActiveDuration
            {
                Date = baseDate.AddDays(i).ToString("yyyy-MM-dd"),
                DeviceId = deviceId,
                ActiveDurationSeconds = hours * 3600.0
            });
        }
        return list;
    }

    /// <summary>
    /// S8 (INV-C19): 生成本地日凌晨事件（00:00–03:59:59 跨日界），注入三层日期不一致违规
    /// 最小可复现样例：凌晨 02:30 CST，业务日属于前一日，但 QueryWindowDate 误用了自然日
    /// </summary>
    public static List<DayBoundarySample> GenerateS8EarlyMorningViolations(int seed = 42)
    {
        // 2026-07-06 02:30:00 +08:00 对应 UTC 2026-07-05 18:30:00
        var eventUtc = new DateTime(2026, 7, 5, 18, 30, 0, DateTimeKind.Utc);
        return new List<DayBoundarySample>
        {
            new()
            {
                EventId = "evt_s8_inconsistent_early_morning",
                EventTimeUtc = eventUtc,
                DataFieldDateBucket = "2026-07-05", // 正确对应 04:00 业务日
                QueryWindowDate = "2026-07-06",     // 错误：误取了本地自然日 07-06，产生不一致
                PageDisplayDate = "2026-07-05",
                TableName = "pc_tracker_events"
            }
        };
    }

    /// <summary>
    /// S8 (INV-C19): 生成本地日凌晨与白天事件，三层日期均严格遵循 04:00 业务日界（合规数据）
    /// </summary>
    public static List<DayBoundarySample> GenerateS8ConsistentSamples(int count = 6, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<DayBoundarySample>();

        // 凌晨样本：02:15 CST -> 属于前一天 2026-07-05
        var earlyUtc = new DateTime(2026, 7, 5, 18, 15, 0, DateTimeKind.Utc);
        list.Add(new DayBoundarySample
        {
            EventId = "evt_s8_consistent_early",
            EventTimeUtc = earlyUtc,
            DataFieldDateBucket = "2026-07-05",
            QueryWindowDate = "2026-07-05",
            PageDisplayDate = "2026-07-05",
            TableName = "pc_tracker_events"
        });

        // 白天样本：14:30 CST -> 属于当天 2026-07-06
        var dayUtc = new DateTime(2026, 7, 6, 6, 30, 0, DateTimeKind.Utc);
        list.Add(new DayBoundarySample
        {
            EventId = "evt_s8_consistent_day",
            EventTimeUtc = dayUtc,
            DataFieldDateBucket = "2026-07-06",
            QueryWindowDate = "2026-07-06",
            PageDisplayDate = "2026-07-06",
            TableName = "pc_tracker_events"
        });

        for (int i = 0; i < count - 2; i++)
        {
            var hour = faker.Random.Int(4, 23);
            var utcTime = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc).AddHours(hour - 8).AddMinutes(faker.Random.Int(0, 59));
            list.Add(new DayBoundarySample
            {
                EventId = $"evt_s8_norm_{i}",
                EventTimeUtc = utcTime,
                DataFieldDateBucket = "2026-07-06",
                QueryWindowDate = "2026-07-06",
                PageDisplayDate = "2026-07-06",
                TableName = "pc_tracker_events"
            });
        }

        return list;
    }

    private static string PickPackage(Faker faker)
    {
        var pkgs = new[] { "com.tencent.mobileqq", "com.tencent.mm", "com.ss.android.ugc.aweme", "com.sina.weibo", "com.alibaba.taobao" };
        return faker.PickRandom(pkgs);
    }
}
