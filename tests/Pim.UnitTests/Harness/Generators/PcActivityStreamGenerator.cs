using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;
using Pim.Core.Invariants;
using Pim.UnitTests.Harness.RealDb;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// PC activity stream generator covering AFK jitter, classification rule conflict, RecordKey mapping failure.
/// 现已扩展支持 S2 (INV-P17: 超长事件三态判定) 与 S13 (INV-P22: 同设备多采集流互斥)
/// Reproducibility via new Faker().Random = new Randomizer(seed).
/// </summary>
public static class PcActivityStreamGenerator
{
    public sealed record PcActivity(
        string DeviceId,
        DateTimeOffset Timestamp,
        double DurationMs,
        string AppName,
        string? WindowTitle,
        string AfkStatus,
        string RecordKey,
        string Classification,
        string EventType);

    private static readonly string[] Apps = { "chrome.exe", "code.exe", "explorer.exe", "devenv.exe", "teams.exe", "outlook.exe", "slack.exe", "notepad.exe", "figma.exe", "spotify.exe" };
    private static readonly string[] Classifications = { "work", "communication", "entertainment", "idle", "unknown", "learning" };
    private static readonly string[] AfkStatuses = { "active", "afk", "idle" };
    private static readonly string[] EventTypes = { "window", "afk", "heartbeat" };

    /// <summary>
    /// Generate random PC activity stream.
    /// </summary>
    public static List<PcActivity> Generate(int count = 50, int seed = 42)
    {
        new Faker().Random = new Randomizer(seed);
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        for (int i = 0; i < count; i++)
        {
            var app = faker.PickRandom(Apps);
            var afk = faker.PickRandom(AfkStatuses);
            var duration = faker.Random.Double(1000, 600000);
            var ts = baseTime.AddSeconds(i * 60 + faker.Random.Int(-10, 10));
            var recordKey = $"{faker.Random.Guid():N}_{ts.ToUnixTimeSeconds()}";
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 10):D3}",
                ts,
                duration,
                app,
                faker.Lorem.Sentence(2),
                afk,
                recordKey,
                faker.PickRandom(Classifications),
                faker.PickRandom(EventTypes)));
        }
        return list;
    }

    /// <summary>
    /// Generate stream with AFK jitter: rapid active/afk flapping within seconds.
    /// </summary>
    public static List<PcActivity> GenerateWithAfkJitter(int count = 30, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        var ts = baseTime;
        string lastAfk = "active";
        for (int i = 0; i < count; i++)
        {
            // jitter: 40% chance to flip AFK within 1-5 seconds
            var jitter = faker.Random.Bool(0.4f);
            if (jitter)
            {
                lastAfk = lastAfk == "active" ? "afk" : "active";
                ts = ts.AddSeconds(faker.Random.Int(1, 5));
            }
            else
            {
                lastAfk = faker.PickRandom(AfkStatuses);
                ts = ts.AddSeconds(faker.Random.Int(10, 120));
            }
            var duration = jitter
                ? faker.Random.Double(500, 5000)
                : faker.Random.Double(10000, 300000);
            var app = faker.PickRandom(Apps);
            var recordKey = $"jitter_{faker.Random.Hash(8)}_{ts.ToUnixTimeMilliseconds()}";
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 5):D3}",
                ts,
                duration,
                app,
                faker.Lorem.Words(2).Aggregate((a, b) => a + " " + b),
                lastAfk,
                recordKey,
                lastAfk == "afk" ? "idle" : faker.PickRandom(Classifications),
                "afk"));
        }
        return list;
    }

    /// <summary>
    /// Generate stream with classification rule conflicts: same app maps to multiple classifications.
    /// </summary>
    public static List<PcActivity> GenerateWithClassificationConflict(int count = 30, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        var conflictApps = faker.Random.ListItems(Apps, 3);
        var conflictRules = new Dictionary<string, string[]>
        {
            { conflictApps[0], new[] { "work", "entertainment" } },
            { conflictApps[1], new[] { "communication", "work" } },
            { conflictApps[2], new[] { "learning", "entertainment", "work" } },
        };
        for (int i = 0; i < count; i++)
        {
            var app = faker.PickRandom(Apps);
            string classification;
            if (conflictRules.TryGetValue(app, out var opts))
            {
                classification = opts[i % opts.Length];
            }
            else
            {
                classification = faker.PickRandom(Classifications);
            }
            var ts = baseTime.AddMinutes(i * 5 + faker.Random.Int(-2, 2));
            var recordKey = $"conflict_{faker.Random.Hash(6)}_{ts.ToUnixTimeSeconds()}";
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 5):D3}",
                ts,
                faker.Random.Double(5000, 600000),
                app,
                faker.Lorem.Sentence(2),
                "active",
                recordKey,
                classification,
                "window"));
        }
        return list;
    }

    /// <summary>
    /// Generate stream with RecordKey mapping failures: duplicate, null-like, malformed keys.
    /// </summary>
    public static List<PcActivity> GenerateWithRecordKeyFailures(int count = 30, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 11, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        var sharedKey = $"shared_{faker.Random.Hash(12)}";
        for (int i = 0; i < count; i++)
        {
            var failureType = faker.Random.Int(0, 4);
            string recordKey = failureType switch
            {
                0 => sharedKey,
                1 => string.Empty,
                2 => $"malformed key with spaces {i} / {faker.Lorem.Word()}",
                3 => $"{faker.Random.Guid():N}",
                _ => $"ok_{faker.Random.Hash(8)}_{baseTime.AddMinutes(i).ToUnixTimeSeconds()}"
            };
            if (failureType == 1 && faker.Random.Bool(0.5f))
                recordKey = "null";
            if (failureType == 2 && faker.Random.Bool(0.3f))
                recordKey = $"key,with,commas,{i}";
            var ts = baseTime.AddMinutes(i * 3);
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 5):D3}",
                ts,
                faker.Random.Double(1000, 300000),
                faker.PickRandom(Apps),
                faker.Lorem.Sentence(1),
                faker.PickRandom(AfkStatuses),
                recordKey,
                faker.PickRandom(Classifications),
                faker.PickRandom(EventTypes)));
        }
        return list;
    }

    /// <summary>
    /// S2 (INV-P17): 生成超长事件三态候选列表（有输入、有媒体、什么都没有）
    /// 最小可复现样例：
    /// 态1: 有输入（键盘/鼠标 > 1次/分）——合法通过
    /// 态2: 有媒体（媒体播放或有声）——合法通过
    /// 态3: 什么都没有（疑似未收尾僵尸事件）——违规触发 INV-P17
    /// </summary>
    public static List<LongEventCandidate> GenerateS2TriStateEvents(int seed = 42, bool injectSuspectedZombie = true)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s2_test";

        var list = new List<LongEventCandidate>
        {
            // 态1: 有输入（时长 60 分钟，按键 120，鼠标 60，速率 3.0/min >= 1.0/min）
            new()
            {
                EventId = "evt_s2_has_input",
                DeviceId = deviceId,
                EventType = "window",
                StartTime = baseTime,
                EndTime = baseTime.AddMinutes(60),
                Keystrokes = 120,
                MouseClicks = 60,
                IsMediaActive = false,
                IsAudible = false,
                IsGapOrOffline = false,
                AppName = "code.exe"
            },
            // 态2: 有媒体（时长 90 分钟，输入为 0，但有声/媒体活跃）
            new()
            {
                EventId = "evt_s2_has_media",
                DeviceId = deviceId,
                EventType = "media",
                StartTime = baseTime.AddHours(2),
                EndTime = baseTime.AddHours(3).AddMinutes(30),
                Keystrokes = 0,
                MouseClicks = 0,
                IsMediaActive = true,
                IsAudible = true,
                IsGapOrOffline = false,
                AppName = "spotify.exe"
            }
        };

        if (injectSuspectedZombie)
        {
            // 态3: 什么都没有（时长 180 分钟，输入 0，媒体 false，非声明 gap -> 疑似未收尾僵尸事件）
            list.Add(new LongEventCandidate
            {
                EventId = "evt_s2_zombie_unclosed",
                DeviceId = deviceId,
                EventType = "window",
                StartTime = baseTime.AddHours(4),
                EndTime = baseTime.AddHours(7),
                Keystrokes = 0,
                MouseClicks = 0,
                IsMediaActive = false,
                IsAudible = false,
                IsGapOrOffline = false,
                AppName = "unknown_hung.exe"
            });
        }

        return list;
    }

    /// <summary>
    /// S2 (INV-P17): 生成完全合规的超长/普通事件列表（绿尺子基线）
    /// </summary>
    public static List<LongEventCandidate> GenerateS2NormalEvents(int count = 5, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s2_normal";

        var list = new List<LongEventCandidate>();
        for (int i = 0; i < count; i++)
        {
            var isOverlong = faker.Random.Bool(0.5f);
            var durationMinutes = isOverlong ? faker.Random.Int(35, 120) : faker.Random.Int(5, 29);
            var start = baseTime.AddMinutes(i * 150);
            var end = start.AddMinutes(durationMinutes);

            if (isOverlong)
            {
                // 必须具备输入证据或媒体证据
                var hasMedia = faker.Random.Bool(0.5f);
                list.Add(new LongEventCandidate
                {
                    EventId = $"norm_long_{i}",
                    DeviceId = deviceId,
                    EventType = "window",
                    StartTime = start,
                    EndTime = end,
                    Keystrokes = hasMedia ? 0 : durationMinutes * 5,
                    MouseClicks = hasMedia ? 0 : durationMinutes * 3,
                    IsMediaActive = hasMedia,
                    IsAudible = hasMedia,
                    IsGapOrOffline = false,
                    AppName = hasMedia ? "potplayer.exe" : "devenv.exe"
                });
            }
            else
            {
                list.Add(new LongEventCandidate
                {
                    EventId = $"norm_short_{i}",
                    DeviceId = deviceId,
                    EventType = "window",
                    StartTime = start,
                    EndTime = end,
                    Keystrokes = faker.Random.Int(0, 100),
                    MouseClicks = faker.Random.Int(0, 50),
                    IsMediaActive = false,
                    IsAudible = false,
                    IsGapOrOffline = false,
                    AppName = "chrome.exe"
                });
            }
        }
        return list;
    }

    /// <summary>
    /// S13 (INV-P22): 生成同设备多采集流违规心跳（同一设备、同一小时内存在多个采集器实例或相位冲突）
    /// 最小可复现样例：两个实例 ID 在同一小时内交替上报心跳
    /// </summary>
    public static List<CollectionHeartbeat> GenerateS13MultiStreamViolations(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 14, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s13_multi";

        var list = new List<CollectionHeartbeat>();
        for (int i = 0; i < 6; i++)
        {
            var ts = baseTime.AddMinutes(i * 5);
            list.Add(new CollectionHeartbeat
            {
                InstanceId = "instance_tracker_A",
                DeviceId = deviceId,
                Timestamp = ts,
                SessionId = 1001,
                PhaseOffsetSeconds = 0.0
            });
            list.Add(new CollectionHeartbeat
            {
                InstanceId = "instance_tracker_B",
                DeviceId = deviceId,
                Timestamp = ts.AddSeconds(2),
                SessionId = 1002,
                PhaseOffsetSeconds = 2.0
            });
        }
        return list;
    }

    /// <summary>
    /// S13 (INV-P22): 生成单设备单实例正常采集流心跳（绿尺子基线）
    /// </summary>
    public static List<CollectionHeartbeat> GenerateS13SingleStreamNormal(int count = 12, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 14, 0, 0, DateTimeKind.Utc);
        const string deviceId = "device_s13_single";
        const string singleInstanceId = "instance_tracker_primary";

        var list = new List<CollectionHeartbeat>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new CollectionHeartbeat
            {
                InstanceId = singleInstanceId,
                DeviceId = deviceId,
                Timestamp = baseTime.AddMinutes(i * 5),
                SessionId = 2000 + i,
                PhaseOffsetSeconds = 0.0
            });
        }
        return list;
    }

    /// <summary>
    /// Try to sample PC activity stream from DB; fallback to synthetic <see cref="Generate"/> on failure.
    /// </summary>
    public static List<PcActivity> FromDb(int seed = 42)
    {
        // 真库采样（历史上通过 `docker exec ... psql -d pim_prod` 直连生产容器）已移除：
        // 1. 测试采样不应该依赖硬编码的生产容器，也不应该把生产数据带进测试；
        // 2. 该路径对数据库错误一律回退合成数据，会把"口令错误/库不存在/权限不足"伪装成正常回退。
        // 需要真库属性的场景走 `PimDbFixture`（PIM_TEST_CONN）驱动的 RealDb 用例；
        // 这里保持确定性合成数据，保证离线与 CI 可复现。
        return Generate(50, seed);
    }

}
