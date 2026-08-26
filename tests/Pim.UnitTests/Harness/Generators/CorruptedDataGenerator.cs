using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 脏数据生成器
/// 生成各种异常数据场景：null值、0值、负数、极大值、格式错误等
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
}
