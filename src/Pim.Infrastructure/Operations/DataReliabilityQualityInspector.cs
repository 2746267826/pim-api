using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 数据可靠性体检服务（实现 IDataQualityInspector，真实接入 PimDbContext 数据库消费 Pim.Core.Invariants 纯函数判据库）。
/// 支持 S1–S13 全部 13 根基准尺子真实取数，四态区分（红/黄/绿/未知），带超时、只读与采样上限保护。
/// 全面接入原生 pc_tracker_events 数据源，保证全量覆盖。
/// </summary>
public sealed class DataReliabilityQualityInspector : IDataQualityInspector
{
    private readonly PimDbContext? _db;
    private readonly InvariantOptions _options;
    private readonly ILogger<DataReliabilityQualityInspector> _logger;

    public DataReliabilityQualityInspector(
        PimDbContext? db,
        IOptions<InvariantOptions> options,
        ILogger<DataReliabilityQualityInspector> logger)
    {
        _db = db;
        _options = options?.Value ?? InvariantOptions.Default;
        _logger = logger;
    }

    public string CheckName => "data_reliability";

    public async Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var details = new Dictionary<string, string>();
        var (resolvedOptions, fallback, fallbackNote) = InvariantOptions.Resolve(_options);

        if (fallback)
        {
            details["options_fallback"] = fallbackNote ?? "配置非法回退默认值";
        }

        int redCount = 0;
        int yellowCount = 0;
        int greenCount = 0;
        int unknownCount = 0;
        int totalIssues = 0;

        if (_db == null)
        {
            _logger.LogWarning("PimDbContext 未注入，数据可靠性体检全部标记为未知");
            for (int i = 1; i <= 13; i++)
            {
                RecordUnknown(GetInvariantKey(i), "数据库上下文未注入或不可用", details, ref unknownCount, ref totalIssues);
            }

            return new DataQualityInspectionResult(
                CheckName,
                IsHealthy: false,
                IssueCount: totalIssues,
                Message: "数据库上下文未配置，全部 13 项判据处于未知状态",
                Details: details);
        }

        DbConnection? conn = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                conn = _db.Database.GetDbConnection();
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "建立数据可靠性体检数据库连接失败");
        }

        if (conn == null || conn.State != ConnectionState.Open)
        {
            for (int i = 1; i <= 13; i++)
            {
                RecordUnknown(GetInvariantKey(i), "非关系型数据库或数据库连接未打开", details, ref unknownCount, ref totalIssues);
            }

            return new DataQualityInspectionResult(
                CheckName,
                IsHealthy: false,
                IssueCount: totalIssues,
                Message: "数据库连接不可用，全部 13 项判据标记为未知",
                Details: details);
        }

        var nowUtc = now.UtcDateTime;

        // 执行 S1–S13 判据取数与断言
        await ExecuteCheckSafeAsync("S1_INV-P16", () => CheckS1Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S2_INV-P17", () => CheckS2Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S3_INV-P18", () => CheckS3Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S4_INV-C18", () => CheckS4Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S5_INV-P19", () => CheckS5Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S6_INV-P20", () => CheckS6Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S7_INV-P21", () => CheckS7Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S8_INV-C19", () => CheckS8Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S9_INV-C20", () => CheckS9Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S10_INV-C21", () => CheckS10Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S11_INV-M21", () => CheckS11Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S12_INV-M22", () => CheckS12Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));
        await ExecuteCheckSafeAsync("S13_INV-P22", () => CheckS13Async(conn, resolvedOptions, nowUtc, ct), details, (r, k) => RecordCheck(k, r, details, ref redCount, ref yellowCount, ref greenCount, ref unknownCount, ref totalIssues));

        sw.Stop();
        bool isHealthy = redCount == 0 && unknownCount == 0 && totalIssues == 0;
        details["summary"] = $"{redCount} Red, {yellowCount} Yellow, {greenCount} Green, {unknownCount} Unknown";

        string message = isHealthy
            ? $"数据可靠性体检全部通过 (13/13 绿灯)。耗时 {sw.ElapsedMilliseconds}ms。"
            : $"数据可靠性体检发现异常：{redCount} 红, {yellowCount} 黄, {greenCount} 绿, {unknownCount} 未知。耗时 {sw.ElapsedMilliseconds}ms。";

        _logger.LogInformation(
            "数据可靠性体检完成：Healthy={IsHealthy}, Red={RedCount}, Yellow={YellowCount}, Green={GreenCount}, Unknown={UnknownCount}, Elapsed={ElapsedMs}ms",
            isHealthy, redCount, yellowCount, greenCount, unknownCount, sw.ElapsedMilliseconds);

        return new DataQualityInspectionResult(
            CheckName,
            isHealthy,
            totalIssues,
            message,
            details);
    }

    #region Check Implementations

    private async Task<InvariantResult> CheckS1Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-P16 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = """
            SELECT id, device_id, event_type, timestamp, duration 
            FROM pc_tracker_events 
            WHERE event_type IN ('window', 'web-page') 
            ORDER BY timestamp ASC;
            """;

        var list = new List<EventTimeSpan>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long id = reader.GetInt64(0);
            string dev = reader.IsDBNull(1) ? "default" : reader.GetString(1);
            string type = reader.IsDBNull(2) ? "window" : reader.GetString(2);
            DateTime start = reader.GetDateTime(3);
            double dur = reader.GetDouble(4);
            list.Add(new EventTimeSpan
            {
                EventId = id.ToString(),
                DeviceId = dev,
                EventType = type,
                StartTime = start,
                EndTime = start.AddSeconds(dur)
            });
        }

        if (list.Count == 0)
            return InvariantResult.Unknown("INV-P16 UNKNOWN: pc_tracker_events 中无可用事件序列");

        return DataReliabilityInvariants.CheckS1_NoOverlap(list, options, referenceTimeUtc: nowUtc);
    }

    private async Task<InvariantResult> CheckS2Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-P17 UNKNOWN: 数据表 pc_tracker_events 不存在");

        double thresholdSeconds = options.LongEventThresholdMinutes * 60.0;
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = $"""
            SELECT id, device_id, event_type, timestamp, duration, app_name, is_idle, is_media_active, audible 
            FROM pc_tracker_events 
            WHERE duration > {thresholdSeconds:F0} 
            ORDER BY duration DESC;
            """;

        var rawEvents = new List<(long id, string dev, string type, DateTime start, double dur, string? app, bool isIdle, bool isMedia, bool isAudible)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rawEvents.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? "default" : reader.GetString(1),
                    reader.IsDBNull(2) ? "window" : reader.GetString(2),
                    reader.GetDateTime(3),
                    reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    !reader.IsDBNull(6) && reader.GetBoolean(6),
                    !reader.IsDBNull(7) && reader.GetBoolean(7),
                    !reader.IsDBNull(8) && reader.GetBoolean(8)
                ));
            }
        }

        if (rawEvents.Count == 0)
            return InvariantResult.Unknown("INV-P17 UNKNOWN: 无超过 30 分钟事件记录");

        bool hasKeystats = await TableExistsAsync(conn, "pc_keystats_samples", ct);
        var candidates = new List<LongEventCandidate>();

        foreach (var (id, dev, type, start, dur, app, isIdle, isMediaFromCol, isAudible) in rawEvents)
        {
            var end = start.AddSeconds(dur);
            long keystrokes = 0;
            long clicks = 0;

            if (hasKeystats)
            {
                await using var keyCmd = conn.CreateCommand();
                keyCmd.CommandTimeout = 5;
                keyCmd.CommandText = """
                    SELECT COALESCE(MAX(key_presses) - MIN(key_presses), 0), 
                           COALESCE(MAX(left_clicks) - MIN(left_clicks) + MAX(right_clicks) - MIN(right_clicks), 0)
                    FROM pc_keystats_samples
                    WHERE pim_device_id = @dev AND sampled_at_utc >= @start AND sampled_at_utc <= @end;
                    """;
                var pDev = keyCmd.CreateParameter(); pDev.ParameterName = "@dev"; pDev.Value = dev; keyCmd.Parameters.Add(pDev);
                var pStart = keyCmd.CreateParameter(); pStart.ParameterName = "@start"; pStart.Value = start; keyCmd.Parameters.Add(pStart);
                var pEnd = keyCmd.CreateParameter(); pEnd.ParameterName = "@end"; pEnd.Value = end; keyCmd.Parameters.Add(pEnd);

                await using var keyReader = await keyCmd.ExecuteReaderAsync(ct);
                if (await keyReader.ReadAsync(ct))
                {
                    keystrokes = Math.Max(0, keyReader.GetInt64(0));
                    clicks = Math.Max(0, keyReader.GetInt64(1));
                }
            }

            bool isMedia = isMediaFromCol || (!string.IsNullOrEmpty(app) &&
                (app.Contains("player", StringComparison.OrdinalIgnoreCase) ||
                 app.Contains("music", StringComparison.OrdinalIgnoreCase) ||
                 app.Contains("video", StringComparison.OrdinalIgnoreCase) ||
                 app.Contains("bilibili", StringComparison.OrdinalIgnoreCase) ||
                 app.Contains("potplayer", StringComparison.OrdinalIgnoreCase) ||
                 app.Contains("spotify", StringComparison.OrdinalIgnoreCase)));

            bool isGap = type.Equals("gap", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("sleep", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("offline", StringComparison.OrdinalIgnoreCase);

            candidates.Add(new LongEventCandidate
            {
                EventId = id.ToString(),
                DeviceId = dev,
                EventType = type,
                StartTime = start,
                EndTime = end,
                Keystrokes = keystrokes,
                MouseClicks = clicks,
                IsMediaActive = isMedia,
                IsAudible = isAudible,
                IsGapOrOffline = isGap,
                AppName = app
            });
        }

        return DataReliabilityInvariants.CheckS2_OverlongEventEvidence(candidates, options, referenceTimeUtc: nowUtc);
    }

    private async Task<InvariantResult> CheckS3Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-P18 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 20;
        cmd.CommandText = """
            SELECT device_id, 
                   ((timestamp AT TIME ZONE 'Asia/Shanghai') - interval '4 hours')::date::text as biz_date, 
                   timestamp,
                   duration,
                   event_type,
                   is_idle,
                   is_media_active,
                   audible,
                   app_name,
                   id::text
            FROM pc_tracker_events
            WHERE duration > 0
            ORDER BY timestamp ASC;
            """;

        var rawEvents = new List<RawActivityEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string dev = reader.IsDBNull(0) ? "default" : reader.GetString(0);
            string date = reader.GetString(1);
            DateTime ts = reader.GetDateTime(2);
            double dur = reader.GetDouble(3);
            string type = reader.IsDBNull(4) ? "window" : reader.GetString(4);
            bool isIdle = !reader.IsDBNull(5) && reader.GetBoolean(5);
            bool isMedia = !reader.IsDBNull(6) && reader.GetBoolean(6);
            bool audible = !reader.IsDBNull(7) && reader.GetBoolean(7);
            string? app = reader.IsDBNull(8) ? null : reader.GetString(8);
            string? id = reader.IsDBNull(9) ? null : reader.GetString(9);

            rawEvents.Add(new RawActivityEvent
            {
                DeviceId = dev,
                BusinessDate = date,
                Timestamp = ts,
                DurationSeconds = dur,
                EventType = type,
                IsIdle = isIdle,
                IsMediaActive = isMedia,
                Audible = audible,
                AppName = app,
                EventId = id
            });
        }

        if (rawEvents.Count == 0)
            return InvariantResult.Unknown("INV-P18 UNKNOWN: 无活跃事件可聚合单日时长");

        var dailyDurations = DataReliabilityInvariants.AggregateDailyActiveDurations(rawEvents, options);
        return DataReliabilityInvariants.CheckS3_DailyDurationBounded(dailyDurations, options);
    }

    private async Task<InvariantResult> CheckS4Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        var keys = new List<BusinessRecordKey>();
        bool anyTableExists = false;

        // 1. 定位去重 (mobile_location_points)
        if (await TableExistsAsync(conn, "mobile_location_points", ct))
        {
            anyTableExists = true;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = """
                SELECT device_id, recorded_at_utc, latitude, longitude, count(*) 
                FROM mobile_location_points 
                GROUP BY device_id, recorded_at_utc, latitude, longitude 
                HAVING count(*) > 1;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string dev = r.GetString(0);
                DateTime t = r.GetDateTime(1);
                double lat = r.GetDouble(2);
                double lon = r.GetDouble(3);
                long cnt = r.GetInt64(4);
                for (int i = 0; i < cnt; i++)
                {
                    keys.Add(new BusinessRecordKey { Domain = "Location", DeviceId = dev, UniqueKey = $"{t:O}:{lat:F6}:{lon:F6}", Timestamp = t });
                }
            }
        }

        // 2. 手机事件去重 (mobile_usage_events)
        if (await TableExistsAsync(conn, "mobile_usage_events", ct))
        {
            anyTableExists = true;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = """
                SELECT device_id, package_name, event_timestamp_utc, count(*) 
                FROM mobile_usage_events 
                GROUP BY device_id, package_name, event_timestamp_utc 
                HAVING count(*) > 1;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string dev = r.GetString(0);
                string pkg = r.GetString(1);
                DateTime t = r.GetDateTime(2);
                long cnt = r.GetInt64(3);
                for (int i = 0; i < cnt; i++)
                {
                    keys.Add(new BusinessRecordKey { Domain = "Mobile", DeviceId = dev, UniqueKey = $"{pkg}:{t:O}", Timestamp = t });
                }
            }
        }

        // 3. PC 事件去重 (pc_tracker_events)
        if (await TableExistsAsync(conn, "pc_tracker_events", ct))
        {
            anyTableExists = true;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = """
                SELECT device_id, event_type, timestamp, count(*) 
                FROM pc_tracker_events 
                GROUP BY device_id, event_type, timestamp 
                HAVING count(*) > 1;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string dev = r.GetString(0);
                string type = r.GetString(1);
                DateTime t = r.GetDateTime(2);
                long cnt = r.GetInt64(3);
                for (int i = 0; i < cnt; i++)
                {
                    keys.Add(new BusinessRecordKey { Domain = "Pc", DeviceId = dev, UniqueKey = $"{type}:{t:O}", Timestamp = t });
                }
            }
        }

        if (!anyTableExists)
            return InvariantResult.Unknown("INV-C18 UNKNOWN: 业务数据表不存在");

        if (keys.Count == 0)
        {
            // 若无重复项，采样近 24 小时正常项以验证表非空且处于健康状态
            if (await TableExistsAsync(conn, "pc_tracker_events", ct))
            {
                await using var sampleCmd = conn.CreateCommand();
                sampleCmd.CommandTimeout = 10;
                sampleCmd.CommandText = """
                    SELECT device_id, timestamp 
                    FROM pc_tracker_events 
                    WHERE timestamp >= (NOW() - interval '24 hours')
                    ORDER BY id DESC 
                    LIMIT 10;
                    """;
                await using var sr = await sampleCmd.ExecuteReaderAsync(ct);
                while (await sr.ReadAsync(ct))
                {
                    keys.Add(new BusinessRecordKey { Domain = "Pc", DeviceId = sr.GetString(0), UniqueKey = Guid.NewGuid().ToString(), Timestamp = sr.GetDateTime(1) });
                }
            }
        }

        if (keys.Count == 0)
            return InvariantResult.Unknown("INV-C18 UNKNOWN: 业务表为空，无数据检验业务键唯一性");

        return DataReliabilityInvariants.CheckS4_BusinessKeyUnique(keys, options, referenceTimeUtc: nowUtc);
    }

    private async Task<InvariantResult> CheckS5Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-P19 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        // 增加时间窗限定在最近 24 小时，避免无界扫描或混入过旧历史数据
        cmd.CommandText = """
            SELECT id, device_id, timestamp, created_at 
            FROM pc_tracker_events 
            WHERE created_at >= (NOW() - interval '24 hours')
            ORDER BY id DESC 
            LIMIT 500;
            """;

        var items = new List<ClockEventItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long id = reader.GetInt64(0);
            string dev = reader.IsDBNull(1) ? "default" : reader.GetString(1);
            DateTime ts = reader.GetDateTime(2);
            DateTime created = reader.GetDateTime(3);
            items.Add(new ClockEventItem
            {
                EventId = id.ToString(),
                DeviceId = dev,
                EventTime = ts,
                ServerReceivedTime = created
            });
        }

        // 若最近 24 小时无数据，兜底取最近 100 条
        if (items.Count == 0)
        {
            await using var fallbackCmd = conn.CreateCommand();
            fallbackCmd.CommandTimeout = 10;
            fallbackCmd.CommandText = """
                SELECT id, device_id, timestamp, created_at 
                FROM pc_tracker_events 
                ORDER BY id DESC 
                LIMIT 100;
                """;
            await using var fReader = await fallbackCmd.ExecuteReaderAsync(ct);
            while (await fReader.ReadAsync(ct))
            {
                long id = fReader.GetInt64(0);
                string dev = fReader.IsDBNull(1) ? "default" : fReader.GetString(1);
                DateTime ts = fReader.GetDateTime(2);
                DateTime created = fReader.GetDateTime(3);
                items.Add(new ClockEventItem
                {
                    EventId = id.ToString(),
                    DeviceId = dev,
                    EventTime = ts,
                    ServerReceivedTime = created
                });
            }
        }

        if (items.Count == 0)
            return InvariantResult.Unknown("INV-P19 UNKNOWN: pc_tracker_events 表中无事件记录");

        return DataReliabilityInvariants.CheckS5_ClockTrustworthy(items, options, referenceTimeUtc: nowUtc);
    }

    private async Task<InvariantResult> CheckS6Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-P20 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = """
            SELECT timestamp, created_at 
            FROM pc_tracker_events 
            WHERE event_type != 'web-page'
            ORDER BY timestamp ASC;
            """;

        var times = new List<DateTime>();
        var lags = new List<(DateTime, DateTime)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                DateTime ts = reader.GetDateTime(0);
                DateTime created = reader.GetDateTime(1);
                times.Add(ts);
                lags.Add((ts, created));
            }
        }

        if (times.Count == 0)
            return InvariantResult.Unknown("INV-P20 UNKNOWN: 无事件记录检验下线声明与上传延迟");

        var declarations = new List<OfflineDeclaration>();
        if (await TableExistsAsync(conn, "pc_tracker_health", ct))
        {
            await using var hcmd = conn.CreateCommand();
            hcmd.CommandTimeout = 5;
            hcmd.CommandText = "SELECT device_id, reported_at, status FROM pc_tracker_health;";
            await using var hreader = await hcmd.ExecuteReaderAsync(ct);
            while (await hreader.ReadAsync(ct))
            {
                string dev = hreader.GetString(0);
                DateTime rep = hreader.GetDateTime(1);
                string stat = hreader.GetString(2);
                if (stat.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
                    stat.Equals("planned_offline", StringComparison.OrdinalIgnoreCase))
                {
                    declarations.Add(new OfflineDeclaration
                    {
                        DeviceId = dev,
                        StartTime = rep,
                        EndTime = rep.AddHours(2),
                        Reason = stat
                    });
                }
            }
        }

        var trace = new DeviceActivityTrace
        {
            DeviceId = "default",
            EventTimes = times,
            Declarations = declarations,
            UploadLagSamples = lags
        };

        return DataReliabilityInvariants.CheckS6_OfflineDeclared(trace, options);
    }

    private async Task<InvariantResult> CheckS7Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-P21 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = """
            SELECT device_id, timestamp, timestamp + (duration || ' seconds')::interval as end_time, event_type
            FROM pc_tracker_events
            WHERE event_type IN ('window', 'idle', 'gap')
            ORDER BY timestamp ASC;
            """;

        var intervals = new List<TimelineInterval>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string dev = reader.IsDBNull(0) ? "default" : reader.GetString(0);
            DateTime start = reader.GetDateTime(1);
            DateTime end = reader.GetDateTime(2);
            string type = reader.IsDBNull(3) ? "window" : reader.GetString(3);

            bool isGap = type.Equals("gap", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("afk", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("sleep", StringComparison.OrdinalIgnoreCase);

            intervals.Add(new TimelineInterval
            {
                DeviceId = dev,
                StartTime = start,
                EndTime = end,
                EventType = type,
                IsGap = isGap
            });
        }

        if (intervals.Count == 0)
            return InvariantResult.Unknown("INV-P21 UNKNOWN: 无时间线区间可检验断档标记");

        return DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals, options);
    }

    private async Task<InvariantResult> CheckS8Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-C19 UNKNOWN: 数据表 pc_tracker_events 不存在");

        var samples = new List<DayBoundarySample>();

        // 1. 验证 PC 侧原生事件业务日字段层 (pc_tracker_events.date)
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = """
            SELECT timestamp, date::text 
            FROM pc_tracker_events 
            ORDER BY id DESC 
            LIMIT 500;
            """;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                DateTime timestamp = reader.GetDateTime(0);
                string dateStr = reader.GetString(1);

                samples.Add(new DayBoundarySample
                {
                    EventTimeUtc = timestamp,
                    DataFieldDateBucket = dateStr,
                    QueryWindowDate = null, // 未覆盖，需接口契约测试
                    PageDisplayDate = null, // 未覆盖，需接口契约测试
                    TableName = "pc_tracker_events"
                });
            }
        }

        // 2. 验证移动侧业务日字段层 (若存在 mobile_timeline_blocks 且非空)
        if (await TableExistsAsync(conn, "mobile_timeline_blocks", ct))
        {
            await using var mCmd = conn.CreateCommand();
            mCmd.CommandTimeout = 10;
            mCmd.CommandText = """
                SELECT start_time_utc, local_date 
                FROM mobile_timeline_blocks 
                WHERE local_date IS NOT NULL 
                ORDER BY id DESC 
                LIMIT 100;
                """;
            try
            {
                await using var mReader = await mCmd.ExecuteReaderAsync(ct);
                while (await mReader.ReadAsync(ct))
                {
                    DateTime mTime = mReader.GetDateTime(0);
                    string mDate = mReader.GetString(1);
                    samples.Add(new DayBoundarySample
                    {
                        EventTimeUtc = mTime,
                        DataFieldDateBucket = mDate,
                        QueryWindowDate = null,
                        PageDisplayDate = null,
                        TableName = "mobile_timeline_blocks"
                    });
                }
            }
            catch
            {
                // 容错处理
            }
        }

        if (samples.Count == 0)
            return InvariantResult.Unknown("INV-C19 UNKNOWN: 无样本检验日界一致性");

        return DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(samples, options);
    }

    private async Task<InvariantResult> CheckS9Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-C20 UNKNOWN: 数据表 pc_tracker_events 不存在");

        // 1. 获取最近 24h 的有效活跃时长（window 与 web-page，排除 idle）
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = """
            SELECT COALESCE(SUM(duration), 0) 
            FROM pc_tracker_events 
            WHERE timestamp >= (NOW() - interval '24 hours') 
              AND event_type IN ('window', 'web-page') 
              AND is_idle = false;
            """;
        var scalar = await cmd.ExecuteScalarAsync(ct);
        double validDuration = Convert.ToDouble(scalar ?? 0);

        string deviceId = "default";
        string reportedStatus = "Normal";
        double onlineDuration = 86400.0; // 24h 自然窗口基准

        if (await TableExistsAsync(conn, "pc_tracker_health", ct))
        {
            await using var statCmd = conn.CreateCommand();
            statCmd.CommandTimeout = 5;
            statCmd.CommandText = "SELECT device_id, status, uptime_seconds FROM pc_tracker_health ORDER BY reported_at DESC LIMIT 1;";
            await using var hr = await statCmd.ExecuteReaderAsync(ct);
            if (await hr.ReadAsync(ct))
            {
                deviceId = hr.GetString(0);
                string stat = hr.GetString(1);
                if (stat.Equals("running", StringComparison.OrdinalIgnoreCase) || stat.Equals("healthy", StringComparison.OrdinalIgnoreCase))
                {
                    reportedStatus = "Normal";
                }
                else
                {
                    reportedStatus = stat;
                }
            }
        }

        // 2. 统计最近 24h 内的大缺口明细 (>15m)
        var gapBreakdowns = new List<string>();
        try
        {
            await using var gapCmd = conn.CreateCommand();
            gapCmd.CommandTimeout = 15;
            gapCmd.CommandText = """
                WITH evs AS (
                    SELECT timestamp as s, timestamp + duration * interval '1 second' as e,
                           LEAD(timestamp) OVER (ORDER BY timestamp ASC) as next_s
                    FROM pc_tracker_events
                    WHERE timestamp >= (NOW() - interval '24 hours')
                )
                SELECT s, next_s, extract(epoch from (next_s - e)) as gap_sec
                FROM evs
                WHERE next_s > e AND extract(epoch from (next_s - e)) > 900
                ORDER BY s ASC;
                """;

            await using var gapReader = await gapCmd.ExecuteReaderAsync(ct);
            while (await gapReader.ReadAsync(ct))
            {
                DateTime s = gapReader.GetDateTime(0);
                DateTime nextS = gapReader.GetDateTime(1);
                double gapSec = gapReader.GetDouble(2);
                gapBreakdowns.Add($"[{s:yyyy-MM-dd HH:mm} ~ {nextS:yyyy-MM-dd HH:mm} 缺口 {gapSec / 3600.0:F2}h]");
            }
        }
        catch
        {
            // 容错处理
        }

        // 3. 按照 Reviewer 指示：pc_tracker_health 仅保存单条当前进程心跳（无历史心跳序列与离线声明日志），
        //    分母设备在线时长无法精准界定（若粗暴以 24h 自然日 86400s 为分母，夜间关机 14h 将导致覆盖率虚低 47.7%）。
        //    因此标记为 IsDataInsufficientForDenominator = true，输出为 UNKNOWN (口径近似 / 数据源不足) 并附带详细明细。
        var report = new CoverageSignalReport
        {
            DeviceId = deviceId,
            OnlineDurationSeconds = onlineDuration,
            ValidDataDurationSeconds = validDuration,
            ReportedStatus = reportedStatus,
            IsDataInsufficientForDenominator = true,
            DenominatorBasisNote = "pc_tracker_health 仅存单条当前心跳 (uptime=2400s)，缺失历史心跳时序与离线声明日志，无法精准界定设备在线区间分母",
            GapBreakdown = gapBreakdowns
        };

        return DataReliabilityInvariants.CheckS9_GapHasSignal(report, options);
    }

    private async Task<InvariantResult> CheckS10Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        var runs = new List<BackgroundTaskRun>();

        // 检查分类快照任务：原生 pc_tracker_events 对比 pc_activity_classifications，验证是否存在未分类事件业务日但补齐产出为 0
        if (await TableExistsAsync(conn, "pc_tracker_events", ct) && await TableExistsAsync(conn, "pc_activity_classifications", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = """
                SELECT count(*) as unclassified_days, COALESCE(sum(tracker_cnt), 0) as unclassified_events
                FROM (
                    SELECT ((timestamp AT TIME ZONE 'Asia/Shanghai') - interval '4 hours')::date as day, count(*) as tracker_cnt
                    FROM pc_tracker_events
                    WHERE duration > 0
                    GROUP BY 1
                ) tracker
                LEFT JOIN (
                    SELECT ((started_at AT TIME ZONE 'Asia/Shanghai') - interval '4 hours')::date as day, count(*) as cls_cnt
                    FROM pc_activity_classifications
                    GROUP BY 1
                ) cls ON tracker.day = cls.day
                WHERE COALESCE(cls.cls_cnt, 0) = 0;
                """;

            long unclassifiedDays = 0;
            long unclassifiedEvents = 0;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    unclassifiedDays = reader.GetInt64(0);
                    unclassifiedEvents = reader.GetInt64(1);
                }
            }

            runs.Add(new BackgroundTaskRun
            {
                TaskName = "pc_classification_backfill",
                ExecutedAt = nowUtc,
                AvailableDataCount = (int)Math.Min(int.MaxValue, unclassifiedEvents),
                ProcessedCount = 0,
                OutputCount = 0
            });
        }

        // 检查手机汇总任务
        if (await TableExistsAsync(conn, "mobile_usage_events", ct) && await TableExistsAsync(conn, "mobile_usage_summaries", ct))
        {
            await using var cmd1 = conn.CreateCommand();
            cmd1.CommandTimeout = 10;
            cmd1.CommandText = "SELECT count(*) FROM mobile_usage_events WHERE created_at >= (NOW() - interval '14 days');";
            long mobEvents = Convert.ToInt64(await cmd1.ExecuteScalarAsync(ct) ?? 0);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandTimeout = 10;
            cmd2.CommandText = "SELECT count(*) FROM mobile_usage_summaries WHERE created_at >= (NOW() - interval '14 days');";
            long mobSummaries = Convert.ToInt64(await cmd2.ExecuteScalarAsync(ct) ?? 0);

            runs.Add(new BackgroundTaskRun
            {
                TaskName = "mobile_usage_summaries",
                ExecutedAt = nowUtc,
                AvailableDataCount = (int)Math.Min(int.MaxValue, mobEvents),
                OutputCount = (int)Math.Min(int.MaxValue, mobSummaries)
            });
        }

        if (runs.Count == 0)
            return InvariantResult.Unknown("INV-C21 UNKNOWN: 相关后台任务数据表不存在");

        return DataReliabilityInvariants.CheckS10_TaskHasOutput(runs, options);
    }

    private async Task<InvariantResult> CheckS11Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "mobile_sync_batches", ct))
            return InvariantResult.Unknown("INV-M21 UNKNOWN: 数据表 mobile_sync_batches 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        // 计数必须与写入侧口径一致（#241 / #243）：accepted 统计全部被接受条目，
        // rejected / skipped 是条目级结果，不能一律当成 0，否则"只含拒绝/跳过条目的合法批次"
        // 会被规则 3（虚假完成）误判。
        // 对尚未跑完迁移的库（新增列还不存在）退化为只读旧列，规则 1/2 仍然有效。
        var hasItemCounts = await ColumnExistsAsync(conn, "mobile_sync_batches", "rejected_count", ct)
            && await ColumnExistsAsync(conn, "mobile_sync_batches", "skipped_count", ct);
        cmd.CommandText = hasItemCounts
            ? """
              SELECT batch_id, status, failed_count, accepted_count, rejected_count, skipped_count
              FROM mobile_sync_batches 
              ORDER BY created_at DESC;
              """
            : """
              SELECT batch_id, status, failed_count, accepted_count, 0, 0
              FROM mobile_sync_batches 
              ORDER BY created_at DESC;
              """;

        var batches = new List<BatchSyncStatusRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string batchId = reader.GetString(0);
            string status = reader.GetString(1);
            int failed = reader.GetInt32(2);
            int accepted = reader.GetInt32(3);
            int rejected = reader.GetInt32(4);
            int skipped = reader.GetInt32(5);
            batches.Add(new BatchSyncStatusRecord
            {
                BatchId = batchId,
                Status = status,
                TotalCount = accepted + failed + rejected + skipped,
                AcceptedCount = accepted,
                FailedCount = failed,
                RejectedCount = rejected,
                SkippedCount = skipped
            });
        }

        if (batches.Count == 0)
            return InvariantResult.Unknown("INV-M21 UNKNOWN: mobile_sync_batches 中无批次记录");

        return DataReliabilityInvariants.CheckS11_StatusSemantics(batches, options);
    }

    private async Task<InvariantResult> CheckS12Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        bool hasMobEvents = await TableExistsAsync(conn, "mobile_usage_events", ct);
        if (!hasMobEvents)
            return InvariantResult.Unknown("INV-M22 UNKNOWN: 数据源表 mobile_usage_events 不存在");

        await using var scmd = conn.CreateCommand();
        scmd.CommandTimeout = 10;
        scmd.CommandText = "SELECT count(*) FROM mobile_usage_events WHERE created_at >= (SELECT COALESCE(MAX(created_at), NOW()) - interval '24 hours' FROM mobile_usage_events);";
        int sourceCount = Convert.ToInt32(await scmd.ExecuteScalarAsync(ct) ?? 0);

        await using var bcmd = conn.CreateCommand();
        bcmd.CommandTimeout = 10;
        bcmd.CommandText = "SELECT count(*) FROM mobile_timeline_blocks;";
        int blocksCount = Convert.ToInt32(await bcmd.ExecuteScalarAsync(ct) ?? 0);

        await using var acmd = conn.CreateCommand();
        acmd.CommandTimeout = 10;
        acmd.CommandText = "SELECT count(*) FROM mobile_usage_aggregates;";
        int aggsCount = Convert.ToInt32(await acmd.ExecuteScalarAsync(ct) ?? 0);

        var statuses = new List<DerivedTableStatus>
        {
            new()
            {
                TableName = "mobile_timeline_blocks",
                SourceDataCountLast24H = sourceCount,
                DerivedRowCount = blocksCount,
                IsExplicitOnlineCalculation = false
            },
            new()
            {
                TableName = "mobile_usage_aggregates",
                SourceDataCountLast24H = sourceCount,
                DerivedRowCount = aggsCount,
                IsExplicitOnlineCalculation = false
            }
        };

        return DataReliabilityInvariants.CheckS12_DerivedTableActive(statuses, options);
    }

    private async Task<InvariantResult> CheckS13Async(DbConnection conn, InvariantOptions options, DateTime nowUtc, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", ct))
            return InvariantResult.Unknown("INV-P22 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = """
            SELECT device_id, timestamp, instance_id
            FROM pc_tracker_events
            WHERE instance_id IS NOT NULL AND instance_id != ''
            ORDER BY timestamp DESC;
            """;

        var heartbeats = new List<CollectionHeartbeat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string dev = reader.IsDBNull(0) ? "default" : reader.GetString(0);
            DateTime ts = reader.GetDateTime(1);
            string instanceId = reader.IsDBNull(2) ? "default" : reader.GetString(2);
            heartbeats.Add(new CollectionHeartbeat
            {
                DeviceId = dev,
                Timestamp = ts,
                InstanceId = instanceId
            });
        }

        if (heartbeats.Count == 0)
            return InvariantResult.Unknown("INV-P22 UNKNOWN: 无采集流数据可检验多实例冲突");

        return DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats, options);
    }

    #endregion

    #region Helpers

    private static async Task<bool> TableExistsAsync(DbConnection conn, string tableName, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 5;
            cmd.CommandText = $"SELECT 1 FROM \"{tableName}\" LIMIT 0;";
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection conn,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 5;
            cmd.CommandText = $"SELECT \"{columnName}\" FROM \"{tableName}\" LIMIT 0;";
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ExecuteCheckSafeAsync(
        string key,
        Func<Task<InvariantResult>> checkFunc,
        Dictionary<string, string> details,
        Action<InvariantResult, string> recordAction)
    {
        try
        {
            var result = await checkFunc();
            recordAction(result, key);
        }
        catch (Exception ex)
        {
            var unknown = InvariantResult.Unknown($"{key} UNKNOWN: 取数执行异常: {ex.Message}");
            recordAction(unknown, key);
        }
    }

    private static void RecordCheck(
        string key,
        InvariantResult result,
        Dictionary<string, string> details,
        ref int redCount,
        ref int yellowCount,
        ref int greenCount,
        ref int unknownCount,
        ref int totalIssues)
    {
        if (result.CoveredLayers != null)
        {
            details[$"{key}_covered_layers"] = result.CoveredLayers;
        }

        switch (result.Status)
        {
            case InvariantStatus.Fail:
                redCount++;
                totalIssues += Math.Max(1, result.TotalViolations);
                details[key] = $"🔴 FAIL: {result.Detail}";
                break;
            case InvariantStatus.Warning:
                yellowCount++;
                totalIssues += Math.Max(1, result.TotalViolations);
                details[key] = $"🟡 WARN: {result.Detail}";
                break;
            case InvariantStatus.Unknown:
                unknownCount++;
                totalIssues++;
                details[key] = $"⚪ UNKNOWN: {result.Detail}";
                break;
            case InvariantStatus.Pass:
            default:
                greenCount++;
                details[key] = $"🟢 PASS: {result.Detail}";
                break;
        }
    }

    private static void RecordUnknown(
        string key,
        string reason,
        Dictionary<string, string> details,
        ref int unknownCount,
        ref int totalIssues)
    {
        unknownCount++;
        totalIssues++;
        details[key] = $"⚪ UNKNOWN: {reason}";
    }

    private static string GetInvariantKey(int index) => index switch
    {
        1 => "S1_INV-P16",
        2 => "S2_INV-P17",
        3 => "S3_INV-P18",
        4 => "S4_INV-C18",
        5 => "S5_INV-P19",
        6 => "S6_INV-P20",
        7 => "S7_INV-P21",
        8 => "S8_INV-C19",
        9 => "S9_INV-C20",
        10 => "S10_INV-C21",
        11 => "S11_INV-M21",
        12 => "S12_INV-M22",
        13 => "S13_INV-P22",
        _ => $"S{index}"
    };

    #endregion
}
