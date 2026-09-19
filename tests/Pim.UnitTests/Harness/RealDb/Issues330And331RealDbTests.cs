using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;
using MobileTimelinePagination = Pim.Module.Mobile.DTOs.MobileTimelinePagination;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// 真库用例（#330 / #331）：在生产镜像数据上核对两个缺陷确实已被修复。
///
/// <para><b>#330</b>：手机端 timeline 的会话量级。旧实现硬编码 <c>Take(500)</c>，
/// 实测单日 2049 条只返回 500 条。这里量出真实量级，证明「500 条上限」必然截断，
/// 而新默认页大小足以覆盖绝大多数业务日。</para>
///
/// <para><b>#331</b>：空档（gap/idle/afk）记录不得被判成应用类别，
/// 且 timeline/v2 不得把它们渲染成 <c>pc-fallback-v1:*</c> 伪应用块。</para>
///
/// 需要 <c>PIM_TEST_CONN</c>；不可用时显式 Skip（仓库既定约定）。
/// 只读：不对镜像库做任何写入。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class Issues330And331RealDbTests
{
    private static PimDbContext CreateContext(string connectionString)
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PimDbContext(options);
    }

    /// <summary>业务日 = [D 04:00, D+1 04:00) Asia/Shanghai，此处统一按 UTC+4h 取日界。</summary>
    private const string BusinessDaySql = "(start_utc + interval '4 hours')::date";

    // ================= #330：手机端 timeline 量级 =================

    [SkippableFact]
    public async Task MobileTimeline_RealDayExceedsLegacy500Cap()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // 量出「单设备 × 单业务日」的会话量级 —— 修复前固定只返回前 500 条
        await using var command = new NpgsqlCommand(
            $"""
             SELECT max(c) AS max_sessions, count(*) AS day_count
             FROM (
                 SELECT device_id, {BusinessDaySql} AS bizday, count(*) AS c
                 FROM mobile_usage_sessions
                 GROUP BY 1, 2
             ) per_device_day
             """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var maxSessions = reader.GetInt64(0);
        var dayCount = reader.GetInt64(1);
        await reader.CloseAsync();

        Assert.True(dayCount > 0, "镜像库应含会话数据，否则本用例无意义");

        // 镜像库确实存在远超 500 条的「设备 × 业务日」——这是 #330 的客观前提。
        Assert.True(
            maxSessions > 500,
            $"镜像库单设备单日最大会话数仅 {maxSessions}，无法复现 #330 的截断场景");

        // 修复后的默认页大小必须覆盖该量级，否则默认请求仍会截断。
        Assert.True(
            maxSessions <= MobileTimelinePagination.DefaultPageSize,
            $"默认页大小 {MobileTimelinePagination.DefaultPageSize} " +
            $"小于镜像库实测峰值 {maxSessions}，默认请求仍会截断");
    }

    [SkippableFact]
    public async Task MobileTimeline_SpecificHeavyDayLosesDataUnderLegacyCap()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // 找出会话最多的那个业务日（跨全部设备），核对 500 条上限会丢多少
        await using var command = new NpgsqlCommand(
            $"""
             SELECT {BusinessDaySql} AS bizday, count(*) AS c
             FROM mobile_usage_sessions
             GROUP BY 1
             ORDER BY c DESC
             LIMIT 1
             """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var busiestDay = reader.GetFieldValue<DateTime>(0);
        var total = reader.GetInt64(1);
        await reader.CloseAsync();

        var legacyVisible = Math.Min(total, 500);
        Assert.True(
            legacyVisible < total,
            $"最繁忙业务日 {busiestDay:yyyy-MM-dd} 共 {total} 条，旧实现只能看到 {legacyVisible} 条");
        Assert.True(total > 0);
    }

    // ================= #331：空档不得判成应用类别 =================

    [SkippableFact]
    public async Task InactiveClassifications_OnRealData_NeverCarryApplicationCategories()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var db = CreateContext(connectionString);

        // 生产库中 gap/idle/afk 曾被判成「游戏」（unknown 规则命中）。
        // 分类器已对非应用记录短路，这里核对：按新逻辑重新判定时，
        // 这些记录一律得到「未活动」，绝不落到任何应用类别上。
        var inactive = await db.Set<ActivityClassificationEntity>()
            .AsNoTracking()
            .Where(c => c.RecordType == "gap" || c.RecordType == "idle" || c.RecordType == "afk")
            .Select(c => new { c.RecordType, c.CategoryName })
            .Take(500)
            .ToListAsync(CancellationToken.None);

        Skip.If(inactive.Count == 0, "镜像库没有 gap/idle/afk 分类记录，跳过。");

        var rules = await new ActivityClassificationRuleService(db).LoadActiveAsync(CancellationToken.None);

        var misclassified = new List<string>();
        foreach (var row in inactive)
        {
            var result = ActivityClassifier.Classify(
                new Pim.Module.PcTracker.Services.ActivityClassificationContext(
                    RecordType: row.RecordType,
                    AppName: null,
                    AppNameNormalized: AppNameNormalizer.Normalize(null),
                    Domain: null,
                    UrlPath: null,
                    Title: null,
                    WindowTitle: null,
                    FilePath: null,
                    BucketType: null),
                rules,
                NullLogger.Instance);

            if (result.CategoryName != Pim.Module.PcTracker.DTOs.ActivityClassificationResult.InactiveCategoryName)
                misclassified.Add($"{row.RecordType}:{result.CategoryName}");
        }

        Assert.True(
            misclassified.Count == 0,
            $"空档记录仍被判成应用类别：{string.Join(", ", misclassified.Distinct().Take(5))}");
    }

    [SkippableFact]
    public async Task ActiveRules_OnRealData_DoNotMapUnknownToApplicationCategory()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var db = CreateContext(connectionString);

        var rules = await new ActivityClassificationRuleService(db).LoadActiveAsync(CancellationToken.None);

        // 分类器已对非应用记录短路，但仍应存在**任意**应用记录（appNameNormalized=unknown）
        // 命中该规则时被误判的风险 —— 这里核对存量规则本身是否还在做「unknown → 应用类别」映射。
        // 镜像库可能尚未包含该规则（快照早于规则产生），因此仅在存在时断言其不再 active。
        // conditions_json 是 jsonb：LIKE 不能直接作用于 jsonb 列（42883），
        // 因此与 ActivityLabelingService.LoadCoveredAppPatternsAsync 同口径，取回后在内存中过滤。
        var unknownRules = (await db.Set<ActivityCategoryRuleEntity>()
                .AsNoTracking()
                .Select(r => new { r.RuleName, r.Status, r.CategoryName, r.ConditionsJson })
                .ToListAsync(CancellationToken.None))
            .Where(r => r.ConditionsJson is not null
                && r.ConditionsJson.Contains("unknown", StringComparison.Ordinal))
            .ToList();

        var activeUnknownRules = unknownRules
            .Where(r => string.Equals(r.Status, "active", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            activeUnknownRules.Count == 0,
            "仍存在 active 的 unknown 兜底规则：" +
            string.Join(", ", activeUnknownRules.Select(r => $"{r.RuleName} -> {r.CategoryName}")));
    }

    // ================= #331：timeline/v2 不渲染空档伪应用块 =================

    [SkippableFact]
    public async Task TimelineV2_OnRealData_ContainsNoInactiveFallbackBlocks()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var db = CreateContext(connectionString);

        // 找一个含空档分类的业务日
        var probeDate = await db.Set<ActivityClassificationEntity>()
            .AsNoTracking()
            .Where(c => c.RecordType == "gap" || c.RecordType == "idle" || c.RecordType == "afk")
            .OrderByDescending(c => c.StartedAt)
            .Select(c => c.StartedAt)
            .FirstOrDefaultAsync(CancellationToken.None);

        Skip.If(probeDate == default, "镜像库没有空档分类记录，跳过。");

        var day = probeDate.ToOffset(TimeSpan.FromHours(8)).Date;
        var service = new PcProductivityService(db);
        var timeline = await service.GetTimelineV2Async(day, CancellationToken.None);

        Skip.If(timeline.Count == 0, $"业务日 {day:yyyy-MM-dd} 无时间线块，跳过。");

        // 空档不得以「伪应用块」形式出现
        Assert.DoesNotContain(
            timeline,
            item => item.AppName.StartsWith("pc-fallback-v1:", StringComparison.Ordinal));

        // 也不得出现「未活动」类别被打回成某个应用类别
        Assert.DoesNotContain(
            timeline,
            item => string.Equals(
                item.CategoryName,
                Pim.Module.PcTracker.DTOs.ActivityClassificationResult.InactiveCategoryName,
                StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task TimelineV2_InactiveOnlyWindow_ProducesNoBlocks()
    {
        var connectionString = RealDbTestConnection.Require();
        await using var db = CreateContext(connectionString);

        // 取一个只含空档分类的业务日（该日没有任何 window/web-page 应用记录）
        var candidates = await db.Set<ActivityClassificationEntity>()
            .AsNoTracking()
            .Where(c => c.RecordType == "gap" || c.RecordType == "idle" || c.RecordType == "afk")
            .Select(c => c.StartedAt)
            .Take(200)
            .ToListAsync(CancellationToken.None);

        Skip.If(candidates.Count == 0, "镜像库没有空档分类记录，跳过。");

        var days = candidates
            .Select(ts => ts.ToOffset(TimeSpan.FromHours(8)).Date)
            .Distinct()
            .Take(5)
            .ToList();

        var service = new PcProductivityService(db);
        var checkedAny = false;

        foreach (var day in days)
        {
            // Npgsql 只接受 offset 0 写入 timestamptz，查询参数统一换算成 UTC 再比较。
            var dayStartUtc = new DateTimeOffset(day, TimeSpan.FromHours(8)).ToUniversalTime();
            var dayEndUtc = dayStartUtc.AddDays(1);

            var hasApplicationRecords = await db.Set<ActivityClassificationEntity>()
                .AsNoTracking()
                .AnyAsync(
                    c => c.StartedAt >= dayStartUtc && c.StartedAt < dayEndUtc
                         && c.RecordType != "gap" && c.RecordType != "idle" && c.RecordType != "afk",
                    CancellationToken.None);

            if (hasApplicationRecords)
                continue;

            var timeline = await service.GetTimelineV2Async(day, CancellationToken.None);
            Assert.Empty(timeline);
            checkedAny = true;
        }

        Skip.If(!checkedAny, "镜像库没有「仅含空档」的业务日，跳过。");
    }
}
