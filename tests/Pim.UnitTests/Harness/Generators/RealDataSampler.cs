using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Bogus;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 真库采样器 - 从生产库采样100条mobile_usage_sessions，脱敏后输出种子文件
/// 若无Docker/DB连接则回退到合成数据，保证可复现
/// 使用 new Faker().Random = new Randomizer(seed) 保证可复现
/// </summary>
public static class RealDataSampler
{
    public sealed record SampledSession(
        string AnonUserId,
        string AnonDeviceId,
        string PackageName,
        DateTimeOffset StartUtc,
        DateTimeOffset? EndUtc,
        long DurationMs,
        string QualityFlagsJson);

    private const string SeedFileRelative = "Harness/SeedData/sampled_sessions.json";

    /// <summary>
    /// 采样并写入种子文件，供后续测试复用
    /// </summary>
    public static List<SampledSession> SampleAndWrite(int count = 100, int seed = 42)
    {
        var sessions = TrySampleFromDb(count) ?? GenerateSynthetic(count, seed);
        var anonymized = Anonymize(sessions, seed);
        WriteSeedFile(anonymized);
        return anonymized;
    }

    /// <summary>
    /// 直接生成脱敏合成数据（DB不可用时回退）
    /// </summary>
    public static List<SampledSession> GenerateSynthetic(int count = 100, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = DateTimeOffset.Parse("2026-07-06T00:00:00+08:00");
        var packages = new[] { "com.tencent.mobileqq", "com.tencent.mm", "com.ss.android.ugc.aweme", "com.sina.weibo", "com.alibaba.taobao", "com.netease.cloudmusic", "com.baidu.BaiduMap", "com.autonavi.minimap" };
        var list = new List<SampledSession>();
        for (int i = 0; i < count; i++)
        {
            var start = baseTime.AddSeconds(faker.Random.Int(0, 82800));
            var durationMs = faker.Random.Long(1000, 3600000);
            var end = start.AddMilliseconds(durationMs);
            if (end > baseTime.AddDays(1)) end = baseTime.AddDays(1);
            var pkg = faker.PickRandom(packages);
            var quality = faker.Random.Bool(0.1f) ? "[\"anomalous_duration\"]" : "[]";
            list.Add(new SampledSession(
                $"user_{faker.Random.Int(1, 1000):D4}",
                $"device_{faker.Random.Int(1, 100):D3}",
                pkg,
                start,
                end,
                (long)(end - start).TotalMilliseconds,
                quality));
        }
        return Anonymize(list, seed);
    }

    /// <summary>
    /// 脱敏：替换userId/deviceId为假值，保留时间/时长/包名结构
    /// </summary>
    public static List<SampledSession> Anonymize(List<SampledSession> sessions, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var userMap = new Dictionary<string, string>();
        var deviceMap = new Dictionary<string, string>();
        string MapUser(string orig)
        {
            if (!userMap.TryGetValue(orig, out var anon))
            {
                anon = $"anon_user_{faker.Random.Int(1000, 9999)}";
                userMap[orig] = anon;
            }
            return anon;
        }
        string MapDevice(string orig)
        {
            if (!deviceMap.TryGetValue(orig, out var anon))
            {
                anon = $"anon_device_{faker.Random.Int(100, 999)}";
                deviceMap[orig] = anon;
            }
            return anon;
        }
        return sessions.Select(s => s with
        {
            AnonUserId = MapUser(s.AnonUserId),
            AnonDeviceId = MapDevice(s.AnonDeviceId)
        }).ToList();
    }

    /// <summary>
    /// 从种子文件加载（若存在）
    /// </summary>
    public static List<SampledSession> LoadSeedFile()
    {
        var path = ResolvePath();
        if (!File.Exists(path)) return new List<SampledSession>();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<SampledSession>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }

    /// <summary>
    /// 生成可迭代的 (package,start,end) 元组，供属性测试直接使用
    /// </summary>
    public static List<(string packageName, DateTimeOffset start, DateTimeOffset end)> ToTuples(List<SampledSession> sessions)
        => sessions.Where(s => s.EndUtc.HasValue).Select(s => (s.PackageName, s.StartUtc, s.EndUtc!.Value)).ToList();

    private static List<SampledSession>? TrySampleFromDb(int count)
    {
        try
        {
            var psi = new ProcessStartInfo("docker", $"exec 1Panel-postgresql-rIyE psql -U pim -d pim_prod -t -A -F\",\" -c \"SELECT user_id, device_id, package_name, start_utc, end_utc, duration_ms, quality_flags_json FROM mobile_usage_sessions ORDER BY random() LIMIT {count}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var list = new List<SampledSession>();
            foreach (var line in lines)
            {
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                if (!DateTimeOffset.TryParse(parts[3], out var start)) continue;
                DateTimeOffset? end = DateTimeOffset.TryParse(parts[4], out var e) ? e : null;
                var duration = parts.Length > 5 && long.TryParse(parts[5], out var d) ? d : (end.HasValue ? (long)(end.Value - start).TotalMilliseconds : 0);
                var quality = parts.Length > 6 ? parts[6] : "[]";
                list.Add(new SampledSession(parts[0], parts[1], parts[2], start, end, duration, quality));
            }
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSeedFile(List<SampledSession> sessions)
    {
        try
        {
            var path = ResolvePath();
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { /* ignore I/O errors in test context */ }
    }

    private static string ResolvePath()
    {
        // 尝试定位仓库根下的 tests/Pim.UnitTests/Harness/SeedData
        var cwd = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(cwd, SeedFileRelative),
            Path.Combine(cwd, "tests", "Pim.UnitTests", SeedFileRelative),
            Path.Combine(AppContext.BaseDirectory, SeedFileRelative)
        };
        foreach (var p in candidates)
        {
            var dir = Path.GetDirectoryName(p);
            if (dir != null && Directory.Exists(dir)) return p;
        }
        return candidates[0];
    }
}
