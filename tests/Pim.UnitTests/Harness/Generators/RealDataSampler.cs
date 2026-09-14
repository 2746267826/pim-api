using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Bogus;
using Pim.Core.Invariants;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// 真库采样器 - 从生产库采样100条mobile_usage_sessions，脱敏后输出种子文件
/// 若无Docker/DB连接则回退到合成数据，保证可复现
/// 现已扩展支持：
/// S9 (INV-C20: 覆盖率不足/汇总停摆)
/// S10 (INV-C21: 后台任务产出为0且有可处理数据)
/// S11 (INV-M21: 批次状态语义与被拒/失败条目一致性)
/// S12 (INV-M22: 派生表空/非空与在线计算声明)
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
    /// 采样并写入种子文件，供后续测试复用（默认1000行，满足离线兜底）
    /// </summary>
    public static List<SampledSession> SampleAndWrite(int count = 1000, int seed = 42)
    {
        var sessions = TrySampleFromDb(count) ?? GenerateSynthetic(count, seed);
        var anonymized = Anonymize(sessions, seed);
        WriteSeedFile(anonymized);
        return anonymized;
    }

    /// <summary>
    /// 直接生成脱敏合成数据（DB不可用时回退，默认1000行）
    /// </summary>
    public static List<SampledSession> GenerateSynthetic(int count = 1000, int seed = 42)
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

    /// <summary>
    /// S9 (INV-C20): 生成覆盖率严重不足但虚报 Normal 的信号报告（违规数据）
    /// 最小可复现样例：在线 86400 秒，有效数据仅 73440 秒（85% 覆盖率 < 95%），状态却声称 Normal
    /// </summary>
    public static CoverageSignalReport GenerateS9CoverageInsufficientReport(int seed = 42, double coverage = 0.85)
    {
        const double onlineSeconds = 86400.0;
        return new CoverageSignalReport
        {
            DeviceId = "device_s9_insufficient",
            OnlineDurationSeconds = onlineSeconds,
            ValidDataDurationSeconds = onlineSeconds * coverage,
            ReportedStatus = "Normal", // 虚报 Normal，必须触发红线
            IsDataInsufficientForDenominator = false,
            GapBreakdown = new[] { "03:00-06:00: 缺失采集" }
        };
    }

    /// <summary>
    /// S9 (INV-C20): 生成合规的覆盖率报告（覆盖率 >= 99% 或诚实上报降级状态）（绿尺子基线）
    /// </summary>
    public static CoverageSignalReport GenerateS9NormalReport(int seed = 42, bool isLowCoverageHonest = false)
    {
        const double onlineSeconds = 86400.0;
        if (isLowCoverageHonest)
        {
            return new CoverageSignalReport
            {
                DeviceId = "device_s9_honest",
                OnlineDurationSeconds = onlineSeconds,
                ValidDataDurationSeconds = onlineSeconds * 0.80, // 80% 覆盖率
                ReportedStatus = "Error",                        // 诚实上报 Error 状态，不掩盖故障
                IsDataInsufficientForDenominator = false,
                GapBreakdown = new[] { "02:00-07:00: 客户端掉线" }
            };
        }

        return new CoverageSignalReport
        {
            DeviceId = "device_s9_high",
            OnlineDurationSeconds = onlineSeconds,
            ValidDataDurationSeconds = onlineSeconds * 0.995, // 99.5% >= 99%
            ReportedStatus = "Normal",
            IsDataInsufficientForDenominator = false
        };
    }

    /// <summary>
    /// S10 (INV-C21): 生成后台任务静默失败记录（有可用输入数据，产出却为 0 行）
    /// 最小可复现样例：输入待聚合数据 200 条，输出为 0 行
    /// </summary>
    public static List<BackgroundTaskRun> GenerateS10ZeroOutputWithDataRuns(int seed = 42)
    {
        return new List<BackgroundTaskRun>
        {
            new()
            {
                TaskName = "HourlyAggregationTask",
                ExecutedAt = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc),
                AvailableDataCount = 200, // 有输入数据
                OutputCount = 0,          // 产出 0（静默死锁/空转）
                ProcessedCount = 0
            }
        };
    }

    /// <summary>
    /// S10 (INV-C21): 生成后台任务合规运行记录（有输入有产出，或无输入0产出）（绿尺子基线）
    /// </summary>
    public static List<BackgroundTaskRun> GenerateS10NormalRuns(int count = 4, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTime(2026, 7, 6, 8, 0, 0, DateTimeKind.Utc);

        var list = new List<BackgroundTaskRun>();
        for (int i = 0; i < count; i++)
        {
            var hasData = faker.Random.Bool(0.7f);
            var inputCount = hasData ? faker.Random.Int(50, 500) : 0;
            var outputCount = hasData ? (int)(inputCount * faker.Random.Double(0.8, 1.0)) : 0;
            list.Add(new BackgroundTaskRun
            {
                TaskName = "HourlyAggregationTask",
                ExecutedAt = baseTime.AddHours(i),
                AvailableDataCount = inputCount,
                OutputCount = outputCount,
                ProcessedCount = inputCount
            });
        }
        return list;
    }

    /// <summary>
    /// S11 (INV-M21): 生成批次同步状态语义冲突记录
    /// 最小可复现样例：
    /// 1. 只有被拒条目 (rejected > 0, failed == 0) 却被标记为 "failed"
    /// 2. 存在真实失败条目 (failed > 0) 却被标记为 "completed"
    /// </summary>
    public static List<BatchSyncStatusRecord> GenerateS11StatusInconsistencyRecords(int seed = 42)
    {
        return new List<BatchSyncStatusRecord>
        {
            // 冲突1: 仅被拒但标记为 failed
            new()
            {
                BatchId = "batch_s11_rejected_marked_failed",
                TotalCount = 100,
                AcceptedCount = 80,
                RejectedCount = 20,
                FailedCount = 0,
                Status = "failed"
            },
            // 冲突2: 存在真实系统级失败但标记为 completed
            new()
            {
                BatchId = "batch_s11_failed_marked_completed",
                TotalCount = 50,
                AcceptedCount = 40,
                RejectedCount = 0,
                FailedCount = 10,
                Status = "completed"
            }
        };
    }

    /// <summary>
    /// S11 (INV-M21): 生成批次状态语义合规的记录（绿尺子基线）
    /// </summary>
    public static List<BatchSyncStatusRecord> GenerateS11ConsistentRecords(int count = 4, int seed = 42)
    {
        return new List<BatchSyncStatusRecord>
        {
            // 全量成功
            new()
            {
                BatchId = "batch_s11_all_success",
                TotalCount = 100,
                AcceptedCount = 100,
                RejectedCount = 0,
                FailedCount = 0,
                Status = "completed"
            },
            // 部分被拒 -> 标记为 partial 或 completed-with-rejected
            new()
            {
                BatchId = "batch_s11_with_rejected",
                TotalCount = 100,
                AcceptedCount = 90,
                RejectedCount = 10,
                FailedCount = 0,
                Status = "completed"
            },
            // 真实失败 -> 标记为 failed
            new()
            {
                BatchId = "batch_s11_with_failure",
                TotalCount = 100,
                AcceptedCount = 60,
                RejectedCount = 0,
                FailedCount = 40,
                Status = "failed"
            }
        };
    }

    /// <summary>
    /// S12 (INV-M22): 生成派生表空但源表有数据的违规记录（且未声明在线计算）
    /// 最小可复现样例：过去 24 小时源表有 10,000 行输入，但派生表行为 0
    /// </summary>
    public static List<DerivedTableStatus> GenerateS12EmptyDerivedTableViolations(int seed = 42)
    {
        return new List<DerivedTableStatus>
        {
            new()
            {
                TableName = "daily_app_usage_summary",
                SourceDataCountLast24H = 10000,
                DerivedRowCount = 0, // 派生表为空
                IsExplicitOnlineCalculation = false,
                DocumentationNote = null
            }
        };
    }

    /// <summary>
    /// S12 (INV-M22): 生成派生表合规状态（派生表行数正常非空，或声明了在线实时计算）（绿尺子基线）
    /// </summary>
    public static List<DerivedTableStatus> GenerateS12NormalDerivedTables(int count = 3, int seed = 42)
    {
        return new List<DerivedTableStatus>
        {
            // 预聚合物理表，行数 > 0
            new()
            {
                TableName = "hourly_activity_summary",
                SourceDataCountLast24H = 8000,
                DerivedRowCount = 240,
                IsExplicitOnlineCalculation = false,
                DocumentationNote = "物理派生聚合表"
            },
            // 显式在线动态计算视图/逻辑，无需物理持久化派生行
            new()
            {
                TableName = "realtime_timeline_view",
                SourceDataCountLast24H = 5000,
                DerivedRowCount = 0,
                IsExplicitOnlineCalculation = true,
                DocumentationNote = "实时在线流式聚合计算"
            }
        };
    }

    private static List<SampledSession>? TrySampleFromDb(int count)
    {
        try
        {
            const string connStr = "Host=127.0.0.1;Database=pim;Username=opencode;Password=62f0a50bb963bb648f8e400399def95a;CommandTimeout=30";
            using var conn = new Npgsql.NpgsqlConnection(connStr);
            conn.Open();
            using var cmd = new Npgsql.NpgsqlCommand($"SELECT user_id, device_id, package_name, start_utc, end_utc, duration_ms, quality_flags_json FROM mobile_usage_sessions ORDER BY random() LIMIT {count}", conn);
            using var reader = cmd.ExecuteReader();
            var list = new List<SampledSession>();
            while (reader.Read())
            {
                var userId = reader.GetGuid(0).ToString();
                var deviceId = reader.GetString(1);
                var pkg = reader.GetString(2);
                var start = reader.GetFieldValue<DateTimeOffset>(3);
                DateTimeOffset? end = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);
                var duration = reader.IsDBNull(5) ? (end.HasValue ? (long)(end.Value - start).TotalMilliseconds : 0) : reader.GetInt64(5);
                var quality = reader.IsDBNull(6) ? "[]" : reader.GetString(6);
                list.Add(new SampledSession(userId, deviceId, pkg, start, end, duration, quality));
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
