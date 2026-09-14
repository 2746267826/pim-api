using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;
using Pim.Core.Invariants;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 脏数据生成器
/// 生成各种异常数据场景：null值、0值、负数、极大值、格式错误等
/// 现已扩展支持 S4 (INV-C18: 业务键唯一性) 与 S5 (INV-P19: 时钟可信度与时钟漂移)
/// </summary>
public static class CorruptedDataGenerator
{
    /// <summary>
    /// 生成含null/0/负数/极大值的定位点
    /// </summary>
    public static List<(double lat, double lon, double accuracy, double? altitude, DateTimeOffset timestamp)>
        GenerateCorruptedLocationPoints(int count = 100, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var points = new List<(double, double, double, double?, DateTimeOffset)>();
        var baseTime = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");

        for (int i = 0; i < count; i++)
        {
            double lat, lon, accuracy;
            double? altitude = null;

            var corruptionType = faker.Random.Int(0, 7);
            switch (corruptionType)
            {
                case 0:
                    lat = faker.Random.Double(39.8, 40.1);
                    lon = faker.Random.Double(116.2, 116.6);
                    accuracy = faker.Random.Double(5, 50);
                    altitude = faker.Random.Double(30, 60);
                    break;
                case 1:
                    lat = faker.Random.Double(39.8, 40.1);
                    lon = faker.Random.Double(116.2, 116.6);
                    accuracy = 0;
                    break;
                case 2:
                    lat = faker.Random.Double(39.8, 40.1);
                    lon = faker.Random.Double(116.2, 116.6);
                    accuracy = faker.Random.Double(5000, 50000);
                    break;
                case 3:
                    lat = 0;
                    lon = 0;
                    accuracy = faker.Random.Double(5, 50);
                    break;
                case 4:
                    lat = faker.Random.Double(39.8, 40.1);
                    lon = faker.Random.Double(116.2, 116.6);
                    accuracy = faker.Random.Double(-100, -1);
                    break;
                case 5:
                    lat = faker.Random.Double(39.8, 40.1);
                    lon = faker.Random.Double(116.2, 116.6);
                    accuracy = faker.Random.Double(5, 50);
                    altitude = faker.Random.Double(-1000, 10000);
                    break;
                case 6:
                    lat = faker.Random.Double(-90, 90);
                    lon = faker.Random.Double(-180, 180);
                    accuracy = faker.Random.Double(5, 50);
                    break;
                case 7:
                    lat = faker.Random.Double(39.8, 40.1);
                    lon = faker.Random.Double(116.2, 116.6);
                    accuracy = faker.Random.Double(5, 50);
                    break;
                default:
                    lat = faker.Random.Double(39.8, 40.1);
                    lon = faker.Random.Double(116.2, 116.6);
                    accuracy = faker.Random.Double(5, 50);
                    break;
            }

            var timestamp = baseTime.AddSeconds(i * 15);
            if (corruptionType == 7 && points.Any())
            {
                timestamp = points.Last().Item5.AddSeconds(-60);
            }

            points.Add((lat, lon, accuracy, altitude, timestamp));
        }

        return points;
    }

    /// <summary>
    /// 生成含极端值的PC会话
    /// </summary>
    public static List<(string processName, double windowDurationMs, double afkDurationMs)>
        GenerateCorruptedPcSessions(int count = 50, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var sessions = new List<(string, double, double)>();

        var processes = new[]
        {
            "chrome.exe", "code.exe", "explorer.exe", "devenv.exe",
            "teams.exe", "outlook.exe", "slack.exe", "notepad.exe"
        };

        for (int i = 0; i < count; i++)
        {
            var process = faker.PickRandom(processes);
            double windowMs, afkMs;

            var corruptionType = faker.Random.Int(0, 4);
            switch (corruptionType)
            {
                case 0:
                    windowMs = faker.Random.Double(1000, 3600000);
                    afkMs = faker.Random.Double(0, windowMs * 0.3);
                    break;
                case 1:
                    windowMs = 0;
                    afkMs = faker.Random.Double(1000, 3600000);
                    break;
                case 2:
                    windowMs = faker.Random.Double(1000, 100000);
                    afkMs = faker.Random.Double(windowMs * 2, windowMs * 10);
                    break;
                case 3:
                    windowMs = faker.Random.Double(86400000, 864000000);
                    afkMs = faker.Random.Double(0, 86400000);
                    break;
                case 4:
                    windowMs = faker.Random.Double(-10000, -1);
                    afkMs = faker.Random.Double(-10000, -1);
                    break;
                default:
                    windowMs = faker.Random.Double(1000, 3600000);
                    afkMs = faker.Random.Double(0, 100000);
                    break;
            }

            sessions.Add((process, windowMs, afkMs));
        }

        return sessions;
    }

    /// <summary>
    /// 生成含空值和异常格式的summary
    /// </summary>
    public static List<(string packageName, int hour, double totalTimeMs, string source)>
        GenerateCorruptedSummaries(int count = 100, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var summaries = new List<(string, int, double, string)>();

        var packages = new[]
        {
            "com.tencent.mobileqq", "com.tencent.mm",
            "com.ss.android.ugc.aweme", "com.sina.weibo"
        };

        for (int i = 0; i < count; i++)
        {
            var pkg = faker.PickRandom(packages);
            var hour = faker.Random.Int(0, 23);
            double totalTimeMs;
            var source = "queryUsageStats";

            var corruptionType = faker.Random.Int(0, 4);
            switch (corruptionType)
            {
                case 0:
                    totalTimeMs = faker.Random.Double(1000, 3600000);
                    break;
                case 1:
                    totalTimeMs = 3600000;
                    break;
                case 2:
                    totalTimeMs = faker.Random.Double(86400000, 8640000000);
                    break;
                case 3:
                    totalTimeMs = 0;
                    break;
                case 4:
                    totalTimeMs = faker.Random.Double(-100000, -1);
                    break;
                default:
                    totalTimeMs = faker.Random.Double(1000, 3600000);
                    break;
            }

            summaries.Add((pkg, hour, totalTimeMs, source));
        }

        return summaries;
    }

    /// <summary>
    /// S4 (INV-C18): 生成业务键重复行（覆盖 location、mobile_usage_event、pc_activity 领域）
    /// 最小可复现样例：在同领域内注入相同的 (Domain, UniqueKey) 重复键
    /// </summary>
    public static List<BusinessRecordKey> GenerateS4DuplicateKeys(int seed = 42, string domain = "all")
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s4_test";

        var list = new List<BusinessRecordKey>();

        if (domain is "all" or "location")
        {
            const string locKey = "loc_39.9042_116.4074_1720267200";
            list.Add(new BusinessRecordKey
            {
                Domain = "location",
                UniqueKey = locKey,
                DeviceId = deviceId,
                Timestamp = baseTime
            });
            list.Add(new BusinessRecordKey
            {
                Domain = "location",
                UniqueKey = locKey, // 重复键
                DeviceId = deviceId,
                Timestamp = baseTime
            });
        }

        if (domain is "all" or "mobile_usage_event")
        {
            const string mobKey = "mob_com.tencent.mm_1720267200_1";
            list.Add(new BusinessRecordKey
            {
                Domain = "mobile_usage_event",
                UniqueKey = mobKey,
                DeviceId = deviceId,
                Timestamp = baseTime.AddMinutes(5)
            });
            list.Add(new BusinessRecordKey
            {
                Domain = "mobile_usage_event",
                UniqueKey = mobKey, // 重复键
                DeviceId = deviceId,
                Timestamp = baseTime.AddMinutes(5)
            });
        }

        if (domain is "all" or "pc_activity")
        {
            const string pcKey = "pc_chrome.exe_1720267200_window";
            list.Add(new BusinessRecordKey
            {
                Domain = "pc_activity",
                UniqueKey = pcKey,
                DeviceId = deviceId,
                Timestamp = baseTime.AddMinutes(10)
            });
            list.Add(new BusinessRecordKey
            {
                Domain = "pc_activity",
                UniqueKey = pcKey, // 重复键
                DeviceId = deviceId,
                Timestamp = baseTime.AddMinutes(10)
            });
        }

        return list;
    }

    /// <summary>
    /// S4 (INV-C18): 生成严格唯一的业务键列表（绿尺子基线）
    /// </summary>
    public static List<BusinessRecordKey> GenerateS4UniqueKeys(int count = 20, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc);
        var domains = new[] { "location", "mobile_usage_event", "pc_activity" };

        var list = new List<BusinessRecordKey>(count);
        for (int i = 0; i < count; i++)
        {
            var dom = domains[i % domains.Length];
            var ts = baseTime.AddSeconds(i * 30);
            list.Add(new BusinessRecordKey
            {
                Domain = dom,
                UniqueKey = $"{dom}_{faker.Random.Hash(8)}_{ts.Ticks}",
                DeviceId = $"device_{faker.Random.Int(1, 5):D2}",
                Timestamp = ts
            });
        }
        return list;
    }

    /// <summary>
    /// S5 (INV-P19): 生成时钟超前异常事件（客户端时间超前于服务器接收时间超过 5.0 分钟容差）
    /// 最小可复现样例：客户端声称发生于 12:15:00，但服务器在 12:00:00 就收到了（超前 15 分钟）
    /// </summary>
    public static List<ClockEventItem> GenerateS5ClockSkewViolations(int seed = 42)
    {
        var serverTime = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
        return new List<ClockEventItem>
        {
            // 正常时钟（偏差仅 2 秒）
            new()
            {
                EventId = "clk_norm",
                DeviceId = "device_clk_01",
                EventTime = serverTime.AddSeconds(-2),
                ServerReceivedTime = serverTime
            },
            // 超前 15 分钟（远超 5 分钟容差）
            new()
            {
                EventId = "clk_skew_future",
                DeviceId = "device_clk_01",
                EventTime = serverTime.AddMinutes(15), // Future clock skew
                ServerReceivedTime = serverTime
            }
        };
    }

    /// <summary>
    /// S5 (INV-P19): 生成完全合规的时钟事件列表（时钟偏差严格在 5 分钟容差内）（绿尺子基线）
    /// </summary>
    public static List<ClockEventItem> GenerateS5NormalClockEvents(int count = 20, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 10, 0, 0, DateTimeKind.Utc);

        var list = new List<ClockEventItem>(count);
        for (int i = 0; i < count; i++)
        {
            var serverReceived = baseTime.AddMinutes(i * 2);
            // 真实时钟在服务器前后 0~60 秒内正常抖动（均小于 5 分钟容差）
            var skewSeconds = faker.Random.Double(-60, 60);
            var eventTime = serverReceived.AddSeconds(skewSeconds);
            list.Add(new ClockEventItem
            {
                EventId = $"clk_evt_{i}_{faker.Random.Hash(4)}",
                DeviceId = $"device_{faker.Random.Int(1, 3):D2}",
                EventTime = eventTime,
                ServerReceivedTime = serverReceived
            });
        }
        return list;
    }
}
