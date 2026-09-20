using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Invariants;

public class DataReliabilityQualityInspectorTests
{
    [Fact]
    public void Invariants_WhenCollectionsEmpty_ReturnUnknownStatus()
    {
        // 验证空集合不得视为通过（绝不亮假绿灯），全部判为 ⚪ UNKNOWN
        var r1 = DataReliabilityInvariants.CheckS1_NoOverlap(new List<EventTimeSpan>());
        Assert.Equal(InvariantStatus.Unknown, r1.Status);
        Assert.False(r1.Pass);
        Assert.StartsWith("INV-P16 UNKNOWN", r1.Detail);

        var r2 = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(new List<LongEventCandidate>());
        Assert.Equal(InvariantStatus.Unknown, r2.Status);
        Assert.False(r2.Pass);

        var r3 = DataReliabilityInvariants.CheckS3_DailyDurationBounded(new List<DailyActiveDuration>());
        Assert.Equal(InvariantStatus.Unknown, r3.Status);
        Assert.False(r3.Pass);

        var r4 = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(new List<BusinessRecordKey>());
        Assert.Equal(InvariantStatus.Unknown, r4.Status);
        Assert.False(r4.Pass);

        var r5 = DataReliabilityInvariants.CheckS5_ClockTrustworthy(new List<ClockEventItem>());
        Assert.Equal(InvariantStatus.Unknown, r5.Status);
        Assert.False(r5.Pass);

        var r6 = DataReliabilityInvariants.CheckS6_OfflineDeclared(new DeviceActivityTrace());
        Assert.Equal(InvariantStatus.Unknown, r6.Status);
        Assert.False(r6.Pass);

        var r7 = DataReliabilityInvariants.CheckS7_TimelineGapMarked(new List<TimelineInterval>());
        Assert.Equal(InvariantStatus.Unknown, r7.Status);
        Assert.False(r7.Pass);

        var r8 = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(new List<DayBoundarySample>());
        Assert.Equal(InvariantStatus.Unknown, r8.Status);
        Assert.False(r8.Pass);

        var r9 = DataReliabilityInvariants.CheckS9_GapHasSignal(new CoverageSignalReport { OnlineDurationSeconds = 0 });
        Assert.Equal(InvariantStatus.Unknown, r9.Status);
        Assert.False(r9.Pass);

        var r10 = DataReliabilityInvariants.CheckS10_TaskHasOutput(new List<BackgroundTaskRun>());
        Assert.Equal(InvariantStatus.Unknown, r10.Status);
        Assert.False(r10.Pass);

        var r11 = DataReliabilityInvariants.CheckS11_StatusSemantics(new List<BatchSyncStatusRecord>());
        Assert.Equal(InvariantStatus.Unknown, r11.Status);
        Assert.False(r11.Pass);

        var r12 = DataReliabilityInvariants.CheckS12_DerivedTableActive(new List<DerivedTableStatus>());
        Assert.Equal(InvariantStatus.Unknown, r12.Status);
        Assert.False(r12.Pass);

        var r13 = DataReliabilityInvariants.CheckS13_SingleInstance(new List<CollectionHeartbeat>());
        Assert.Equal(InvariantStatus.Unknown, r13.Status);
        Assert.False(r13.Pass);
    }

    [Fact]
    public async Task Inspector_WhenDbNull_ReturnsUnhealthyAndThirteenUnknowns()
    {
        var options = Options.Create(new InvariantOptions());
        var inspector = new DataReliabilityQualityInspector(null, options, NullLogger<DataReliabilityQualityInspector>.Instance);

        var result = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        Assert.False(result.IsHealthy);
        Assert.Equal(13, result.IssueCount);

        // Details 是可空属性：先钉住再取值，后续断言就不会踩空。
        Assert.NotNull(result.Details);
        var details = result.Details!;
        Assert.Contains("S1_INV-P16", details.Keys);
        Assert.Contains("S13_INV-P22", details.Keys);

        // 13 条尺子的结论必须全部是"未知"，绝不亮假绿灯（summary 是聚合行，不参与该断言）。
        foreach (var kvp in details.Where(entry => entry.Key != "summary"))
        {
            Assert.StartsWith("⚪ UNKNOWN", kvp.Value);
        }

        Assert.Equal("0 Red, 0 Yellow, 0 Green, 13 Unknown", details["summary"]);
    }

    [Fact]
    public void InvariantResult_FourStatesProperties_AreMutuallyConsistent()
    {
        var pass = InvariantResult.Success("OK");
        Assert.True(pass.IsPass);
        Assert.False(pass.IsWarning);
        Assert.False(pass.IsFail);
        Assert.False(pass.IsUnknown);

        var warn = InvariantResult.Warning("Warn");
        Assert.False(warn.IsPass);
        Assert.True(warn.IsWarning);
        Assert.False(warn.IsFail);
        Assert.False(warn.IsUnknown);

        var fail = InvariantResult.Failure("Fail");
        Assert.False(fail.IsPass);
        Assert.False(fail.IsWarning);
        Assert.True(fail.IsFail);
        Assert.False(fail.IsUnknown);

        var unknown = InvariantResult.Unknown("Unknown");
        Assert.False(unknown.IsPass);
        Assert.False(unknown.IsWarning);
        Assert.False(unknown.IsFail);
        Assert.True(unknown.IsUnknown);
    }

    /// <summary>
    /// 验证取数层（无外置 PG 依赖）：断言 Inspector 生成的 SQL 全面使用 pc_tracker_events 与明确的 Asia/Shanghai 04:00 业务日表达式，
    /// 彻底剔除 pc_aw_events，并接入 pc_tracker_health。
    /// </summary>
    [Fact]
    public async Task Inspector_QueriesUseNativeTrackerAndExplicitBizDay_WithoutExternalDb()
    {
        var recordingConn = new RecordingDbConnection();
        var optionsBuilder = new DbContextOptionsBuilder<PimDbContext>();
        optionsBuilder.UseNpgsql(recordingConn);

        await using var db = new PimDbContext(optionsBuilder.Options);
        var options = Options.Create(new InvariantOptions());
        var inspector = new DataReliabilityQualityInspector(db, options, NullLogger<DataReliabilityQualityInspector>.Instance);

        var result = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        // 验证执行的所有 SQL 语句
        var executedSqlList = recordingConn.ExecutedCommands;
        Assert.NotEmpty(executedSqlList);

        // 1. 绝不包含任何 pc_aw_events
        foreach (var sql in executedSqlList)
        {
            Assert.DoesNotContain("pc_aw_events", sql);
        }

        // 2. 必须包含 pc_tracker_events 取数
        Assert.Contains(executedSqlList, sql => sql.Contains("FROM pc_tracker_events"));

        // 3. 必须包含 pc_tracker_health 取数
        Assert.Contains(executedSqlList, sql => sql.Contains("pc_tracker_health"));

        // 4. 业务日表达式必须显式写为 ((timestamp AT TIME ZONE 'Asia/Shanghai') - interval '4 hours')::date
        Assert.Contains(executedSqlList, sql => sql.Contains("((timestamp AT TIME ZONE 'Asia/Shanghai') - interval '4 hours')::date"));

        // 5. 必须覆盖 S1 到 S13 的所有判据键
        Assert.NotNull(result.Details);
        for (int i = 1; i <= 13; i++)
        {
            var keyPrefix = $"S{i}_";
            Assert.Contains(result.Details!.Keys, k => k.StartsWith(keyPrefix));
        }
    }

    private static readonly DateTimeOffset ReportNow = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static DataReliabilityQualityInspector CreateRecordingInspector(RecordingDbConnection conn)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PimDbContext>();
        optionsBuilder.UseNpgsql(conn);
        var db = new PimDbContext(optionsBuilder.Options);
        return new DataReliabilityQualityInspector(
            db,
            Options.Create(new InvariantOptions()),
            NullLogger<DataReliabilityQualityInspector>.Instance);
    }

    /// <summary>
    /// 结构化报告（#260）：13 条尺子齐全，且每条都带有前端要展示的判据/阈值/理由/关联 issue 与分档计数。
    /// 样例不得超过 T7 的 10 条上限。
    /// </summary>
    [Fact]
    public async Task InspectReportAsync_ReturnsThirteenStructuredRules()
    {
        var inspector = CreateRecordingInspector(new RecordingDbConnection());

        var report = await inspector.InspectReportAsync(ReportNow);

        Assert.Equal(13, report.Rules.Count);
        Assert.Equal(13, report.RedCount + report.YellowCount + report.GreenCount + report.UnknownCount);
        Assert.Equal(report.Rules.Select(rule => rule.Code).OrderBy(code => code.Length).ThenBy(code => code),
            report.Rules.Select(rule => rule.Code).OrderBy(code => code.Length).ThenBy(code => code));

        foreach (var rule in report.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Threshold), $"{rule.Code} 缺阈值");
            Assert.False(string.IsNullOrWhiteSpace(rule.Criterion), $"{rule.Code} 缺判据原文");
            Assert.False(string.IsNullOrWhiteSpace(rule.Rationale), $"{rule.Code} 缺设定理由");
            Assert.True(rule.Samples.Count <= new InvariantOptions().MaxSampleCount, $"{rule.Code} 样例超过上限");
            Assert.InRange(rule.NewViolations + rule.HistoricalViolations, 0, rule.TotalViolations);
            Assert.Contains(rule.Status, new[] { "red", "yellow", "green", "unknown" });
            Assert.False(string.IsNullOrWhiteSpace(rule.StatusLabel));
        }

        // 能判定出状态的尺子必须给出当前值；未知的尺子不得把"违规数 0"伪装成测量值。
        Assert.All(report.Rules.Where(rule => rule.Status != "unknown"), rule => Assert.NotNull(rule.CurrentValue));
        Assert.All(
            report.Rules.Where(rule => rule.Status == "unknown" && rule.CurrentValueUnit == "条"),
            rule => Assert.Null(rule.CurrentValue));

        // 总览状态必须与逐条结论自洽（红 > 黄 > 未知 > 绿）。
        var expectedOverall = report.RedCount > 0
            ? "red"
            : report.YellowCount > 0
                ? "yellow"
                : report.UnknownCount > 0
                    ? "unknown"
                    : "green";
        Assert.Equal(expectedOverall, report.Status);
    }

    [Fact]
    public async Task InspectReportAsync_AllDatabaseAccessIsReadOnly()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);
        await inspector.GetViolationsAsync("S1", 50);

        Assert.NotEmpty(conn.ExecutedCommands);

        // 关键字必须按词边界匹配：created_at / updated_at 是列名，不能误判为写操作。
        var writeKeyword = new System.Text.RegularExpressions.Regex(
            @"\b(INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|TRUNCATE|COPY|GRANT|VACUUM|REINDEX)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var sql in conn.ExecutedCommands)
        {
            var match = writeKeyword.Match(sql);
            Assert.False(match.Success, $"体检链路出现了写操作 {match.Value}: {sql}");
            Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>13 个取数 SQL 都必须在数据库侧被限行（#260 验收标准 2：单条尺子查询有上限保护）。</summary>
    [Fact]
    public async Task InspectReportAsync_BoundsEveryScan()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);

        // 只要求"会把多行拉进内存"的查询带上 LIMIT；count(*)/sum() 这类单值聚合天然只有一行结果。
        var scans = conn.ExecutedCommands
            .Where(sql => sql.Contains("FROM pc_tracker_events", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("FROM mobile_", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("FROM pc_tracker_health", StringComparison.OrdinalIgnoreCase))
            .Where(sql => !System.Text.RegularExpressions.Regex.IsMatch(
                sql,
                @"SELECT\s+(count|COALESCE\s*\(\s*SUM|SUM)\s*\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        Assert.NotEmpty(scans);
        Assert.All(scans, sql => Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 回归防线：所有取数 SQL 都必须真正完成字符串插值。
    /// 漏写 `$` 的原始字符串会把 `{options.MaxScanRows + 1}` 原样发给 PostgreSQL（42601 语法错误），
    /// 而在 mock 连接下不会报错，只有实机体检才会暴露 —— 这里用断言把它挡在 CI 里。
    /// </summary>
    [Fact]
    public async Task InspectReportAsync_NeverSendsUninterpolatedPlaceholders()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);
        await inspector.GetViolationsAsync("S1", 5);

        foreach (var sql in conn.ExecutedCommands)
        {
            Assert.DoesNotContain("{options.", sql);
            Assert.DoesNotContain("{thresholdSeconds", sql);
            Assert.DoesNotContain("{max", sql);
        }
    }

    /// <summary>
    /// 回归防线：SQL 里出现的每一个 @参数都必须真的被绑定过。
    /// 漏绑会让 PostgreSQL 抛 42883/42P02，而在 mock 连接下静默通过 —— 只有实机体检才暴露。
    /// </summary>
    [Fact]
    public async Task InspectReportAsync_BindsEveryReferencedParameter()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);
        await inspector.GetViolationsAsync("S1", 5);

        Assert.NotEmpty(conn.ExecutedCommands);
        Assert.Equal(conn.ExecutedCommands.Count, conn.ExecutedParameterNames.Count);

        for (int i = 0; i < conn.ExecutedCommands.Count; i++)
        {
            var sql = conn.ExecutedCommands[i];
            var bound = conn.ExecutedParameterNames[i].Select(name => name.TrimStart('@')).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(sql, @"@([A-Za-z_][A-Za-z0-9_]*)"))
            {
                Assert.True(bound.Contains(match.Groups[1].Value),
                    $"SQL 引用了未绑定的参数 {match.Value}: {sql}");
            }
        }
    }

    /// <summary>
    /// 复审回归（Important）：C# 侧的 <c>IsGapEventType</c> 与 SQL 侧的
    /// <c>GapEventTypeSqlList</c> 必须逐项一致。
    ///
    /// 这两份"缺数据"定义分别用于 S7（在内存里标记覆盖区间）与 S9（在 SQL 里拆分离线与
    /// 有效记录）。一旦有人只改了一边，同一行数据就会在一条尺子里被算作记录、
    /// 在另一条里被算作离线 —— 这类漂移没有任何编译错误，只能靠这条断言拦住。
    /// </summary>
    [Fact]
    public void GapEventTypePredicate_MatchesSqlList()
    {
        const char SingleQuote = '\'';

        var sqlTypes = DataReliabilityQualityInspector.GapEventTypeSqlList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim(SingleQuote))
            .ToList();

        Assert.NotEmpty(sqlTypes);
        Assert.Equal(sqlTypes.Count, sqlTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // SQL 片段必须恰好由权威清单渲染而来 —— 双向都成立，漂移无从发生
        Assert.Equal(
            DataReliabilityQualityInspector.GapEventTypes.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            sqlTypes.OrderBy(t => t, StringComparer.Ordinal).ToList());

        // C# 判定与 SQL 清单对每一个候选类型给出**相同**结论（双向覆盖）
        var candidates = DataReliabilityQualityInspector.GapEventTypes
            .Concat(["window", "web-page", "idle", "notepad", "hibernate", "", "GAP", "Sleep"])
            .ToList();

        foreach (var type in candidates)
        {
            bool inSqlList = sqlTypes.Contains(type, StringComparer.OrdinalIgnoreCase);
            bool byCSharp = DataReliabilityQualityInspector.IsGapEventType(type);
            Assert.True(inSqlList == byCSharp,
                $"'{type}' 在 SQL 清单里={inSqlList}，但 C# 判定={byCSharp} —— S7 与 S9 会对同一行数据给出相反分类");
        }

        // 真实采集类型必须**不**被判为 gap
        foreach (var type in new[] { "window", "web-page", "idle" })
        {
            Assert.False(DataReliabilityQualityInspector.IsGapEventType(type),
                $"'{type}' 是真实采集类型，不应被判为缺数据");
        }

        Assert.False(DataReliabilityQualityInspector.IsGapEventType(null));
        Assert.False(DataReliabilityQualityInspector.IsGapEventType(""));
        Assert.False(DataReliabilityQualityInspector.IsGapEventType("unknown-type"));
    }

    /// <summary>
    /// 复审回归（Important）：S9 与 S7 必须使用**同一份**"缺数据"类型口径。
    /// S7 把 gap/afk/offline/sleep 当作覆盖标记；S9 必须把同样的类型算作"设备声明的离线"，
    /// 并把其余类型算作有效记录。两处各写各的，legacy（afk/offline/sleep）或未来新增的类型
    /// 就会在一条尺子里被算作记录、在另一条里被算作离线，导致两条尺子互相矛盾。
    /// </summary>
    [Fact]
    public async Task CheckS9AndS7_UseTheSameGapEventTypePredicate()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);

        // S9 的两条 CTE：离线集合按 IN 判定，有效记录按 NOT IN 判定，二者互补。
        var coverageQuery = conn.ExecutedCommands
            .First(sql => sql.Contains("offline_seconds", StringComparison.OrdinalIgnoreCase));

        // 从权威清单派生期望值，而不是冻结成字面量：这样"调整缺数据类型集合"
        // 只需改一处，测试只负责验证 IN / NOT IN 两者互补。
        var gapList = DataReliabilityQualityInspector.GapEventTypeSqlList;
        Assert.Contains($"NOT IN ({gapList})", coverageQuery);
        Assert.Contains($"IN ({gapList})", coverageQuery);

        // S7 的时间线查询取全部类型，由 C# 侧统一判定是否 gap（不再在 SQL 里硬编码类型集合）
        var timelineQuery = conn.ExecutedCommands
            .First(sql => sql.Contains("end_time", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("event_type IN", timelineQuery, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 复审回归（Important）：S9 的设备集合必须取"有记录"与"有离线声明"的**并集**。
    /// 只从 recorded 出发会让"整段窗口都声明了离线、因此没有任何记录"的设备被静默跳过，
    /// 等于替它默认通过；这类设备恰恰是最需要被看见的。
    /// </summary>
    [Fact]
    public async Task CheckS9_EnumeratesDevicesFromBothRecordedAndDeclaredOffline()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);

        var coverageQuery = conn.ExecutedCommands
            .FirstOrDefault(sql => sql.Contains("offline_seconds", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(coverageQuery);
        Assert.Contains("UNION", coverageQuery!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("devices", coverageQuery!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 复审回归（Important）：S7 的时间线区间必须取**全部**事件类型。
    /// 早先只取 window/idle/gap、把 web-page 排除在外，会让"浏览器会话被分段成
    /// window + web-page"的时间段凭空出现空洞（实测多报 6 处不存在的断档）。
    /// </summary>
    [Fact]
    public async Task CheckS7_TimelineQuery_IncludesEveryEventType()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);

        var timelineQueries = conn.ExecutedCommands
            .Where(sql => sql.Contains("end_time", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(timelineQueries);
        Assert.All(timelineQueries, sql =>
            Assert.DoesNotContain("event_type IN", sql, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>体检窗口必须来自调用方传入的时钟，不得依赖数据库 NOW()（否则结论随库时钟漂移）。</summary>
    [Fact]
    public async Task InspectReportAsync_DoesNotRelyOnTheDatabaseClock()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);

        Assert.All(conn.ExecutedCommands, sql =>
            Assert.DoesNotContain("NOW()", sql, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 复审回归（Important）：导出通路必须复刻体检用到的**全部**阈值配置。
    /// 漏拷一个字段会让"面板结论"与"导出清单"用不同口径计算，用户看到的违规数与导出结果对不上。
    ///
    /// 这里用反射逐字段对比（除有意覆盖的样例上限），因此以后给 InvariantOptions
    /// 新增字段却忘了复制时会自动失败，不需要有人记得回来补测试。
    /// </summary>
    [Fact]
    public void BuildExportOptions_CopiesEveryThresholdExceptSampleLimit()
    {
        var source = new InvariantOptions
        {
            MinInputDensityPerMinute = 2.5,
            LongEventThresholdMinutes = 45,
            UndeclaredOfflineGapMinutes = 40,
            MaxUploadLagP99Minutes = 35,
            MobileSummaryLagHours = 6,
            RecentWindowHours = 48,
            MaxDailyActiveHours = 20,
            AwakeWindowHours = 15,
            AwakeWindowWarningRatio = 0.8,
            CoverageRedRatio = 0.9,
            CoverageYellowRatio = 0.97,
            MaxSampleCount = 7,
            ClockSkewToleranceMinutes = 3,
            TimelineGapThresholdMinutes = 20,
            InstanceOverlapToleranceSeconds = 0.25,
            Tolerance = 0.07,
            MaxScanRows = 1234,
            InspectionTimeoutSeconds = 30
        };

        var export = DataReliabilityQualityInspector.BuildExportOptions(source, sampleLimit: 500);

        Assert.Equal(500, export.MaxSampleCount); // 唯一被有意覆盖的字段

        var mismatches = new List<string>();
        foreach (var property in typeof(InvariantOptions).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.Name == nameof(InvariantOptions.MaxSampleCount)) continue;

            var expected = property.GetValue(source);
            var actual = property.GetValue(export);
            if (!Equals(expected, actual))
            {
                mismatches.Add($"{property.Name}: 期望 {expected}, 实际 {actual}");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"导出配置漏拷了体检阈值：{string.Join("; ", mismatches)}");
    }

    /// <summary>
    /// 端到端佐证：同一份心跳在默认容差下判红，在放宽容差后判绿 ——
    /// 说明容差确实参与判定，因此上面那条"逐字段复刻"的断言是有实际后果的。
    /// </summary>
    [Fact]
    public void InstanceOverlapTolerance_ChangesS13Verdict()
    {
        var heartbeats = new List<CollectionHeartbeat>
        {
            new() { DeviceId = "d", Timestamp = ReportNow.UtcDateTime, DurationSeconds = 600.2, InstanceId = "a" },
            new() { DeviceId = "d", Timestamp = ReportNow.UtcDateTime.AddSeconds(600), DurationSeconds = 60, InstanceId = "b" }
        };

        Assert.False(DataReliabilityInvariants.CheckS13_SingleInstance(
            heartbeats, new InvariantOptions { InstanceOverlapToleranceSeconds = 0.05 }).Pass);
        Assert.True(DataReliabilityInvariants.CheckS13_SingleInstance(
            heartbeats, new InvariantOptions { InstanceOverlapToleranceSeconds = 0.25 }).Pass);
    }

    [Fact]
    public async Task GetViolationsAsync_UnknownRule_Throws()
    {
        var inspector = CreateRecordingInspector(new RecordingDbConnection());

        await Assert.ThrowsAsync<ArgumentException>(() => inspector.GetViolationsAsync("S99", 10));
    }

    #region Mock ADO.NET Infrastructure for Offline Verification

    private sealed class RecordingDbConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        public List<string> ExecutedCommands { get; } = new();

        /// <summary>每条语句执行时已绑定的参数名（用于验证 SQL 里的 @xxx 都真的被绑定了）。</summary>
        public List<IReadOnlyList<string>> ExecutedParameterNames { get; } = new();

        [AllowNull]
        public override string ConnectionString { get; set; } = "Host=mock;Database=mock";
        public override string Database => "mock";
        public override string DataSource => "mock";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new RecordingDbCommand(this);
    }

    private sealed class RecordingDbCommand : DbCommand
    {
        private readonly RecordingDbConnection _connection;

        public RecordingDbCommand(RecordingDbConnection connection)
        {
            _connection = connection;
        }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        protected override DbConnection? DbConnection
        {
            get => _connection;
            set { }
        }
        protected override DbParameterCollection DbParameterCollection { get; } = new DummyParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Cancel() { }
        protected override DbParameter CreateDbParameter() => new DummyParameter();

        private void Record()
        {
            _connection.ExecutedCommands.Add(CommandText);
            _connection.ExecutedParameterNames.Add(
                Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName).ToList());
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            return new EmptyDbDataReader();
        }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            Record();
            return Task.FromResult<DbDataReader>(new EmptyDbDataReader());
        }

        public override int ExecuteNonQuery()
        {
            Record();
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            Record();
            return Task.FromResult(1);
        }

        public override object? ExecuteScalar()
        {
            Record();
            return 0L;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            Record();
            return Task.FromResult<object?>(0L);
        }

        public override void Prepare() { }
    }

    private sealed class DummyParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class DummyParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = new();
        public override int Count => _parameters.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _parameters.Add((DbParameter)value); return _parameters.Count - 1; }
        public override void AddRange(Array values)
        {
            foreach (var val in values)
            {
                if (val is DbParameter p) _parameters.Add(p);
            }
        }
        public override void Clear() => _parameters.Clear();
        public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
        public override bool Contains(string value) => _parameters.Exists(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_parameters).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
        protected override DbParameter GetParameter(int index) => _parameters[index];
        protected override DbParameter GetParameter(string parameterName) => _parameters.Find(p => p.ParameterName == parameterName) ?? new DummyParameter();
        public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _parameters.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _parameters.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _parameters.RemoveAt(index);
        public override void RemoveAt(string parameterName) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters.RemoveAt(idx); }
        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters[idx] = value; }
    }

    private sealed class EmptyDbDataReader : DbDataReader
    {
        public override int FieldCount => 0;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override bool HasRows => false;

        public override object this[int ordinal] => DBNull.Value;
        public override object this[string name] => DBNull.Value;

        public override bool Read() => false;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public override bool NextResult() => false;
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => ' ';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => string.Empty;
        public override DateTime GetDateTime(int ordinal) => DateTime.UtcNow;
        public override decimal GetDecimal(int ordinal) => 0m;
        public override double GetDouble(int ordinal) => 0.0;
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override float GetFloat(int ordinal) => 0f;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => 0;
        public override long GetInt64(int ordinal) => 0;
        public override string GetName(int ordinal) => string.Empty;
        public override int GetOrdinal(string name) => -1;
        public override string GetString(int ordinal) => string.Empty;
        public override object GetValue(int ordinal) => DBNull.Value;
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => true;
        public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }

    #endregion
}
