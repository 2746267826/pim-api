using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// WO-RELIABILITY-WINDOW-20260928 REQ-1 / REQ-5：考核线定义与配置拆分。
///
/// <para>
/// 覆盖三层：纯函数（考核线公式）、配置（默认值 / 范围校验 / 考核窗与覆盖率窗口独立）、
/// 取数层（S3 / S4 采样 / S5 主取数绑考核线，S9 覆盖率窗口保持 24h）。
/// </para>
/// </summary>
public class DataReliabilityAssessmentWindowTests
{
    private static readonly DateTime InspectionTimeUtc = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset InspectionTime = new(InspectionTimeUtc, TimeSpan.Zero);

    #region AC-1.1 ~ AC-1.4 / AC-1.6：考核线公式

    /// <summary>AC-1.1：注入进程启动时刻 T0、体检时刻 T0 + 5 天 → 考核线 = T0。</summary>
    [Fact]
    public void AssessmentLine_ProcessStartLaterThanRollingStart_EqualsProcessStart()
    {
        var processStart = InspectionTimeUtc.AddDays(-5);

        var line = DataReliabilityAssessmentWindow.ResolveAssessmentStartUtc(
            InspectionTimeUtc, assessmentWindowHours: 168, processStartedAtUtc: processStart);

        Assert.Equal(processStart, line);
    }

    /// <summary>AC-1.2：进程启动时刻 = 体检时刻 − 8 天 → 考核线 = 体检时刻 − 7 天。</summary>
    [Fact]
    public void AssessmentLine_RollingStartLaterThanProcessStart_EqualsRollingStart()
    {
        var line = DataReliabilityAssessmentWindow.ResolveAssessmentStartUtc(
            InspectionTimeUtc, assessmentWindowHours: 168, processStartedAtUtc: InspectionTimeUtc.AddDays(-8));

        Assert.Equal(InspectionTimeUtc.AddDays(-7), line);
    }

    /// <summary>AC-1.3：刚重启（启动时刻在 2 分钟前）→ 考核线 = 进程启动时刻。</summary>
    [Fact]
    public void AssessmentLine_JustRestarted_EqualsProcessStart()
    {
        var processStart = InspectionTimeUtc.AddMinutes(-2);

        var line = DataReliabilityAssessmentWindow.ResolveAssessmentStartUtc(
            InspectionTimeUtc, assessmentWindowHours: 168, processStartedAtUtc: processStart);

        Assert.Equal(processStart, line);

        // 该时刻之前产生的违规全部进历史欠账、窗内计数为 0。
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(
            new List<EventTimeSpan>
            {
                new() { EventId = "x1", DeviceId = "PC-1", EventType = "window", StartTime = InspectionTimeUtc.AddDays(-3), EndTime = InspectionTimeUtc.AddDays(-3).AddMinutes(10) },
                new() { EventId = "x2", DeviceId = "PC-1", EventType = "window", StartTime = InspectionTimeUtc.AddDays(-3).AddMinutes(5), EndTime = InspectionTimeUtc.AddDays(-3).AddMinutes(15) }
            },
            referenceTimeUtc: InspectionTimeUtc,
            assessmentStartUtc: line);

        Assert.Equal(InvariantStatus.Pass, result.Status);
        Assert.Equal(0, result.WindowViolations);
        Assert.Equal(1, result.HistoricalViolations);
    }

    /// <summary>AC-1.4（反面）：考核线取较晚者，不得取较早者。</summary>
    [Theory]
    [InlineData(-1)]     // 进程启动晚于滚动窗起点
    [InlineData(-5)]     // 进程启动落在滚动窗内
    [InlineData(-7)]     // 与滚动窗起点重合
    [InlineData(-8)]     // 进程启动早于滚动窗起点
    [InlineData(-30)]
    public void AssessmentLine_IsNeverEarlierThanEitherBoundary(int processStartDaysAgo)
    {
        var rollingStart = InspectionTimeUtc.AddDays(-7);
        var processStart = InspectionTimeUtc.AddDays(processStartDaysAgo);

        var line = DataReliabilityAssessmentWindow.ResolveAssessmentStartUtc(
            InspectionTimeUtc, assessmentWindowHours: 168, processStartedAtUtc: processStart);

        Assert.True(line >= rollingStart, $"考核线 {line:O} 早于滚动窗起点 {rollingStart:O}");
        Assert.True(line >= processStart, $"考核线 {line:O} 早于进程启动时刻 {processStart:O}");
        Assert.Equal(processStart > rollingStart ? processStart : rollingStart, line);
    }

    /// <summary>AC-1.6：未注入进程启动时刻 → 回退纯滚动窗（体检时刻 − 考核窗时长）。</summary>
    [Fact]
    public void AssessmentLine_NoProcessStart_FallsBackToRollingWindow()
    {
        var line = DataReliabilityAssessmentWindow.ResolveAssessmentStartUtc(
            InspectionTimeUtc, assessmentWindowHours: 168, processStartedAtUtc: null);

        Assert.Equal(InspectionTimeUtc.AddDays(-7), line);

        // 回退路径也要真的参与判据：25 小时前的违规在 7 天窗内 → 窗内违规。
        var result = DataReliabilityInvariants.CheckS1_NoOverlap(
            new List<EventTimeSpan>
            {
                new() { EventId = "y1", DeviceId = "PC-1", EventType = "window", StartTime = InspectionTimeUtc.AddHours(-25), EndTime = InspectionTimeUtc.AddHours(-25).AddMinutes(10) },
                new() { EventId = "y2", DeviceId = "PC-1", EventType = "window", StartTime = InspectionTimeUtc.AddHours(-25).AddMinutes(5), EndTime = InspectionTimeUtc.AddHours(-25).AddMinutes(15) }
            },
            referenceTimeUtc: InspectionTimeUtc,
            assessmentStartUtc: line);

        Assert.Equal(InvariantStatus.Fail, result.Status);
        Assert.Equal(1, result.WindowViolations);
    }

    #endregion

    #region AC-5.1 / AC-5.5：考核窗与 S9 覆盖率窗口相互独立

    [Fact]
    public void Options_Defaults_AssessmentWindow168_CoverageWindow24()
    {
        var options = new InvariantOptions();

        Assert.Equal(168.0, options.AssessmentWindowHours);
        Assert.Equal(24.0, options.CoverageWindowHours);
        Assert.NotEqual(options.AssessmentWindowHours, options.CoverageWindowHours);
    }

    /// <summary>AC-5.5（反面）：改考核窗不得联动 S9 覆盖率窗口 —— 同一实例上同时验证。</summary>
    [Fact]
    public async Task Inspector_S9CoverageWindow_Stays24h_WhenAssessmentWindowChanges()
    {
        foreach (var assessmentWindowHours in new[] { 168.0, 24.0, 720.0 })
        {
            var conn = new RecordingDbConnection();
            var inspector = CreateInspector(conn, new InvariantOptions
            {
                AssessmentWindowHours = assessmentWindowHours,
                CoverageWindowHours = 24.0
            });

            await inspector.InspectReportAsync(InspectionTime);

            var coverageStart = FindBoundTimestamp(conn, sql => sql.Contains("@windowStart::timestamptz"), "@windowStart");
            Assert.Equal(InspectionTimeUtc.AddHours(-24), coverageStart);
        }
    }

    /// <summary>AC-5.5（反面）：改覆盖率窗口不得影响分档线（判据仍按考核窗分档）。</summary>
    [Fact]
    public async Task Inspector_ChangingCoverageWindow_DoesNotMoveAssessmentLine()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateInspector(conn, new InvariantOptions
        {
            AssessmentWindowHours = 168.0,
            CoverageWindowHours = 1.0
        });

        var report = await inspector.InspectReportAsync(InspectionTime);

        Assert.Equal(InspectionTimeUtc.AddDays(-7), report.AssessmentStartUtc.UtcDateTime);
        Assert.Equal(168.0, report.AssessmentWindowHours);
        var coverageStart = FindBoundTimestamp(conn, sql => sql.Contains("@windowStart::timestamptz"), "@windowStart");
        Assert.Equal(InspectionTimeUtc.AddHours(-1), coverageStart);
    }

    #endregion

    #region AC-5.6：取数下限归位到「≥ 考核窗」

    [Fact]
    public async Task Inspector_FetchLowerBounds_FollowAssessmentLine_NotCoverageWindow()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateInspector(conn, new InvariantOptions
        {
            AssessmentWindowHours = 168.0,
            CoverageWindowHours = 24.0
        });

        await inspector.InspectReportAsync(InspectionTime);

        var expected = InspectionTimeUtc.AddDays(-7);

        // S3 单日时长：只聚合考核窗内的事件行（AC-6.1 的取数侧）
        Assert.Equal(expected, FindBoundTimestamp(conn, sql => sql.Contains("as biz_date"), "@since"));
        // S4 采样取数（无重复项时的健康性采样）
        Assert.Equal(expected, FindBoundTimestamp(conn, sql => sql.Contains("SELECT device_id, timestamp"), "@since"));
        // S5 主取数
        Assert.Equal(expected, FindBoundTimestamp(conn, sql => sql.Contains("WHERE created_at >= @since"), "@since"));
    }

    [Fact]
    public async Task Inspector_FetchLowerBounds_FollowProcessStart_AfterRestart()
    {
        var processStart = InspectionTimeUtc.AddMinutes(-2);
        var conn = new RecordingDbConnection();
        var inspector = CreateInspector(
            conn,
            new InvariantOptions { AssessmentWindowHours = 168.0, CoverageWindowHours = 24.0 },
            processStartedAtUtc: processStart);

        var report = await inspector.InspectReportAsync(InspectionTime);

        Assert.Equal(processStart, report.AssessmentStartUtc.UtcDateTime);
        Assert.Equal(processStart, FindBoundTimestamp(conn, sql => sql.Contains("as biz_date"), "@since"));
        Assert.Equal(processStart, FindBoundTimestamp(conn, sql => sql.Contains("WHERE created_at >= @since"), "@since"));
    }

    /// <summary>反面（AC-5.6）：不得存在"仍按 24h 取数却参与考核窗判定"的尺子。</summary>
    [Fact]
    public async Task Inspector_NoFetchLowerBound_UsesCoverageWindowForAssessmentRules()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateInspector(conn, new InvariantOptions
        {
            AssessmentWindowHours = 168.0,
            CoverageWindowHours = 24.0
        });

        await inspector.InspectReportAsync(InspectionTime);

        var twentyFourHoursAgo = InspectionTimeUtc.AddHours(-24);

        // 三条"考核窗型取数"逐条按 SQL 形状精确定位（避免误匹配 S8 / S13 等同样以 device_id, timestamp 开头的取数）。
        var assessmentFetches = new (string Label, string Pattern)[]
        {
            ("S3 单日时长", @"as\s+biz_date"),
            ("S4 采样", @"SELECT\s+device_id,\s+timestamp\s+FROM\s+pc_tracker_events\s+WHERE\s+timestamp\s+>=\s+@since\s+ORDER BY id DESC"),
            ("S5 主取数", @"SELECT\s+id,\s+device_id,\s+timestamp,\s+created_at\s+FROM\s+pc_tracker_events\s+WHERE\s+created_at\s+>=\s+@since")
        };

        foreach (var (label, pattern) in assessmentFetches)
        {
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            var matches = conn.ExecutedCommands
                .Select((sql, index) => (Sql: sql, Parameters: conn.ExecutedParameterValues[index]))
                .Where(entry => regex.IsMatch(entry.Sql))
                .ToList();

            Assert.True(matches.Count == 1, $"{label} 取数应恰好 1 条，实际 {matches.Count} 条");
            var (sql, parameters) = matches[0];
            Assert.True(parameters.TryGetValue("@since", out var value), $"取数缺少 @since 绑定: {sql}");
            Assert.NotEqual(twentyFourHoursAgo, (DateTime)value!);
        }
    }

    #endregion

    #region 进程启动时刻：重启即重置账本，且不落库

    /// <summary>AC-1.5：模拟新进程启动（新启动时刻）→ 账本以新时刻为准；沿用旧时刻 = 失败。</summary>
    [Fact]
    public async Task Inspector_NewProcessStart_ResetsLedger_AndPersistsNothing()
    {
        var firstProcessStart = InspectionTimeUtc.AddDays(-3);
        var secondProcessStart = InspectionTimeUtc.AddMinutes(-2);

        var firstConn = new RecordingDbConnection();
        var first = await CreateInspector(
            firstConn,
            new InvariantOptions(),
            processStartedAtUtc: firstProcessStart).InspectReportAsync(InspectionTime);

        var secondConn = new RecordingDbConnection();
        var second = await CreateInspector(
            secondConn,
            new InvariantOptions(),
            processStartedAtUtc: secondProcessStart).InspectReportAsync(InspectionTime);

        Assert.Equal(firstProcessStart, first.AssessmentStartUtc.UtcDateTime);
        Assert.Equal(secondProcessStart, second.AssessmentStartUtc.UtcDateTime);
        Assert.NotEqual(first.AssessmentStartUtc, second.AssessmentStartUtc);

        // 进程启动时刻只在内存：体检链路不得出现任何写操作（账本重置靠重启，不靠改数据）。
        var writeKeyword = new System.Text.RegularExpressions.Regex(
            @"\b(INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|TRUNCATE|COPY|GRANT)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var sql in secondConn.ExecutedCommands)
        {
            Assert.False(writeKeyword.IsMatch(sql), $"体检链路出现了写操作（进程启动时刻不得落库）: {sql}");
        }
    }

    #endregion

    #region AC-5.3：非法配置回退默认并在结果中标注

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(721.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Options_InvalidAssessmentWindow_FallsBackToDefaultWithNote(double invalidHours)
    {
        var (resolved, fallback, note) = InvariantOptions.Resolve(new InvariantOptions
        {
            AssessmentWindowHours = invalidHours
        });

        Assert.True(fallback);
        Assert.NotNull(note);
        Assert.Contains("AssessmentWindowHours", note!, StringComparison.Ordinal);
        Assert.Equal(168.0, resolved.AssessmentWindowHours);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(721.0)]
    [InlineData(double.NaN)]
    public void Options_InvalidCoverageWindow_FallsBackToDefaultWithNote(double invalidHours)
    {
        var (resolved, fallback, note) = InvariantOptions.Resolve(new InvariantOptions
        {
            CoverageWindowHours = invalidHours
        });

        Assert.True(fallback);
        Assert.NotNull(note);
        Assert.Contains("CoverageWindowHours", note!, StringComparison.Ordinal);
        Assert.Equal(24.0, resolved.CoverageWindowHours);
    }

    [Fact]
    public async Task Inspector_InvalidAssessmentWindow_IsReportedAsNotice()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateInspector(conn, new InvariantOptions { AssessmentWindowHours = 721.0 });

        var report = await inspector.InspectReportAsync(InspectionTime);

        Assert.Equal(168.0, report.AssessmentWindowHours);
        Assert.Equal(InspectionTimeUtc.AddDays(-7), report.AssessmentStartUtc.UtcDateTime);
        Assert.True(report.Notices.ContainsKey("options_fallback"), "非法配置必须在体检结果里显式标注");
        Assert.Contains("AssessmentWindowHours", report.Notices["options_fallback"], StringComparison.Ordinal);
    }

    #endregion

    #region AC-2.4 / AC-2.7：总览只由窗内违规产生

    [Fact]
    public void Summary_HistoricalDebt_DoesNotEnterOverviewTotalsOrStatus()
    {
        var rules = new List<DataReliabilityRuleReport>
        {
            Rule("S1", "red", windowViolations: 3, historicalViolations: 100),
            Rule("S2", "green", windowViolations: 0, historicalViolations: 4784),
            Rule("S11", "green", windowViolations: 0, historicalViolations: 2301)
        };

        var summary = DataReliabilityInspectionSummary.Compute(rules);

        Assert.Equal(3, summary.TotalViolations);
        Assert.Equal(3, summary.WindowViolations);
        Assert.Equal(7185, summary.HistoricalViolations);
        Assert.Equal("red", summary.Status);
        Assert.Equal(1, summary.RedCount);
        Assert.Equal(2, summary.GreenCount);
    }

    [Fact]
    public void Summary_OnlyDebtEverywhere_IsGreen()
    {
        var rules = new List<DataReliabilityRuleReport>
        {
            Rule("S1", "green", windowViolations: 0, historicalViolations: 4784),
            Rule("S4", "green", windowViolations: 0, historicalViolations: 195),
            Rule("S11", "green", windowViolations: 0, historicalViolations: 2301)
        };

        var summary = DataReliabilityInspectionSummary.Compute(rules);

        Assert.Equal("green", summary.Status);
        Assert.Equal(0, summary.TotalViolations);
        Assert.Equal(7280, summary.HistoricalViolations);
        Assert.Equal(3, summary.GreenCount);
    }

    [Fact]
    public void Summary_GreenWithDebt_StillCountsDebtForTheLedgerLine()
    {
        var rules = new List<DataReliabilityRuleReport>
        {
            Rule("S13", "green", windowViolations: 0, historicalViolations: 7)
        };

        var summary = DataReliabilityInspectionSummary.Compute(rules);

        Assert.Equal("green", summary.Status);
        Assert.Equal(0, summary.WindowViolations);
        Assert.Equal(7, summary.HistoricalViolations);
    }

    #endregion

    #region 夹具

    private static DataReliabilityRuleReport Rule(string code, string status, int windowViolations, int historicalViolations) =>
        new(
            Code: code,
            InvariantCode: $"INV-{code}",
            Key: $"{code}_INV",
            Order: 1,
            Name: code,
            Group: "SelfConsistency",
            GroupLabel: "数据自洽",
            Status: status,
            StatusLabel: status,
            Detail: string.Empty,
            CurrentValue: null,
            CurrentValueUnit: null,
            CurrentValueLabel: null,
            Threshold: string.Empty,
            Criterion: string.Empty,
            Rationale: string.Empty,
            RelatedIssues: Array.Empty<int>(),
            TotalViolations: windowViolations + historicalViolations,
            WindowViolations: windowViolations,
            HistoricalViolations: historicalViolations,
            EarliestOccurrenceUtc: null,
            LatestOccurrenceUtc: null,
            Samples: Array.Empty<string>(),
            ThresholdFallback: false,
            ThresholdNote: null,
            CoveredLayers: null,
            Trend: "unknown",
            TrendDelta: null,
            TrendBaselineUtc: null,
            ThreeState: null,
            ScanTruncated: false);

    private sealed class FixedProcessStartTimeProvider : IProcessStartTimeProvider
    {
        public FixedProcessStartTimeProvider(DateTime startedAtUtc) => ProcessStartedAtUtc = new DateTimeOffset(startedAtUtc, TimeSpan.Zero);

        public DateTimeOffset ProcessStartedAtUtc { get; }
    }

    private static DataReliabilityQualityInspector CreateInspector(
        RecordingDbConnection conn,
        InvariantOptions options,
        DateTime? processStartedAtUtc = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PimDbContext>();
        optionsBuilder.UseNpgsql(conn);
        var db = new PimDbContext(optionsBuilder.Options);

        return new DataReliabilityQualityInspector(
            db,
            Options.Create(options),
            NullLogger<DataReliabilityQualityInspector>.Instance,
            processStartTimeProvider: processStartedAtUtc is null
                ? null
                : new FixedProcessStartTimeProvider(processStartedAtUtc.Value));
    }

    /// <summary>取某条 SQL 上绑定的时间参数值（用于钉住"取数下限到底绑了哪个窗口"）。</summary>
    private static DateTime FindBoundTimestamp(RecordingDbConnection conn, Func<string, bool> sqlPredicate, string parameterName)
    {
        var matches = conn.ExecutedCommands
            .Select((sql, index) => (Sql: sql, Parameters: conn.ExecutedParameterValues[index]))
            .Where(entry => sqlPredicate(entry.Sql))
            .ToList();

        Assert.True(matches.Count > 0, "没有找到匹配的取数语句，断言目标不存在");
        var (sql, parameters) = matches[0];
        Assert.True(parameters.TryGetValue(parameterName, out var value), $"语句缺少参数 {parameterName}: {sql}");
        Assert.IsType<DateTime>(value);
        return (DateTime)value!;
    }

    #endregion
}
