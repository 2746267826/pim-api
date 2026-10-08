using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// issue #397 的真库回归（<c>Trait DataSource=RealDb</c>）：S6「上传滞后 p99」的**取数接线**必须
/// 以「事件区间结束、该条事件可以上传的时刻」为基准，而不是区间起点。
///
/// <para>
/// 为什么必须在真库上钉住接线：基准的换算发生在
/// <c>DataReliabilityQualityInspector.CheckS6Async</c>（<c>row.End = timestamp + duration</c> →
/// <c>UploadLagSample.UploadableAt = row.End</c>）。把这条赋值改回 <c>row.Start</c>，
/// 只有"跑一遍真实取数 SQL"的用例才会失败 —— 纯判据层单测是发现不了的。
/// </para>
///
/// <para>
/// 判别性样本（与生产实测同形）：<c>duration = 1800s</c>、区间结束后 22 秒完成上传。
/// 按区间结束算滞后 = 0.37 分钟（健康，不违规）；按区间起点算 = 30.4 分钟（假红）。
/// </para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class DataReliabilityS6UploadLagRealDbTests
{
    private const string Device = "PC-LAG-PROBE";

    /// <summary>插入一行 pc_tracker_events，并显式指定 created_at（上行完成时刻）。</summary>
    private static async Task InsertSliceAsync(
        TempMigrationDatabase database,
        DateTimeOffset start,
        double durationSeconds,
        DateTimeOffset createdAt,
        string eventType = "window")
    {
        await database.ExecuteAsync($"""
            INSERT INTO pc_tracker_events
                (device_id, timestamp, duration, event_type, app_name, display_name, window_title,
                 created_at, is_idle, is_media_active, page_visit_count, date)
            VALUES ('{Device}', '{start:O}', {durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    '{eventType}', 'Code.exe', 'Code', 'probe',
                    '{createdAt:O}', false, false, 0,
                    ('{start:O}' AT TIME ZONE 'Asia/Shanghai')::date);
            """);
    }

    /// <summary>走真实取数 SQL 跑一遍 S6，并取回结构化违规清单（kind 字段可用于判定类型）。</summary>
    private static async Task<IReadOnlyList<DataReliabilityViolationItem>> RunS6Async(TempMigrationDatabase database)
    {
        var inspector = new DataReliabilityQualityInspector(
            database.Db,
            Options.Create(new InvariantOptions()),
            NullLogger<DataReliabilityQualityInspector>.Instance);

        var export = await inspector.GetViolationsAsync("S6", 5000);
        return export.Items;
    }

    private static bool IsUploadLag(DataReliabilityViolationItem item)
        => item.Fields.TryGetValue("kind", out var kind) && kind == "upload-lag-p99";

    [SkippableFact]
    public async Task S6UploadLag_ThirtyMinuteSliceUploaded22SecondsAfterItEnds_IsNotALagViolation()
    {
        RealDbTestConnection.SkipIfUnavailable();
        await using var database = await TempMigrationDatabase.CreateAsync("test_issue_397");
        await database.MigrateAsync();
        await database.RunPcTrackerSchemaInitializerAsync();

        // 40 小时前的一条 30 分钟切片，区间结束后 22 秒完成上传（生产命中样本的形态）。
        var start = DateTimeOffset.UtcNow.AddHours(-40);
        await InsertSliceAsync(database, start, 1800, start.AddSeconds(1800 + 22));

        var violations = await RunS6Async(database);

        Assert.DoesNotContain(violations, IsUploadLag);
        Assert.Empty(violations);   // 只有这一条事件区间，没有任何空档，S6 应当全绿

        // 反向确认这条样本确实具有判别性：按区间**起点**算滞后必然超过 30 分钟阈值。
        var legacyLagMinutes = (start.AddSeconds(1800 + 22) - start).TotalMinutes;
        Assert.True(legacyLagMinutes > 30.0, $"按区间起点算的滞后应超过阈值，实际 {legacyLagMinutes:F1} 分钟");
    }

    [SkippableFact]
    public async Task S6UploadLag_FortyMinutesAfterTheSliceEnds_IsStillAViolation()
    {
        // 反面行为：真实的链路积压必须仍被判出（不得因为换基准而漏报）。
        RealDbTestConnection.SkipIfUnavailable();
        await using var database = await TempMigrationDatabase.CreateAsync("test_issue_397_neg");
        await database.MigrateAsync();
        await database.RunPcTrackerSchemaInitializerAsync();

        var start = DateTimeOffset.UtcNow.AddHours(-40);
        await InsertSliceAsync(database, start, 1800, start.AddSeconds(1800).AddMinutes(40));

        var violations = await RunS6Async(database);

        var violation = Assert.Single(violations, IsUploadLag);
        Assert.Equal("40.0", violation.Fields["worstLagMinutes"]);
        Assert.Equal("40.0", violation.Fields["p99LagMinutes"]);
    }
}
