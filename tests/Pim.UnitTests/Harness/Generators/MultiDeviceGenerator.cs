using System;
using System.Collections.Generic;
using System.Linq;
using Bogus;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 多设备重复上报生成器
/// 覆盖：同一用户多设备、同时段双设备上报、旧设备停更、新设备无数据
/// </summary>
public static class MultiDeviceGenerator
{
    private static readonly string[] DeviceIds = { "android-main", "android-old", "android-new", "android-tablet" };
    private static readonly string[] Packages = { "com.tencent.mobileqq", "com.tencent.mm", "com.ss.android.ugc.aweme", "com.sina.weibo", "com.alibaba.taobao" };

    /// <summary>
    /// 同一用户4个设备ID（模拟重装/多机）
    /// 每个设备生成N条会话
    /// </summary>
    public static Dictionary<string, List<(string packageName, DateTimeOffset start, DateTimeOffset end)>> GenerateFourDevices(int sessionsPerDevice = 10, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");
        var result = new Dictionary<string, List<(string, DateTimeOffset, DateTimeOffset)>>();
        foreach (var device in DeviceIds)
        {
            var list = new List<(string, DateTimeOffset, DateTimeOffset)>();
            for (int i = 0; i < sessionsPerDevice; i++)
            {
                var start = baseTime.AddSeconds(faker.Random.Int(0, 82800));
                var end = start.AddSeconds(faker.Random.Int(60, 1800));
                if (end > baseTime.AddDays(1)) end = baseTime.AddDays(1);
                list.Add((faker.PickRandom(Packages), start, end));
            }
            result[device] = list;
        }
        return result;
    }

    /// <summary>
    /// 同一时间段两个设备都上报（重复数据）
    /// 两个设备在同一时间窗口内产生重叠会话
    /// </summary>
    public static Dictionary<string, List<(string packageName, DateTimeOffset start, DateTimeOffset end)>> GenerateDuplicateReporting(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T13:00:00+08:00");
        var result = new Dictionary<string, List<(string, DateTimeOffset, DateTimeOffset)>>();
        var devices = new[] { "android-main", "android-old" };
        var duration = faker.Random.Int(600, 3600);
        var end = baseTime.AddSeconds(duration);
        foreach (var device in devices)
        {
            var list = new List<(string, DateTimeOffset, DateTimeOffset)>();
            // 同一时间段完全重叠，并增加随机抖动
            for (int i = 0; i < 5; i++)
            {
                var pkg = faker.PickRandom(Packages);
                var jitterStart = baseTime.AddSeconds(faker.Random.Int(-30, 30));
                var jitterEnd = end.AddSeconds(faker.Random.Int(-30, 30));
                if (jitterEnd <= jitterStart) jitterEnd = jitterStart.AddSeconds(60);
                list.Add((pkg, jitterStart, jitterEnd));
            }
            result[device] = list;
        }
        return result;
    }

    /// <summary>
    /// 一个设备停止同步（旧设备）：前半段有数据，后半段无数据
    /// </summary>
    public static Dictionary<string, List<(string packageName, DateTimeOffset start, DateTimeOffset end)>> GenerateStaleDevice(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var dayStart = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");
        var noon = DateTimeOffset.Parse("2026-07-06T12:00:00+08:00");
        var result = new Dictionary<string, List<(string, DateTimeOffset, DateTimeOffset)>>();
        var active = new List<(string, DateTimeOffset, DateTimeOffset)>();
        var stale = new List<(string, DateTimeOffset, DateTimeOffset)>();
        for (int i = 0; i < 10; i++)
        {
            var start = dayStart.AddSeconds(faker.Random.Int(0, (int)(noon - dayStart).TotalSeconds - 600));
            var end = start.AddSeconds(faker.Random.Int(60, 600));
            active.Add((faker.PickRandom(Packages), start, end));
            stale.Add((faker.PickRandom(Packages), start, end));
        }
        // stale device只有上午数据，active全天
        for (int i = 0; i < 5; i++)
        {
            var start = noon.AddSeconds(faker.Random.Int(0, 40000));
            var end = start.AddSeconds(faker.Random.Int(60, 600));
            if (end > dayStart.AddDays(1)) end = dayStart.AddDays(1);
            active.Add((faker.PickRandom(Packages), start, end));
        }
        result["android-main"] = active;
        result["android-old"] = stale;
        return result;
    }

    /// <summary>
    /// 新设备注册但无数据（空列表）
    /// </summary>
    public static Dictionary<string, List<(string packageName, DateTimeOffset start, DateTimeOffset end)>> GenerateNewDeviceNoData(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");
        var result = new Dictionary<string, List<(string, DateTimeOffset, DateTimeOffset)>>();
        var main = new List<(string, DateTimeOffset, DateTimeOffset)>();
        for (int i = 0; i < 10; i++)
        {
            var start = baseTime.AddSeconds(faker.Random.Int(0, 82800));
            var end = start.AddSeconds(faker.Random.Int(60, 1800));
            main.Add((faker.PickRandom(Packages), start, end));
        }
        result["android-main"] = main;
        result["android-new"] = new List<(string, DateTimeOffset, DateTimeOffset)>(); // 无数据
        return result;
    }

    /// <summary>
    /// 随机多设备混合场景：100组seed循环生成
    /// </summary>
    public static Dictionary<string, List<(string packageName, DateTimeOffset start, DateTimeOffset end)>> GenerateRandomMultiDevice(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var type = faker.Random.Int(0, 3);
        return type switch
        {
            0 => GenerateFourDevices(faker.Random.Int(5, 15), seed: seed),
            1 => GenerateDuplicateReporting(seed),
            2 => GenerateStaleDevice(seed),
            _ => GenerateNewDeviceNoData(seed)
        };
    }

    /// <summary>
    /// 计算设备合并后是否数据量等于各设备之和（用于验证INV-C07）
    /// </summary>
    public static (Dictionary<string, int> preMerge, int postMerge) MergeCounts(Dictionary<string, List<(string, DateTimeOffset, DateTimeOffset)>> perDevice)
    {
        var pre = perDevice.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var total = pre.Values.Sum();
        return (pre, total);
    }
}
