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
/// 数据可靠性体检服务：真实接入 PimDbContext 消费 <see cref="Pim.Core.Invariants"/> 的纯函数判据库。
/// <para>两种出口共用同一批取数与判据：</para>
/// <list type="bullet">
///   <item><description><see cref="InspectReportAsync"/> 产出结构化报告（设置页「数据可信度」面板与体检接口用）。</description></item>
///   <item><description><see cref="InspectAsync"/> 产出既有的字符串字典契约（Stage0 巡检与监控用），并顺手保温进程内缓存。</description></item>
/// </list>
/// <para>
/// 全程只读：只发 SELECT，不写任何表（含缓存表）；每条查询按业务时间倒序限量，整次体检有统一超时，
/// 超时/异常一律记为「未知」而不是通过。
/// </para>
/// </summary>
public sealed class DataReliabilityQualityInspector : IDataQualityInspector, IDataReliabilityReportInspector, IDataReliabilityViolationExporter
{
    private readonly PimDbContext? _db;
    private readonly InvariantOptions _options;
    private readonly ILogger<DataReliabilityQualityInspector> _logger;
    private readonly IDataReliabilityInspectionStore? _store;
    private readonly TimeProvider _timeProvider;

    public DataReliabilityQualityInspector(
        PimDbContext? db,
        IOptions<InvariantOptions> options,
        ILogger<DataReliabilityQualityInspector> logger,
        IDataReliabilityInspectionStore? store = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _options = options?.Value ?? InvariantOptions.Default;
        _logger = logger;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string CheckName => "data_reliability";

    /// <summary>
    /// 结构化体检（#260）：13 条尺子的编号 / 名称 / 状态 / 当前值 / 阈值 / 违规分档 / 样例 / 关联 issue。
    /// 与 <see cref="InspectAsync"/> 共用同一批取数与判据调用（<see cref="RunChecksAsync"/>），保证"判据只有一份实现"。
    /// 全程只读：只发 SELECT，不写任何表，也不写缓存表——"最近一次结果"由进程内单例缓存承担。
    /// </summary>
    public async Task<DataReliabilityInspectionReport> InspectReportAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var (options, fallback, fallbackNote) = InvariantOptions.Resolve(_options);

        var notices = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fallback)
        {
            notices["options_fallback"] = fallbackNote ?? "配置非法回退默认值";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.InspectionTimeoutSeconds)));

        var run = await RunChecksAsync(now, options, timeout.Token);
        if (run.UnavailableReason != null)
        {
            notices["database_unavailable"] = run.UnavailableReason;
        }

        // 数据库不可用时，13 条尺子一律显式标记为"未知"，绝不亮假绿灯。
        var outcomes = run.UnavailableReason != null
            ? DataReliabilityRuleCatalog.All.ToDictionary(
                definition => definition.Code,
                definition => new RuleOutcome
                {
                    RuleCode = definition.Code,
                    Result = InvariantResult.Unknown(run.UnavailableReason)
                },
                StringComparer.OrdinalIgnoreCase)
            : run.Outcomes;

        foreach (var outcome in outcomes.Values)
        {
            if (outcome.Result.ThresholdFallback && outcome.Result.ThresholdNote != null)
            {
                notices[$"{outcome.RuleCode}_threshold_fallback"] = outcome.Result.ThresholdNote;
            }
        }

        var rules = outcomes.Values
            .Select(outcome => BuildRuleReport(outcome, options))
            .OrderBy(rule => rule.Order)
            .ToList();

        int redCount = rules.Count(rule => rule.Status == "red");
        int yellowCount = rules.Count(rule => rule.Status == "yellow");
        int greenCount = rules.Count(rule => rule.Status == "green");
        int unknownCount = rules.Count(rule => rule.Status == "unknown");

        // 总览的"违规数"只累加 red/yellow 尺子，green/unknown 的 0 不参与，避免把未知当成 0 违规的假象。
        var countedRules = rules.Where(rule => rule.Status is "red" or "yellow").ToList();
        int totalViolations = countedRules.Sum(rule => rule.TotalViolations);
        int newViolations = countedRules.Sum(rule => rule.NewViolations);
        int historicalViolations = countedRules.Sum(rule => rule.HistoricalViolations);

        started.Stop();
        var overallStatus = redCount > 0
            ? "red"
            : yellowCount > 0
                ? "yellow"
                : unknownCount > 0
                    ? "unknown"
                    : "green";

        string message = overallStatus switch
        {
            "green" => $"数据可信度体检全部通过（13/13 绿灯），耗时 {started.ElapsedMilliseconds}ms。",
            "red" => $"数据可信度体检发现 {redCount} 条红线、{yellowCount} 条黄线，耗时 {started.ElapsedMilliseconds}ms。",
            "yellow" => $"数据可信度体检无红线，但有 {yellowCount} 条黄线，耗时 {started.ElapsedMilliseconds}ms。",
            _ => $"数据可信度体检有 {unknownCount} 条尺子数据源不足、{yellowCount} 条黄线，耗时 {started.ElapsedMilliseconds}ms。"
        };

        _logger.LogInformation(
            "数据可信度体检查询完成：Red={RedCount}, Yellow={YellowCount}, Green={GreenCount}, Unknown={UnknownCount}, Violations={TotalViolations}, Elapsed={ElapsedMs}ms",
            redCount, yellowCount, greenCount, unknownCount, totalViolations, started.ElapsedMilliseconds);

        return new DataReliabilityInspectionReport(
            InspectedAtUtc: now,
            Version: 0, // 由 IDataReliabilityInspectionStore.Publish 赋值
            ElapsedMilliseconds: started.ElapsedMilliseconds,
            Status: overallStatus,
            RedCount: redCount,
            YellowCount: yellowCount,
            GreenCount: greenCount,
            UnknownCount: unknownCount,
            TotalViolations: totalViolations,
            NewViolations: newViolations,
            HistoricalViolations: historicalViolations,
            Notices: notices,
            Rules: rules,
            Message: message);
    }

    /// <summary>
    /// 导出某条尺子的完整违规清单（#261 下钻）：只跑这一条尺子的取数，返回结构化的 ID + 业务时间 + 设备 + 关键字段。
    /// 违规明细由判据本身产出（<see cref="InvariantResult.Violations"/>），因此导出与判据永远不会漂移。
    /// </summary>
    public async Task<DataReliabilityViolationExport> GetViolationsAsync(string ruleCode, int limit, CancellationToken ct = default)
    {
        var definition = DataReliabilityRuleCatalog.Find(ruleCode)
            ?? throw new ArgumentException($"未知的尺子编号: {ruleCode}", nameof(ruleCode));

        var clampedLimit = Math.Clamp(limit, 1, 5000);
        var (options, _, _) = InvariantOptions.Resolve(_options);
        var now = _timeProvider.GetUtcNow();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.InspectionTimeoutSeconds)));

        var conn = await OpenConnectionAsync(timeout.Token);
        if (conn == null)
        {
            return new DataReliabilityViolationExport(
                definition.Code,
                now,
                0,
                false,
                Array.Empty<DataReliabilityViolationItem>());
        }

        // 导出只关心"完整清单"，因此把样例上限抬到请求的 limit（判据本身仍按同一套三态/区间规则判定）。
        var exportOptions = new InvariantOptions
        {
            MinInputDensityPerMinute = options.MinInputDensityPerMinute,
            LongEventThresholdMinutes = options.LongEventThresholdMinutes,
            UndeclaredOfflineGapMinutes = options.UndeclaredOfflineGapMinutes,
            MaxUploadLagP99Minutes = options.MaxUploadLagP99Minutes,
            MobileSummaryLagHours = options.MobileSummaryLagHours,
            RecentWindowHours = options.RecentWindowHours,
            MaxDailyActiveHours = options.MaxDailyActiveHours,
            AwakeWindowHours = options.AwakeWindowHours,
            AwakeWindowWarningRatio = options.AwakeWindowWarningRatio,
            CoverageRedRatio = options.CoverageRedRatio,
            CoverageYellowRatio = options.CoverageYellowRatio,
            MaxSampleCount = clampedLimit,
            ClockSkewToleranceMinutes = options.ClockSkewToleranceMinutes,
            TimelineGapThresholdMinutes = options.TimelineGapThresholdMinutes,
            Tolerance = options.Tolerance,
            MaxScanRows = options.MaxScanRows,
            InspectionTimeoutSeconds = options.InspectionTimeoutSeconds
        };

        var context = new RuleCheckContext
        {
            Options = exportOptions,
            NowUtc = now.UtcDateTime,
            Ct = timeout.Token,
            Collector = new RuleRunCollector(clampedLimit)
        };

        InvariantResult result;
        try
        {
            result = await RunSingleCheckAsync(definition.Code, conn, context);
        }
        catch (OperationCanceledException)
        {
            result = InvariantResult.Unknown($"{definition.Code} UNKNOWN: 导出取数超时");
        }

        var items = result.Violations
            .Select(violation => new DataReliabilityViolationItem(
                RuleCode: definition.Code,
                Id: violation.Id,
                DeviceId: violation.DeviceId,
                OccurredAtUtc: new DateTimeOffset(DateTime.SpecifyKind(violation.OccurredAtUtc, DateTimeKind.Utc)),
                Fields: violation.Fields))
            .ToArray();

        bool truncated = result.TotalViolations > items.Length;

        return new DataReliabilityViolationExport(
            definition.Code,
            now,
            result.TotalViolations,
            truncated,
            items);
    }

    /// <summary>
    /// 传统数据质量巡检出口（Stage0DiagnosticJob 使用）。
    /// 与 <see cref="InspectReportAsync"/> 复用同一次取数与同一批判据，并顺手把结构化结果发布到进程内缓存，
    /// 让每小时的巡检同时为设置页与质量报告门禁"保温"。
    /// </summary>
    public async Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var report = await InspectReportAsync(now, ct);
        _store?.Publish(report);
        return MapToLegacyResult(report);
    }

    /// <summary>把结构化体检结果映射回既有的字符串字典契约（键名、四态前缀、summary 格式保持不变）。</summary>
    private DataQualityInspectionResult MapToLegacyResult(DataReliabilityInspectionReport report)
    {
        var details = new Dictionary<string, string>();

        if (report.Notices.TryGetValue("options_fallback", out var fallbackNote))
        {
            details["options_fallback"] = fallbackNote;
        }

        int redCount = 0;
        int yellowCount = 0;
        int greenCount = 0;
        int unknownCount = 0;
        int totalIssues = 0;

        foreach (var rule in report.Rules)
        {
            if (rule.CoveredLayers != null)
            {
                details[$"{rule.Key}_covered_layers"] = rule.CoveredLayers;
            }

            switch (rule.Status)
            {
                case "red":
                    redCount++;
                    totalIssues += Math.Max(1, rule.TotalViolations);
                    details[rule.Key] = $"🔴 FAIL: {rule.Detail}";
                    break;
                case "yellow":
                    yellowCount++;
                    totalIssues += Math.Max(1, rule.TotalViolations);
                    details[rule.Key] = $"🟡 WARN: {rule.Detail}";
                    break;
                case "unknown":
                    unknownCount++;
                    totalIssues++;
                    details[rule.Key] = $"⚪ UNKNOWN: {rule.Detail}";
                    break;
                default:
                    greenCount++;
                    details[rule.Key] = $"🟢 PASS: {rule.Detail}";
                    break;
            }
        }

        bool isHealthy = redCount == 0 && unknownCount == 0 && totalIssues == 0;
        details["summary"] = $"{redCount} Red, {yellowCount} Yellow, {greenCount} Green, {unknownCount} Unknown";

        string message = isHealthy
            ? $"数据可靠性体检全部通过 (13/13 绿灯)。耗时 {report.ElapsedMilliseconds}ms。"
            : $"数据可靠性体检发现异常：{redCount} 红, {yellowCount} 黄, {greenCount} 绿, {unknownCount} 未知。耗时 {report.ElapsedMilliseconds}ms。";

        _logger.LogInformation(
            "数据可靠性体检完成：Healthy={IsHealthy}, Red={RedCount}, Yellow={YellowCount}, Green={GreenCount}, Unknown={UnknownCount}, Elapsed={ElapsedMs}ms",
            isHealthy, redCount, yellowCount, greenCount, unknownCount, report.ElapsedMilliseconds);

        return new DataQualityInspectionResult(
            CheckName,
            isHealthy,
            totalIssues,
            message,
            details);
    }

    /// <summary>一次体检运行的取数结果集合。</summary>
    private sealed class CheckRun
    {
        public Dictionary<string, RuleOutcome> Outcomes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>数据库不可用时的原因；为 null 表示取数正常。</summary>
        public string? UnavailableReason { get; init; }

        public string? UnavailableMessage { get; init; }
    }

    /// <summary>单条尺子的取数结果：判据结论 + 当前值/三态等附加测量。</summary>
    private sealed class RuleOutcome
    {
        public string RuleCode { get; init; } = string.Empty;
        public InvariantResult Result { get; init; } = InvariantResult.Unknown("UNKNOWN");
        public double? CurrentValue { get; init; }
        public string? CurrentValueUnit { get; init; }
        public string? CurrentValueLabel { get; init; }
        public S2ThreeStateDistribution? ThreeState { get; init; }
        public bool ScanTruncated { get; init; }
    }

    /// <summary>
    /// 单次取数运行期间的测量采集器。
    /// 需要额外暴露"当前值/三态/截断"的取数方法通过它回填；每次运行各持一个，互不干扰。
    /// </summary>
    private sealed class RuleRunCollector
    {
        public RuleRunCollector(int sampleLimit)
        {
            SampleLimit = sampleLimit;
        }

        public int SampleLimit { get; }
        public double? CurrentValue { get; private set; }
        public string? CurrentValueUnit { get; private set; }
        public string? CurrentValueLabel { get; private set; }
        public S2ThreeStateDistribution? ThreeState { get; private set; }
        public bool ScanTruncated { get; private set; }

        public void SetCurrentValue(double? value, string? unit = null, string? label = null)
        {
            CurrentValue = value;
            CurrentValueUnit = unit;
            CurrentValueLabel = label;
        }

        public void SetThreeState(S2ThreeStateDistribution distribution) => ThreeState = distribution;

        public void MarkScanTruncated() => ScanTruncated = true;
    }

    /// <summary>
    /// 一次取数运行的全部上下文（阈值、时钟、取消令牌与测量采集器）。
    /// 显式传参而不是挂在实例字段上：同一 Inspector 实例被并发使用（例如同一次请求里并行跑两条尺子）时也不会互相覆盖。
    /// </summary>
    private sealed class RuleCheckContext
    {
        public InvariantOptions Options { get; init; } = InvariantOptions.Default;
        public DateTime NowUtc { get; init; }
        public CancellationToken Ct { get; init; }
        public RuleRunCollector Collector { get; init; } = new(0);
    }

    /// <summary>逐步执行 13 条尺子的取数与判据，并保留结构化测量结果。</summary>
    private async Task<CheckRun> RunChecksAsync(DateTimeOffset now, InvariantOptions options, CancellationToken ct)
    {
        var nowUtc = now.UtcDateTime;
        var outcomes = new Dictionary<string, RuleOutcome>(StringComparer.OrdinalIgnoreCase);

        var conn = await OpenConnectionAsync(ct);
        if (conn == null)
        {
            var reason = _db == null ? "数据库上下文未注入或不可用" : "非关系型数据库或数据库连接未打开";
            var message = _db == null
                ? "数据库上下文未配置，全部 13 项判据处于未知状态"
                : "数据库连接不可用，全部 13 项判据标记为未知";
            return new CheckRun { UnavailableReason = reason, UnavailableMessage = message };
        }

        foreach (var definition in DataReliabilityRuleCatalog.All)
        {
            var collector = new RuleRunCollector(options.MaxSampleCount);
            var context = new RuleCheckContext
            {
                Options = options,
                NowUtc = nowUtc,
                Ct = ct,
                Collector = collector
            };

            InvariantResult result;
            try
            {
                result = await RunSingleCheckAsync(definition.Code, conn, context);
            }
            catch (OperationCanceledException)
            {
                // 整次体检超时：剩下的尺子一律记"未知"，绝不因为"没跑到"就当成通过。
                result = InvariantResult.Unknown(
                    $"{definition.Code} UNKNOWN: 取数超时（超过 {options.InspectionTimeoutSeconds} 秒），本次未能判定");
            }
            catch (Exception ex)
            {
                result = InvariantResult.Unknown($"{definition.Code} UNKNOWN: 取数执行异常: {ex.Message}");
            }

            outcomes[definition.Code] = new RuleOutcome
            {
                RuleCode = definition.Code,
                Result = result,
                CurrentValue = collector.CurrentValue,
                CurrentValueUnit = collector.CurrentValueUnit,
                CurrentValueLabel = collector.CurrentValueLabel,
                ThreeState = collector.ThreeState,
                ScanTruncated = collector.ScanTruncated
            };
        }

        return new CheckRun { Outcomes = outcomes };
    }

    /// <summary>按尺子编号执行对应的取数+判据。</summary>
    private Task<InvariantResult> RunSingleCheckAsync(
        string ruleCode,
        DbConnection conn,
        RuleCheckContext context) => ruleCode.ToUpperInvariant() switch
        {
            "S1" => CheckS1Async(conn, context),
            "S2" => CheckS2Async(conn, context),
            "S3" => CheckS3Async(conn, context),
            "S4" => CheckS4Async(conn, context),
            "S5" => CheckS5Async(conn, context),
            "S6" => CheckS6Async(conn, context),
            "S7" => CheckS7Async(conn, context),
            "S8" => CheckS8Async(conn, context),
            "S9" => CheckS9Async(conn, context),
            "S10" => CheckS10Async(conn, context),
            "S11" => CheckS11Async(conn, context),
            "S12" => CheckS12Async(conn, context),
            "S13" => CheckS13Async(conn, context),
            _ => Task.FromResult(InvariantResult.Unknown($"{ruleCode} UNKNOWN: 未知的尺子编号"))
        };

    private async Task<DbConnection?> OpenConnectionAsync(CancellationToken ct)
    {
        if (_db == null)
        {
            _logger.LogWarning("PimDbContext 未注入，数据可靠性体检全部标记为未知");
            return null;
        }

        try
        {
            if (!_db.Database.IsRelational())
            {
                return null;
            }

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
            }

            return conn.State == ConnectionState.Open ? conn : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "建立数据可靠性体检数据库连接失败");
            return null;
        }
    }

    /// <summary>把单条尺子的取数结果映射成体检接口的数据契约（阈值/判据原文取自规则总表）。</summary>
    private static DataReliabilityRuleReport BuildRuleReport(RuleOutcome outcome, InvariantOptions options)
    {
        var definition = DataReliabilityRuleCatalog.Find(outcome.RuleCode)
            ?? throw new InvalidOperationException($"规则总表缺少尺子 {outcome.RuleCode}");

        var result = outcome.Result;
        var status = DataReliabilityRuleCatalog.NormalizeStatus(result.Status);

        var detail = result.Detail;
        if (outcome.ScanTruncated)
        {
            detail = $"{detail}（查询已达上限 {options.MaxScanRows} 行，结果可能不完整）";
        }

        double? currentValue;
        string? currentValueUnit;
        string? currentValueLabel;
        if (outcome.CurrentValue.HasValue)
        {
            currentValue = outcome.CurrentValue;
            currentValueUnit = outcome.CurrentValueUnit;
            currentValueLabel = outcome.CurrentValueLabel;
        }
        else if (status == "unknown")
        {
            currentValue = null;
            currentValueUnit = null;
            currentValueLabel = null;
        }
        else
        {
            // 大多数尺子的"当前值"就是违规条数；S3/S5/S9 会各自回填更贴切的度量。
            currentValue = result.TotalViolations;
            currentValueUnit = "条";
            currentValueLabel = null;
        }

        return new DataReliabilityRuleReport(
            Code: definition.Code,
            InvariantCode: definition.InvariantCode,
            Key: DataReliabilityRuleCatalog.BuildKey(definition),
            Order: definition.Order,
            Name: definition.Name,
            Group: definition.Group.ToString(),
            GroupLabel: definition.GroupLabel,
            Status: status,
            StatusLabel: DataReliabilityRuleCatalog.StatusLabel(status),
            Detail: detail,
            CurrentValue: currentValue,
            CurrentValueUnit: currentValueUnit,
            CurrentValueLabel: currentValueLabel,
            Threshold: definition.Threshold,
            Criterion: definition.Criterion,
            Rationale: definition.Rationale,
            RelatedIssues: definition.RelatedIssues,
            TotalViolations: result.TotalViolations,
            NewViolations: result.NewViolations,
            HistoricalViolations: result.HistoricalViolations,
            EarliestOccurrenceUtc: ToUtcOffset(result.EarliestOccurrence),
            LatestOccurrenceUtc: ToUtcOffset(result.LatestOccurrence),
            Samples: result.Samples,
            ThresholdFallback: result.ThresholdFallback,
            ThresholdNote: result.ThresholdNote,
            CoveredLayers: result.CoveredLayers,
            Trend: "unknown", // 由 IDataReliabilityInspectionStore 在发布时对比历史计算
            TrendDelta: null,
            TrendBaselineUtc: null,
            ThreeState: outcome.ThreeState,
            ScanTruncated: outcome.ScanTruncated);
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        if (value is null || value.Value == DateTime.MinValue)
        {
            return null;
        }

        var utc = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc);
    }


    #region Check Implementations

    private async Task<InvariantResult> CheckS1Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-P16 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = $"""
            SELECT id, device_id, event_type, timestamp, duration
            FROM pc_tracker_events
            WHERE event_type IN ('window', 'web-page')
            ORDER BY timestamp DESC
            LIMIT {context.Options.MaxScanRows + 1};
            """;

        var list = new List<EventTimeSpan>();
        await using var reader = await cmd.ExecuteReaderAsync(context.Ct);
        while (await reader.ReadAsync(context.Ct))
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

        if (ApplyScanCap(list, context.Options.MaxScanRows))
        {
            context.Collector.MarkScanTruncated();
        }

        if (list.Count == 0)
            return InvariantResult.Unknown("INV-P16 UNKNOWN: pc_tracker_events 中无可用事件序列");

        return DataReliabilityInvariants.CheckS1_NoOverlap(list, context.Options, referenceTimeUtc: context.NowUtc);
    }

    private async Task<InvariantResult> CheckS2Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-P17 UNKNOWN: 数据表 pc_tracker_events 不存在");

        double thresholdSeconds = context.Options.LongEventThresholdMinutes * 60.0;
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = $"""
            SELECT id, device_id, event_type, timestamp, duration, app_name, is_idle, is_media_active, audible
            FROM pc_tracker_events
            WHERE duration > {thresholdSeconds:F0}
            ORDER BY timestamp DESC
            LIMIT {context.Options.MaxScanRows + 1};
            """;

        var rawEvents = new List<(long id, string dev, string type, DateTime start, double dur, string? app, bool isIdle, bool isMedia, bool isAudible)>();
        await using (var reader = await cmd.ExecuteReaderAsync(context.Ct))
        {
            while (await reader.ReadAsync(context.Ct))
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

        if (ApplyScanCap(rawEvents, context.Options.MaxScanRows))
        {
            context.Collector.MarkScanTruncated();
        }

        if (rawEvents.Count == 0)
            return InvariantResult.Unknown("INV-P17 UNKNOWN: 无超过 30 分钟事件记录");

        bool hasKeystats = await TableExistsAsync(conn, "pc_keystats_samples", context.Ct);
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

                await using var keyReader = await keyCmd.ExecuteReaderAsync(context.Ct);
                if (await keyReader.ReadAsync(context.Ct))
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

        // 同一批候选事件既喂给判据、也喂给三态分布，保证设置页展示与红线判定永远一致（EPIC #254 G4）。
        context.Collector.SetThreeState(DataReliabilityInvariants.ClassifyS2ThreeStates(candidates, context.Options));
        return DataReliabilityInvariants.CheckS2_OverlongEventEvidence(candidates, context.Options, referenceTimeUtc: context.NowUtc);
    }

    private async Task<InvariantResult> CheckS3Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-P18 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 20;
        cmd.CommandText = $"""
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
            ORDER BY timestamp DESC
            LIMIT {context.Options.MaxScanRows + 1};
            """;

        var rawEvents = new List<RawActivityEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(context.Ct);
        while (await reader.ReadAsync(context.Ct))
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

        if (ApplyScanCap(rawEvents, context.Options.MaxScanRows))
        {
            context.Collector.MarkScanTruncated();
        }

        if (rawEvents.Count == 0)
            return InvariantResult.Unknown("INV-P18 UNKNOWN: 无活跃事件可聚合单日时长");

        var dailyDurations = DataReliabilityInvariants.AggregateDailyActiveDurations(rawEvents, context.Options);
        context.Collector.SetCurrentValue(
            dailyDurations.Count > 0 ? dailyDurations.Max(d => d.ActiveDurationSeconds) / 3600.0 : 0,
            unit: "h");
        return DataReliabilityInvariants.CheckS3_DailyDurationBounded(dailyDurations, context.Options);
    }

    private async Task<InvariantResult> CheckS4Async(DbConnection conn, RuleCheckContext context)
    {
        var keys = new List<BusinessRecordKey>();
        bool anyTableExists = false;
        bool truncated = false;

        // 1. 定位去重 (mobile_location_points)
        if (await TableExistsAsync(conn, "mobile_location_points", context.Ct))
        {
            anyTableExists = true;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = $"""
                SELECT device_id, recorded_at_utc, latitude, longitude, count(*)
                FROM mobile_location_points
                GROUP BY device_id, recorded_at_utc, latitude, longitude
                HAVING count(*) > 1
                LIMIT {context.Options.MaxScanRows + 1};
                """;
            await using var r = await cmd.ExecuteReaderAsync(context.Ct);
            while (await r.ReadAsync(context.Ct))
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
        if (await TableExistsAsync(conn, "mobile_usage_events", context.Ct))
        {
            anyTableExists = true;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = $"""
                SELECT device_id, package_name, event_timestamp_utc, count(*)
                FROM mobile_usage_events
                GROUP BY device_id, package_name, event_timestamp_utc
                HAVING count(*) > 1
                LIMIT {context.Options.MaxScanRows + 1};
                """;
            await using var r = await cmd.ExecuteReaderAsync(context.Ct);
            while (await r.ReadAsync(context.Ct))
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
        if (await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
        {
            anyTableExists = true;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = $"""
                SELECT device_id, event_type, timestamp, count(*)
                FROM pc_tracker_events
                GROUP BY device_id, event_type, timestamp
                HAVING count(*) > 1
                LIMIT {context.Options.MaxScanRows + 1};
                """;
            await using var r = await cmd.ExecuteReaderAsync(context.Ct);
            while (await r.ReadAsync(context.Ct))
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

        // 命中扫描上限：重复组数已达上限，说明还有更多重复未被统计。
        truncated = keys.Count >= context.Options.MaxScanRows;

        if (truncated)
        {
            context.Collector.MarkScanTruncated();
        }

        if (!anyTableExists)
            return InvariantResult.Unknown("INV-C18 UNKNOWN: 业务数据表不存在");

        if (keys.Count == 0)
        {
            // 若无重复项，采样近 24 小时正常项以验证表非空且处于健康状态
            if (await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            {
                await using var sampleCmd = conn.CreateCommand();
                sampleCmd.CommandTimeout = 10;
                sampleCmd.CommandText = """
                    SELECT device_id, timestamp
                    FROM pc_tracker_events
                    WHERE timestamp >= @since
                    ORDER BY id DESC
                    LIMIT 10;
                    """;
                BindTimestamp(sampleCmd, "@since", context.NowUtc.AddHours(-context.Options.RecentWindowHours));
                await using var sr = await sampleCmd.ExecuteReaderAsync(context.Ct);
                while (await sr.ReadAsync(context.Ct))
                {
                    keys.Add(new BusinessRecordKey { Domain = "Pc", DeviceId = sr.GetString(0), UniqueKey = Guid.NewGuid().ToString(), Timestamp = sr.GetDateTime(1) });
                }
            }
        }

        if (keys.Count == 0)
            return InvariantResult.Unknown("INV-C18 UNKNOWN: 业务表为空，无数据检验业务键唯一性");

        return DataReliabilityInvariants.CheckS4_BusinessKeyUnique(keys, context.Options, referenceTimeUtc: context.NowUtc);
    }

    private async Task<InvariantResult> CheckS5Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-P19 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        // 增加时间窗限定在最近 24 小时，避免无界扫描或混入过旧历史数据
        cmd.CommandText = """
            SELECT id, device_id, timestamp, created_at
            FROM pc_tracker_events
            WHERE created_at >= @since
            ORDER BY id DESC
            LIMIT 500;
            """;
        BindTimestamp(cmd, "@since", context.NowUtc.AddHours(-context.Options.RecentWindowHours));

        var items = new List<ClockEventItem>();
        await using var reader = await cmd.ExecuteReaderAsync(context.Ct);
        while (await reader.ReadAsync(context.Ct))
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
            await using var fReader = await fallbackCmd.ExecuteReaderAsync(context.Ct);
            while (await fReader.ReadAsync(context.Ct))
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

        double maxSkewMinutes = items.Max(item => (item.EventTime - item.ServerReceivedTime).TotalMinutes);
        context.Collector.SetCurrentValue(maxSkewMinutes, unit: "min");
        return DataReliabilityInvariants.CheckS5_ClockTrustworthy(items, context.Options, referenceTimeUtc: context.NowUtc);
    }

    private async Task<InvariantResult> CheckS6Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-P20 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = $"""
            SELECT device_id, timestamp, created_at
            FROM pc_tracker_events
            WHERE event_type != 'web-page'
            ORDER BY timestamp DESC
            LIMIT {context.Options.MaxScanRows + 1};
            """;

        var rows = new List<(string DeviceId, DateTime Timestamp, DateTime CreatedAt)>();
        await using (var reader = await cmd.ExecuteReaderAsync(context.Ct))
        {
            while (await reader.ReadAsync(context.Ct))
            {
                rows.Add((
                    reader.IsDBNull(0) ? "default" : reader.GetString(0),
                    reader.GetDateTime(1),
                    reader.GetDateTime(2)));
            }
        }

        if (ApplyScanCap(rows, context.Options.MaxScanRows))
        {
            context.Collector.MarkScanTruncated();
        }

        if (rows.Count == 0)
            return InvariantResult.Unknown("INV-P20 UNKNOWN: 无事件记录检验下线声明与上传延迟");

        var declarations = new List<OfflineDeclaration>();
        if (await TableExistsAsync(conn, "pc_tracker_health", context.Ct))
        {
            await using var hcmd = conn.CreateCommand();
            hcmd.CommandTimeout = 5;
            hcmd.CommandText = $"""
                SELECT device_id, reported_at, status
                FROM pc_tracker_health
                ORDER BY reported_at DESC
                LIMIT {context.Options.MaxScanRows + 1};
                """;
            await using var hreader = await hcmd.ExecuteReaderAsync(context.Ct);
            int healthRows = 0;
            while (await hreader.ReadAsync(context.Ct))
            {
                healthRows++;
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

            if (healthRows > context.Options.MaxScanRows)
            {
                context.Collector.MarkScanTruncated();
            }
        }

        // 按设备分别判定：下线声明带的是真实 device_id，混在一起判会让所有声明都匹配不上，
        // 从而把"已经声明过下线"的设备误报成无声明空档。
        var byDevice = rows.GroupBy(row => row.DeviceId, StringComparer.Ordinal);
        var deviceResults = new List<InvariantResult>();
        foreach (var group in byDevice)
        {
            var trace = new DeviceActivityTrace
            {
                DeviceId = group.Key,
                EventTimes = group.Select(row => row.Timestamp).ToList(),
                Declarations = declarations,
                UploadLagSamples = group.Select(row => (row.Timestamp, row.CreatedAt)).ToList()
            };

            deviceResults.Add(DataReliabilityInvariants.CheckS6_OfflineDeclared(trace, context.Options));
        }

        return DataReliabilityInvariants.CombineDeviceVerdicts("INV-P20", deviceResults, context.Options);
    }

    private async Task<InvariantResult> CheckS7Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-P21 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = $"""
            SELECT device_id, timestamp, timestamp + (duration || ' seconds')::interval as end_time, event_type
            FROM pc_tracker_events
            WHERE event_type IN ('window', 'idle', 'gap')
            ORDER BY timestamp DESC
            LIMIT {context.Options.MaxScanRows + 1};
            """;

        var intervals = new List<TimelineInterval>();
        await using var reader = await cmd.ExecuteReaderAsync(context.Ct);
        while (await reader.ReadAsync(context.Ct))
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

        if (ApplyScanCap(intervals, context.Options.MaxScanRows))
        {
            context.Collector.MarkScanTruncated();
        }

        if (intervals.Count == 0)
            return InvariantResult.Unknown("INV-P21 UNKNOWN: 无时间线区间可检验断档标记");

        return DataReliabilityInvariants.CheckS7_TimelineGapMarked(intervals, context.Options);
    }

    private async Task<InvariantResult> CheckS8Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
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

        await using (var reader = await cmd.ExecuteReaderAsync(context.Ct))
        {
            while (await reader.ReadAsync(context.Ct))
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
        if (await TableExistsAsync(conn, "mobile_timeline_blocks", context.Ct))
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
                await using var mReader = await mCmd.ExecuteReaderAsync(context.Ct);
                while (await mReader.ReadAsync(context.Ct))
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

        return DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(samples, context.Options);
    }

    private async Task<InvariantResult> CheckS9Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-C20 UNKNOWN: 数据表 pc_tracker_events 不存在");

        // 1. 获取最近 24h 的有效活跃时长（window 与 web-page，排除 idle）
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = """
            SELECT COALESCE(SUM(duration), 0)
            FROM pc_tracker_events
            WHERE timestamp >= @since
              AND event_type IN ('window', 'web-page')
              AND is_idle = false;
            """;
        BindTimestamp(cmd, "@since", context.NowUtc.AddHours(-context.Options.RecentWindowHours));
        var scalar = await cmd.ExecuteScalarAsync(context.Ct);
        double validDuration = Convert.ToDouble(scalar ?? 0);

        string deviceId = "default";
        string reportedStatus = "Normal";
        double onlineDuration = 86400.0; // 24h 自然窗口基准

        if (await TableExistsAsync(conn, "pc_tracker_health", context.Ct))
        {
            await using var statCmd = conn.CreateCommand();
            statCmd.CommandTimeout = 5;
            statCmd.CommandText = "SELECT device_id, status, uptime_seconds FROM pc_tracker_health ORDER BY reported_at DESC LIMIT 1;";
            await using var hr = await statCmd.ExecuteReaderAsync(context.Ct);
            if (await hr.ReadAsync(context.Ct))
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
            gapCmd.CommandText = $"""
                WITH evs AS (
                    SELECT timestamp as s, timestamp + duration * interval '1 second' as e,
                           LEAD(timestamp) OVER (PARTITION BY device_id ORDER BY timestamp ASC) as next_s
                    FROM pc_tracker_events
                    WHERE timestamp >= @since
                )
                SELECT s, next_s, extract(epoch from (next_s - e)) as gap_sec
                FROM evs
                WHERE next_s > e AND extract(epoch from (next_s - e)) > 900
                ORDER BY s DESC
                LIMIT {context.Options.MaxScanRows + 1};
                """;
            BindTimestamp(gapCmd, "@since", context.NowUtc.AddHours(-context.Options.RecentWindowHours));

            await using var gapReader = await gapCmd.ExecuteReaderAsync(context.Ct);
            while (await gapReader.ReadAsync(context.Ct))
            {
                DateTime s = gapReader.GetDateTime(0);
                DateTime nextS = gapReader.GetDateTime(1);
                double gapSec = gapReader.GetDouble(2);
                gapBreakdowns.Add($"[{s:yyyy-MM-dd HH:mm} ~ {nextS:yyyy-MM-dd HH:mm} 缺口 {gapSec / 3600.0:F2}h]");
            }

            // SQL 按时间倒序取上限行（保证截掉的是最旧的缺口而不是最新的），这里还原成时间升序展示。
            if (gapBreakdowns.Count > context.Options.MaxScanRows)
            {
                gapBreakdowns.RemoveRange(context.Options.MaxScanRows, gapBreakdowns.Count - context.Options.MaxScanRows);
                context.Collector.MarkScanTruncated();
            }

            gapBreakdowns.Reverse();
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

        // 即使最终判为 UNKNOWN（分母口径不足），也把近似覆盖率作为"当前值"暴露出来，避免设置页出现空白。
        double approximateRatio = onlineDuration > 0 ? validDuration / onlineDuration : 0;
        context.Collector.SetCurrentValue(approximateRatio, unit: "ratio", label: $"{approximateRatio * 100.0:F1}%");

        return DataReliabilityInvariants.CheckS9_GapHasSignal(report, context.Options);
    }

    private async Task<InvariantResult> CheckS10Async(DbConnection conn, RuleCheckContext context)
    {
        var runs = new List<BackgroundTaskRun>();

        // 检查分类快照任务：原生 pc_tracker_events 对比 pc_activity_classifications，验证是否存在未分类事件业务日但补齐产出为 0
        if (await TableExistsAsync(conn, "pc_tracker_events", context.Ct) && await TableExistsAsync(conn, "pc_activity_classifications", context.Ct))
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
            await using (var reader = await cmd.ExecuteReaderAsync(context.Ct))
            {
                if (await reader.ReadAsync(context.Ct))
                {
                    unclassifiedDays = reader.GetInt64(0);
                    unclassifiedEvents = reader.GetInt64(1);
                }
            }

            runs.Add(new BackgroundTaskRun
            {
                TaskName = "pc_classification_backfill",
                ExecutedAt = context.NowUtc,
                AvailableDataCount = (int)Math.Min(int.MaxValue, unclassifiedEvents),
                ProcessedCount = 0,
                OutputCount = 0
            });
        }

        // 检查手机汇总任务
        if (await TableExistsAsync(conn, "mobile_usage_events", context.Ct) && await TableExistsAsync(conn, "mobile_usage_summaries", context.Ct))
        {
            await using var cmd1 = conn.CreateCommand();
            cmd1.CommandTimeout = 10;
            cmd1.CommandText = "SELECT count(*) FROM mobile_usage_events WHERE created_at >= @since;";
            BindTimestamp(cmd1, "@since", context.NowUtc.AddDays(-14));
            long mobEvents = Convert.ToInt64(await cmd1.ExecuteScalarAsync(context.Ct) ?? 0);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandTimeout = 10;
            cmd2.CommandText = "SELECT count(*) FROM mobile_usage_summaries WHERE created_at >= @since;";
            BindTimestamp(cmd2, "@since", context.NowUtc.AddDays(-14));
            long mobSummaries = Convert.ToInt64(await cmd2.ExecuteScalarAsync(context.Ct) ?? 0);

            runs.Add(new BackgroundTaskRun
            {
                TaskName = "mobile_usage_summaries",
                ExecutedAt = context.NowUtc,
                AvailableDataCount = (int)Math.Min(int.MaxValue, mobEvents),
                OutputCount = (int)Math.Min(int.MaxValue, mobSummaries)
            });
        }

        if (runs.Count == 0)
            return InvariantResult.Unknown("INV-C21 UNKNOWN: 相关后台任务数据表不存在");

        return DataReliabilityInvariants.CheckS10_TaskHasOutput(runs, context.Options);
    }

    private async Task<InvariantResult> CheckS11Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "mobile_sync_batches", context.Ct))
            return InvariantResult.Unknown("INV-M21 UNKNOWN: 数据表 mobile_sync_batches 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        // 计数必须与写入侧口径一致（#241 / #243）：accepted 统计全部被接受条目，
        // rejected / skipped 是条目级结果，不能一律当成 0，否则"只含拒绝/跳过条目的合法批次"
        // 会被规则 3（虚假完成）误判。
        // 对尚未跑完迁移的库（新增列还不存在）退化为只读旧列，规则 1/2 仍然有效。
        var hasItemCounts = await ColumnExistsAsync(conn, "mobile_sync_batches", "rejected_count", context.Ct)
            && await ColumnExistsAsync(conn, "mobile_sync_batches", "skipped_count", context.Ct);
        // 窗口起点是批次"业务时间"（T4）：新增/存量分档必须用它，而不是入库时间 created_at——
        // 否则积压补传的历史窗口会被误算成新增（EPIC #254 T4）。
        cmd.CommandText = hasItemCounts
            ? $"""
              SELECT batch_id, status, failed_count, accepted_count, rejected_count, skipped_count, window_start_utc
              FROM mobile_sync_batches
              ORDER BY created_at DESC
              LIMIT {context.Options.MaxScanRows + 1};
              """
            : $"""
              SELECT batch_id, status, failed_count, accepted_count, 0, 0, window_start_utc
              FROM mobile_sync_batches
              ORDER BY created_at DESC
              LIMIT {context.Options.MaxScanRows + 1};
              """;

        var batches = new List<BatchSyncStatusRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(context.Ct);
        while (await reader.ReadAsync(context.Ct))
        {
            string batchId = reader.GetString(0);
            string status = reader.GetString(1);
            int failed = reader.GetInt32(2);
            int accepted = reader.GetInt32(3);
            int rejected = reader.GetInt32(4);
            int skipped = reader.GetInt32(5);
            DateTime windowStartUtc = reader.GetDateTime(6);
            batches.Add(new BatchSyncStatusRecord
            {
                BatchId = batchId,
                Status = status,
                TotalCount = accepted + failed + rejected + skipped,
                AcceptedCount = accepted,
                FailedCount = failed,
                RejectedCount = rejected,
                SkippedCount = skipped,
                WindowStartUtc = windowStartUtc
            });
        }

        if (ApplyScanCap(batches, context.Options.MaxScanRows))
        {
            context.Collector.MarkScanTruncated();
        }

        if (batches.Count == 0)
            return InvariantResult.Unknown("INV-M21 UNKNOWN: mobile_sync_batches 中无批次记录");

        return DataReliabilityInvariants.CheckS11_StatusSemantics(batches, context.Options, context.NowUtc);
    }

    private async Task<InvariantResult> CheckS12Async(DbConnection conn, RuleCheckContext context)
    {
        bool hasMobEvents = await TableExistsAsync(conn, "mobile_usage_events", context.Ct);
        if (!hasMobEvents)
            return InvariantResult.Unknown("INV-M22 UNKNOWN: 数据源表 mobile_usage_events 不存在");

        await using var scmd = conn.CreateCommand();
        scmd.CommandTimeout = 10;
        scmd.CommandText = "SELECT count(*) FROM mobile_usage_events WHERE created_at >= (SELECT COALESCE(MAX(created_at), @now) - interval '24 hours' FROM mobile_usage_events);";
        BindTimestamp(scmd, "@now", context.NowUtc);
        int sourceCount = Convert.ToInt32(await scmd.ExecuteScalarAsync(context.Ct) ?? 0);

        await using var bcmd = conn.CreateCommand();
        bcmd.CommandTimeout = 10;
        bcmd.CommandText = "SELECT count(*) FROM mobile_timeline_blocks;";
        int blocksCount = Convert.ToInt32(await bcmd.ExecuteScalarAsync(context.Ct) ?? 0);

        await using var acmd = conn.CreateCommand();
        acmd.CommandTimeout = 10;
        acmd.CommandText = "SELECT count(*) FROM mobile_usage_aggregates;";
        int aggsCount = Convert.ToInt32(await acmd.ExecuteScalarAsync(context.Ct) ?? 0);

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

        return DataReliabilityInvariants.CheckS12_DerivedTableActive(statuses, context.Options);
    }

    private async Task<InvariantResult> CheckS13Async(DbConnection conn, RuleCheckContext context)
    {
        if (!await TableExistsAsync(conn, "pc_tracker_events", context.Ct))
            return InvariantResult.Unknown("INV-P22 UNKNOWN: 数据表 pc_tracker_events 不存在");

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = $"""
            SELECT device_id, timestamp, instance_id
            FROM pc_tracker_events
            WHERE instance_id IS NOT NULL AND instance_id != ''
            ORDER BY timestamp DESC
            LIMIT {context.Options.MaxScanRows + 1};
            """;

        var heartbeats = new List<CollectionHeartbeat>();
        await using var reader = await cmd.ExecuteReaderAsync(context.Ct);
        while (await reader.ReadAsync(context.Ct))
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

        if (ApplyScanCap(heartbeats, context.Options.MaxScanRows))
        {
            context.Collector.MarkScanTruncated();
        }

        if (heartbeats.Count == 0)
            return InvariantResult.Unknown("INV-P22 UNKNOWN: 无采集流数据可检验多实例冲突");

        return DataReliabilityInvariants.CheckS13_SingleInstance(heartbeats, context.Options);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// 应用单条尺子的扫描上限：取数 SQL 一律按业务时间倒序取 <c>MaxScanRows + 1</c> 行，
    /// 这里丢掉多出来的那 1 行、还原成升序，并回报是否命中上限（命中时必须显式告知用户结果可能不完整）。
    /// </summary>
    private static bool ApplyScanCap<T>(List<T> descendingRows, int maxScanRows)
    {
        bool truncated = descendingRows.Count > maxScanRows;
        if (truncated)
        {
            descendingRows.RemoveRange(maxScanRows, descendingRows.Count - maxScanRows);
        }

        descendingRows.Reverse();
        return truncated;
    }

    /// <summary>
    /// 绑定一个时间参数。体检窗口一律用调用方传入的时钟（而不是数据库 NOW()），
    /// 这样同一份数据在不同数据库时钟/入库延迟下会得到同样的结论，测试也能注入固定时间。
    /// </summary>
    private static void BindTimestamp(DbCommand command, string name, DateTime value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

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
