using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// 真库回放 Fixture - 直连 pim 库，SELECT 采样，提供给 Harness 属性测试
/// 连接失败时 Skip 而非 Fail，拷库后应100%成功
/// </summary>
public sealed class PimDbFixture : IAsyncLifetime
{
    // 只读环境变量，不内置口令；未设置时视为不可用（用例自行跳过）。
    private static string? ConnStr => Environment.GetEnvironmentVariable("PIM_TEST_CONN");
    private NpgsqlConnection? _conn;
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        var connStr = ConnStr;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            IsAvailable = false;
            System.Console.WriteLine("[PimDbFixture] PIM_TEST_CONN 未设置，跳过 RealDb 回放用例。");
            return;
        }

        try
        {
            _conn = new NpgsqlConnection(connStr);
            await _conn.OpenAsync();
            // 探活：查 mobile_usage_sessions 至少1条
            await using var cmd = new NpgsqlCommand("SELECT 1 FROM mobile_usage_sessions LIMIT 1", _conn);
            await cmd.ExecuteScalarAsync();
            IsAvailable = true;
        }
        catch (Exception ex) when (RealDbTestConnection.IsServerUnreachable(ex))
        {
            // 只有"服务器不可达"才是环境缺失：让用例跳过。
            IsAvailable = false;
            System.Console.WriteLine($"[PimDbFixture] DB unreachable, will Skip RealDb tests: {ex.Message}");
        }
        // 其余数据库异常（28P01 口令错误、3D000 库不存在、42501 权限不足、缺表…）不捕获：
        // 配置错误必须让用例失败，不能被伪装成"跳过的回放"（评审第六轮 Important）。
        finally
        {
            if (!IsAvailable && _conn is not null)
            {
                await _conn.DisposeAsync();
                _conn = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_conn != null)
        {
            await _conn.CloseAsync();
            await _conn.DisposeAsync();
        }
    }

    public NpgsqlConnection RequireConnection()
    {
        if (!IsAvailable || _conn == null)
            throw new SkipException("RealDb unavailable, skipping test");
        return _conn;
    }

    // ========== 采样 helpers ==========

    public async Task<List<MobileUsageSessionRow>> SampleSessions(int n, DateOnly? day = null)
    {
        var conn = RequireConnection();
        var sql = day.HasValue
            ? "SELECT user_id, device_id, package_name, start_utc, end_utc, duration_ms, quality_flags_json FROM mobile_usage_sessions WHERE start_utc::date = @day ORDER BY random() LIMIT @n"
            : "SELECT user_id, device_id, package_name, start_utc, end_utc, duration_ms, quality_flags_json FROM mobile_usage_sessions ORDER BY random() LIMIT @n";
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (day.HasValue) cmd.Parameters.AddWithValue("day", day.Value.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("n", n);
        var list = new List<MobileUsageSessionRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MobileUsageSessionRow(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                reader.IsDBNull(6) ? "[]" : reader.GetString(6)
            ));
        }
        return list;
    }

    public async Task<List<PcAwEventRow>> SamplePcEvents(int n, DateOnly? day = null)
    {
        var conn = RequireConnection();
        var sql = day.HasValue
            ? "SELECT device_id, timestamp, duration, event_type, app_name, window_title, afk_status FROM pc_aw_events WHERE timestamp::date = @day ORDER BY random() LIMIT @n"
            : "SELECT device_id, timestamp, duration, event_type, app_name, window_title, afk_status FROM pc_aw_events ORDER BY random() LIMIT @n";
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (day.HasValue) cmd.Parameters.AddWithValue("day", day.Value.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("n", n);
        var list = new List<PcAwEventRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new PcAwEventRow(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetDouble(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)
            ));
        }
        return list;
    }

    public async Task<List<MobileLocationPointRow>> SampleLocationPoints(int n, DateOnly? day = null)
    {
        var conn = RequireConnection();
        var sql = day.HasValue
            ? "SELECT user_id, device_id, recorded_at_utc, latitude, longitude, horizontal_accuracy_meters, provider, source, altitude_meters FROM mobile_location_points WHERE recorded_at_utc::date = @day ORDER BY random() LIMIT @n"
            : "SELECT user_id, device_id, recorded_at_utc, latitude, longitude, horizontal_accuracy_meters, provider, source, altitude_meters FROM mobile_location_points ORDER BY random() LIMIT @n";
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (day.HasValue) cmd.Parameters.AddWithValue("day", day.Value.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("n", n);
        var list = new List<MobileLocationPointRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MobileLocationPointRow(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8)
            ));
        }
        return list;
    }

    public async Task<List<DateOnly>> SampleDistinctDays(int n, string table, string dateColumn)
    {
        var conn = RequireConnection();
        var sql = $"SELECT DISTINCT {dateColumn}::date AS d FROM {table} ORDER BY d DESC LIMIT @n";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("n", n);
        var list = new List<DateOnly>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dt = reader.GetFieldValue<DateTime>(0);
            list.Add(DateOnly.FromDateTime(dt));
        }
        return list;
    }

    // 静态缓存，避免2000组回放时每次查DB
    private static List<MobileUsageSessionRow>? _cachedSessions;
    private static readonly object _cacheLock = new();
    public async Task<List<MobileUsageSessionRow>> GetCachedSessions(int total = 12000)
    {
        if (_cachedSessions != null) return _cachedSessions;
        lock (_cacheLock)
        {
            if (_cachedSessions != null) return _cachedSessions;
        }
        var sessions = await SampleSessions(total);
        lock (_cacheLock) _cachedSessions = sessions;
        return sessions;
    }

    public sealed record MobileUsageSessionRow(string UserId, string DeviceId, string PackageName, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, long DurationMs, string QualityFlagsJson);
    public sealed record PcAwEventRow(string DeviceId, DateTimeOffset Timestamp, double Duration, string EventType, string? AppName, string? WindowTitle, string? AfkStatus);
    public sealed record MobileLocationPointRow(string UserId, string DeviceId, DateTimeOffset RecordedAtUtc, decimal Latitude, decimal Longitude, decimal HorizontalAccuracyMeters, string? Provider, string? Source, decimal? AltitudeMeters);

    private sealed class SkipException : Xunit.SkipException
    {
        public SkipException(string message) : base(message) { }
    }
}

public sealed class SkipException : Xunit.SkipException
{
    public SkipException(string message) : base(message) { }
}
