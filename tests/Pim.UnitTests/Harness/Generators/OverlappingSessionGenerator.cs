using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;
using Pim.Core.Invariants;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 重叠会话生成器
/// 生成同一设备同一时间段多个app同时前台的脏数据
/// 这是导致"600小时"bug的根因场景，同时支撑 S1 (INV-P16) 尺子
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

    /// <summary>
    /// S1 (INV-P16): 生成包含同类型事件区间重叠（含部分交叉与父子嵌套）的区间列表
    /// 最小可复现样例：两区间同属于一个设备与同一事件类型，且存在交叉时间段
    /// </summary>
    public static List<EventTimeSpan> GenerateS1OverlappingEvents(int seed = 42, bool includeParentChildNesting = true)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 10, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s1_test";
        const string eventType = "window";

        var list = new List<EventTimeSpan>
        {
            // 区间 A: 10:00 - 10:30
            new()
            {
                EventId = $"evt_{faker.Random.Hash(6)}_a",
                DeviceId = deviceId,
                EventType = eventType,
                StartTime = baseTime,
                EndTime = baseTime.AddMinutes(30)
            },
            // 区间 B (部分交叉重叠): 10:15 - 10:45
            new()
            {
                EventId = $"evt_{faker.Random.Hash(6)}_b",
                DeviceId = deviceId,
                EventType = eventType,
                StartTime = baseTime.AddMinutes(15),
                EndTime = baseTime.AddMinutes(45)
            }
        };

        if (includeParentChildNesting)
        {
            // 区间 C (父区间): 11:00 - 12:00
            // 区间 D (子嵌套区间): 11:10 - 11:40
            list.Add(new EventTimeSpan
            {
                EventId = $"evt_{faker.Random.Hash(6)}_c_parent",
                DeviceId = deviceId,
                EventType = eventType,
                StartTime = baseTime.AddHours(1),
                EndTime = baseTime.AddHours(2)
            });
            list.Add(new EventTimeSpan
            {
                EventId = $"evt_{faker.Random.Hash(6)}_d_child",
                DeviceId = deviceId,
                EventType = eventType,
                StartTime = baseTime.AddHours(1).AddMinutes(10),
                EndTime = baseTime.AddHours(1).AddMinutes(40)
            });
        }

        return list;
    }

    /// <summary>
    /// S1 (INV-P16): 生成纯父子嵌套的重叠区间（外层区间完全包含内层区间，同一事件类型）
    /// </summary>
    public static List<EventTimeSpan> GenerateS1NestedEvents(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 14, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s1_nested";
        const string eventType = "window";

        return new List<EventTimeSpan>
        {
            // 父区间: 14:00 - 15:00
            new()
            {
                EventId = "evt_parent",
                DeviceId = deviceId,
                EventType = eventType,
                StartTime = baseTime,
                EndTime = baseTime.AddHours(1)
            },
            // 完全嵌套子区间: 14:15 - 14:45
            new()
            {
                EventId = "evt_nested_child",
                DeviceId = deviceId,
                EventType = eventType,
                StartTime = baseTime.AddMinutes(15),
                EndTime = baseTime.AddMinutes(45)
            }
        };
    }

    /// <summary>
    /// S1 (INV-P16): 生成完全合规的无重叠事件区间列表（按时间严格顺序排列，基线绿尺子）
    /// </summary>
    public static List<EventTimeSpan> GenerateS1NormalEvents(int count = 10, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s1_normal";
        const string eventType = "window";

        var list = new List<EventTimeSpan>();
        var current = baseTime;
        for (int i = 0; i < count; i++)
        {
            var durationMinutes = faker.Random.Int(5, 25);
            var end = current.AddMinutes(durationMinutes);
            list.Add(new EventTimeSpan
            {
                EventId = $"norm_evt_{i}_{faker.Random.Hash(4)}",
                DeviceId = deviceId,
                EventType = eventType,
                StartTime = current,
                EndTime = end
            });
            // 确保下一个事件与上一个事件不重叠（间隔 1~10 分钟）
            current = end.AddMinutes(faker.Random.Int(1, 10));
        }

        return list;
    }

    /// <summary>
    /// S1 (INV-P16): 生成不同事件类型重叠（例如 window 与 web-page）——S1判据设计上同一设备不同类型允许重叠，应当放行
    /// </summary>
    public static List<EventTimeSpan> GenerateS1DifferentTypeOverlapEvents(int seed = 42)
    {
        var baseTime = new DateTime(2026, 7, 6, 16, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s1_diff_types";

        return new List<EventTimeSpan>
        {
            new()
            {
                EventId = "evt_window",
                DeviceId = deviceId,
                EventType = "window",
                StartTime = baseTime,
                EndTime = baseTime.AddMinutes(45)
            },
            new()
            {
                EventId = "evt_webpage",
                DeviceId = deviceId,
                EventType = "web-page",
                StartTime = baseTime.AddMinutes(10),
                EndTime = baseTime.AddMinutes(30)
            }
        };
    }
}
