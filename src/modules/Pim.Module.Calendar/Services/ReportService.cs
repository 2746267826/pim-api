using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Core.Quality;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

/// <summary>
/// 报告服务：按各自周期真取数，产出**结论**而不是账本。
///
/// <para>
/// 周期口径（工单 01 §A2.1 + 开工前经维护者确认）：Asia/Shanghai <b>业务日</b>（04:00 起算）——
/// 日报 = 当日、周报 = 当周（周一 04:00 → 下周一 04:00）、月报 = 当月、项目报告 = 项目下任务的时间跨度。
/// 维护者确认以仓库既有 <see cref="BusinessDay"/>/D-1 口径为准，而不是工单原文的字面
/// 「周一 00:00~周日 24:00」；AC-A2 的边界样本相应改为 04:00 边界。
/// </para>
///
/// <para>
/// 取不到数据的模块一律在 <c>metricsJson</c> 与该模块正文里写「本周期无数据」，
/// 绝不填 0、也绝不省略（§A2.4）。
/// </para>
/// </summary>
public sealed class ReportService
{
    /// <summary>取不到数据的模块在 <c>metricsJson</c> 里的显式标记（§A2.4）。</summary>
    public const string NoDataMark = "本周期无数据";

    /// <summary>
    /// 「实际执行时间」认可的计时段落状态：已经**开始过**的段落（工单 03 的
    /// <c>TaskSegmentStatus</c> 子集）。未开始的 <c>planned</c> 属于「已排入日程」不属于「实际执行」，
    /// <c>cancelled</c> 两侧都不计。
    /// </summary>
    private static readonly string[] ExecutedSegmentStatuses = ["active", "paused", "completed"];

    /// <summary>
    /// 收集质量各数据源的报告字段后缀，需与对应 <see cref="ICollectionQualityProvider"/> 给出的
    /// <c>Figures</c> 键一致；报告字段名 = 数据源 key + 后缀（如 mobile + UsageEvents）。
    /// 未登记的已知数据源按「本周期无数据」输出，保证字段不缺席。
    /// </summary>
    private static readonly Dictionary<string, string[]> CollectionQualityFigureKeys = new(StringComparer.Ordinal)
    {
        [CollectionQualitySources.Mobile] = ["UsageEvents", "LocationPoints"],
        [CollectionQualitySources.Pc] = ["Events", "MissingHours"]
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOperationConfirmationService _confirmations;
    private readonly IReadOnlyList<ICollectionQualityProvider> _collectionQualityProviders;

    public ReportService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IOperationConfirmationService confirmations,
        IEnumerable<ICollectionQualityProvider>? collectionQualityProviders = null)
    {
        _db = db;
        _currentUser = currentUser;
        _confirmations = confirmations;
        // 同一数据源只认第一个实现，避免重复注册时同一字段被写两次
        _collectionQualityProviders = collectionQualityProviders?
            .GroupBy(provider => provider.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList() ?? [];
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<ReportArtifactDto> GenerateAsync(
        GenerateReportRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var kind = NormalizeKind(request.Kind);
        var project = await ResolveProjectAsync(kind, request.ProjectId, userId, ct);
        var period = project is null
            ? ResolveCalendarPeriod(kind, request.Date)
            : await ResolveProjectPeriodAsync(project, userId, ct);

        var facts = await CollectFactsAsync(userId, project?.Id, period, ct);
        var metrics = BuildMetrics(facts);
        var content = BuildContent(kind, period, facts);
        var inputs = BuildInputs(kind, request, project, period);

        var entity = new ReportArtifactEntity
        {
            UserId = userId,
            Kind = kind,
            ProjectId = project?.Id,
            RiskLevel = "L0AutomaticArtifact",
            InputsJson = inputs.ToJsonString(JsonOptions),
            MetricsJson = metrics.ToJsonString(JsonOptions),
            ContentMarkdown = content,
            GeneratedAt = DateTimeOffset.UtcNow,
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Set<ReportArtifactEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<IReadOnlyList<ReportArtifactDto>> ListAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var reports = await _db.Set<ReportArtifactEntity>()
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.GeneratedAt)
            .ToListAsync(ct);
        return reports.Select(Map).ToList();
    }

    public async Task<ReportArtifactDto> GetAsync(Guid id, CancellationToken ct = default)
        => Map(await LoadReportAsync(id, ct));

    public async Task<ReportArtifactDto> ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var report = await LoadReportAsync(id, ct);
        report.Status = "Archived";
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(report);
    }

    public async Task<OperationConfirmationDto> RequestSuggestionActionAsync(
        Guid suggestionId,
        CancellationToken ct = default)
    {
        var suggestion = await _db.Set<ReportSuggestionEntity>()
            .Include(s => s.Report)
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.UserId == UserId, ct)
            ?? throw new DomainException(02043, "Report suggestion does not exist.");
        var changedFields = ReadChangedFields(suggestion.ChangedFieldsJson);
        var confirmation = await _confirmations.CreateAsync(
            new CreateOperationConfirmationRequest(
                UserId,
                "report.suggestion." + suggestion.Action,
                suggestion.Summary,
                OperationRiskLevel.L2PimFactChange,
                "report",
                suggestion.PayloadJson,
                JsonSerializer.Serialize(new
                {
                    suggestion.Action,
                    suggestion.Summary,
                    reportId = suggestion.ReportId
                }, JsonOptions),
                DateTimeOffset.UtcNow.AddHours(6),
                suggestion.Id.ToString("N"),
                changedFields,
                ["confirm", "reject"],
                "report-suggestion",
                suggestion.Id,
                false,
                null,
                suggestion.PayloadJson),
            ct);

        suggestion.ConfirmationId = confirmation.Id;
        suggestion.Status = "PendingConfirmation";
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return confirmation;
    }

    // === 周期 ===

    private static ReportPeriod ResolveCalendarPeriod(string kind, DateOnly date)
        => kind switch
        {
            "Daily" => ReportPeriods.Daily(date),
            "Weekly" => ReportPeriods.Weekly(date),
            "Monthly" => ReportPeriods.Monthly(date),
            _ => throw new DomainException(02045, "Unsupported report kind.")
        };

    /// <summary>
    /// 项目报告必须指定项目：没有项目就没有"项目周期"，产出一份看不出问题的通用报告
    /// 属于静默失败，因此直接报错（工单 §三.2）。
    /// </summary>
    private async Task<DomainProjectEntity?> ResolveProjectAsync(string kind, Guid? projectId, Guid userId, CancellationToken ct)
    {
        if (kind != "Project")
        {
            return null;
        }

        if (projectId is null || projectId == Guid.Empty)
        {
            throw new DomainException(02046, "项目报告必须指定 projectId。");
        }

        return await _db.Set<DomainProjectEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId.Value && p.UserId == userId, ct)
            ?? throw new DomainException(02047, "项目不存在。");
    }

    /// <summary>
    /// 项目周期 = 项目下任务的时间跨度：起点取最早的「已排期起点（DtStart）/ 创建时刻」，
    /// 终点取最晚的「完成时刻 / 计划结束 / 截止时刻 / 创建时刻」。项目下没有任何任务时，
    /// 没有任何依据可以界定周期，报错而不是编一个窗口。
    /// </summary>
    private async Task<ReportPeriod> ResolveProjectPeriodAsync(DomainProjectEntity project, Guid userId, CancellationToken ct)
    {
        var tasks = await _db.Set<TaskEntity>()
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.DomainProjectId == project.Id)
            .Select(t => new { t.DtStart, t.PlannedEnd, t.Due, t.CompletedAt, t.CreatedAt })
            .ToListAsync(ct);

        if (tasks.Count == 0)
        {
            throw new DomainException(02048, "项目下没有任何任务，无法推导项目周期。");
        }

        var start = tasks.Min(t => t.DtStart ?? t.CreatedAt);
        var end = tasks.Max(t => Latest(t.CompletedAt, t.PlannedEnd, t.Due, t.DtStart, t.CreatedAt));
        if (end <= start)
        {
            end = start.AddTicks(1);
        }

        var localStart = TimeZoneInfo.ConvertTime(start, BusinessDay.TimeZone);
        var localEnd = TimeZoneInfo.ConvertTime(end, BusinessDay.TimeZone);
        return ReportPeriods.Span(
            start,
            end,
            $"项目报告 · {project.Name} · 项目周期由项目下任务跨度推导"
            + $"（{localStart:yyyy-MM-dd HH:mm} → {localEnd:yyyy-MM-dd HH:mm}，Asia/Shanghai）");

        static DateTimeOffset Latest(params DateTimeOffset?[] candidates)
        {
            DateTimeOffset? latest = null;
            foreach (var candidate in candidates)
            {
                if (candidate is null)
                {
                    continue;
                }

                if (latest is null || candidate.Value > latest.Value)
                {
                    latest = candidate;
                }
            }

            // 至少 CreatedAt 非空，因此这里不会命中；保底分支仅为消除可空告警
            return latest ?? DateTimeOffset.MinValue;
        }
    }

    // === 取数 ===

    private async Task<ReportFacts> CollectFactsAsync(Guid userId, Guid? projectId, ReportPeriod period, CancellationToken ct)
    {
        // 排期窗口与周期**相交**即纳入（半开区间）：起点在周期之前、排期伸进周期的任务也要算进来，
        // 否则跨业务日边界（04:00）的排期会被整条漏掉；时长已由 period.Overlap 裁剪到周期内。
        var periodTasks = await PeriodTasks(userId, projectId)
            .Where(t => t.DtStart != null
                && t.DtStart < period.EndUtc
                && (t.PlannedEnd == null || t.PlannedEnd > period.StartUtc))
            .Select(t => new { t.Id, t.Status, t.EstimatedDuration, t.DtStart, t.PlannedEnd })
            .ToListAsync(ct);

        var estimated = periodTasks.Where(t => t.EstimatedDuration.HasValue).ToList();
        var scheduledWindows = periodTasks
            .Where(t => t.PlannedEnd.HasValue)
            .Select(t => period.Overlap(t.DtStart!.Value, t.PlannedEnd!.Value))
            .ToList();

        var segments = await _db.Set<TaskExecutionSegmentEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && s.DeletedAt == null
                && s.StartsAt < period.EndUtc
                && s.EndsAt > period.StartUtc
                && (projectId == null || _db.Set<TaskEntity>().Any(t => t.Id == s.TaskId && t.DomainProjectId == projectId)))
            .Select(s => new { s.StartsAt, s.EndsAt, s.Status })
            .ToListAsync(ct);

        var executed = segments
            .Where(s => ExecutedSegmentStatuses.Contains(s.Status, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var events = await _db.Set<EventEntity>()
            .AsNoTracking()
            .Where(e => e.Calendar.UserId == userId
                && e.DtStart < period.EndUtc
                && e.DtEnd > period.StartUtc)
            .Select(e => new { e.DtStart, e.DtEnd, e.Status })
            .ToListAsync(ct);

        // 取消的日程不算占用（大小写不敏感，历史数据来源不一）
        var activeEvents = events
            .Where(e => !string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Start: e.DtStart, End: e.DtEnd))
            .ToList();

        var createdTasks = await PeriodTasks(userId, projectId)
            .CountAsync(t => t.CreatedAt >= period.StartUtc && t.CreatedAt < period.EndUtc, ct);
        var completedTasks = await PeriodTasks(userId, projectId)
            .CountAsync(t => t.Status == "COMPLETED"
                && t.CompletedAt != null
                && t.CompletedAt >= period.StartUtc
                && t.CompletedAt < period.EndUtc, ct);
        var dueInPeriod = await PeriodTasks(userId, projectId)
            .CountAsync(t => t.Due != null && t.Due >= period.StartUtc && t.Due < period.EndUtc, ct);
        // 逾期 = 本周期内到期、且**没有按期完成**（含"到期后才完成"，那同样属于未按期完成）。
        // 只排除取消的任务：此前多写的 `Status != "COMPLETED"` 会把"晚完成"整类漏掉。
        var overdueTasks = await PeriodTasks(userId, projectId)
            .CountAsync(t => t.Due != null
                && t.Due >= period.StartUtc
                && t.Due < period.EndUtc
                && t.Status != "CANCELLED"
                && (t.CompletedAt == null || t.CompletedAt > t.Due), ct);

        var collection = new List<CollectionQualitySnapshot>(CollectionQualitySources.All.Count);
        foreach (var sourceKey in CollectionQualitySources.All)
        {
            var provider = _collectionQualityProviders.FirstOrDefault(p => string.Equals(p.Key, sourceKey, StringComparison.Ordinal));
            collection.Add(provider is null
                ? new CollectionQualitySnapshot(sourceKey, SourceLabel(sourceKey), "Unknown", string.Empty, new Dictionary<string, long?>())
                : await provider.GetForPeriodAsync(period.StartUtc, period.EndUtc, ct));
        }

        // 「本周期压根没有事实」与「有事实但某项统计为 0」必须分开（§A2.4）：
        // 没有任何任务 / 日程 / 计时段落 / 采集数据时，日历占用与任务进展也标「本周期无数据」，
        // 而不是给出一个看不出真假的 0。
        // 注意：任务进展看的是"周期内发生的创建/完成/到期"，这些任务不一定有排期（DtStart），
        // 所以它们也要计入"本周期有事实"，否则只记完成、不排期的用户会拿到一份全是无数据的报告。
        var hasPeriodFacts = periodTasks.Count > 0
            || activeEvents.Count > 0
            || segments.Count > 0
            || createdTasks > 0
            || completedTasks > 0
            || dueInPeriod > 0
            || collection.Any(snapshot => snapshot.HasAnyFigure);

        return new ReportFacts(
            TaskCount: periodTasks.Count,
            CompletedPeriodTasks: periodTasks.Count(t => t.Status == "COMPLETED"),
            EstimatedMinutes: estimated.Count == 0
                ? null
                : ToMinutes(estimated.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.EstimatedDuration!.Value)),
            ScheduledMinutes: scheduledWindows.Count == 0 ? null : ToMinutes(scheduledWindows.Aggregate(TimeSpan.Zero, (sum, span) => sum + span)),
            ActualMinutes: executed.Count == 0
                ? null
                : ToMinutes(executed.Aggregate(TimeSpan.Zero, (sum, s) => sum + period.Overlap(s.StartsAt, s.EndsAt))),
            CalendarMinutes: activeEvents.Count == 0
                ? null
                : ToMinutes(activeEvents.Aggregate(TimeSpan.Zero, (sum, e) => sum + period.Overlap(e.Start, e.End))),
            CalendarDays: activeEvents.Count == 0 ? null : CountDaysWithEvents(activeEvents, period),
            CreatedTasks: hasPeriodFacts ? createdTasks : null,
            CompletedTasks: hasPeriodFacts ? completedTasks : null,
            OverdueTasks: hasPeriodFacts ? overdueTasks : null,
            HasPeriodFacts: hasPeriodFacts,
            Collection: collection);
    }

    private IQueryable<TaskEntity> PeriodTasks(Guid userId, Guid? projectId)
    {
        var query = _db.Set<TaskEntity>().AsNoTracking().Where(t => t.UserId == userId);
        return projectId is null ? query : query.Where(t => t.DomainProjectId == projectId);
    }

    private static int CountDaysWithEvents(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> events,
        ReportPeriod period)
    {
        var days = 0;
        for (var date = period.FirstBusinessDate; date <= period.LastBusinessDate; date = date.AddDays(1))
        {
            var dayStart = BusinessDay.GetStartUtc(date);
            var dayEnd = BusinessDay.GetStartUtc(date.AddDays(1));
            if (events.Any(e => e.Start < dayEnd && e.End > dayStart))
            {
                days++;
            }
        }

        return days;
    }

    // === 结论 ===

    /// <summary>
    /// <c>metricsJson</c> 的形状受既有前端约束（`reports-page.tsx` 对它做 <c>JSON.parse</c> 并按
    /// <c>Record&lt;string, number&gt;</c> 使用）：必须保持**扁平**，且 <c>tasks</c>/<c>completedTasks</c>
    /// 两个既有键必须是数字（否则漏斗/仪表盘会拿到 NaN）。因此这里只放
    /// 「数字」或「本周期无数据」字面量两种取值，不放嵌套对象。
    /// </summary>
    private static JsonObject BuildMetrics(ReportFacts facts)
    {
        var metrics = new JsonObject
        {
            ["tasks"] = facts.TaskCount,
            ["completedTasks"] = facts.CompletedPeriodTasks,
            ["plannedEstimatedMinutes"] = Metric(facts.EstimatedMinutes),
            ["plannedScheduledMinutes"] = Metric(facts.ScheduledMinutes),
            ["actualExecutedMinutes"] = Metric(facts.ActualMinutes),
            ["calendarOccupiedMinutes"] = Metric(facts.CalendarMinutes),
            ["calendarActiveDays"] = Metric(facts.CalendarDays),
            ["tasksCreated"] = Metric(facts.CreatedTasks),
            ["tasksCompleted"] = Metric(facts.CompletedTasks),
            ["tasksOverdue"] = Metric(facts.OverdueTasks)
        };

        foreach (var sourceKey in CollectionQualitySources.All)
        {
            var snapshot = facts.Collection.FirstOrDefault(s => string.Equals(s.Key, sourceKey, StringComparison.Ordinal));
            var figureKeys = CollectionQualityFigureKeys.TryGetValue(sourceKey, out var keys) ? keys : [];
            foreach (var figureKey in figureKeys)
            {
                long? value = null;
                if (snapshot is not null && snapshot.Figures.TryGetValue(figureKey, out var figure))
                {
                    value = figure;
                }

                metrics[sourceKey + figureKey] = Metric(value);
            }
        }

        return metrics;

        static JsonNode? Metric(long? value) => value is null ? NoDataMark : JsonValue.Create(value.Value);
    }

    private static string BuildContent(string kind, ReportPeriod period, ReportFacts facts)
    {
        var localStart = period.LocalStart;
        var localEnd = period.LocalEnd;
        var lines = new List<string>
        {
            $"# {KindLabel(kind)}",
            string.Empty,
            period.Description,
            $"周期：{localStart:yyyy-MM-dd HH:mm} → {localEnd:yyyy-MM-dd HH:mm}（Asia/Shanghai，业务日以 {BusinessDay.StartHour:00}:00 为界）",
            string.Empty,
            "## 结论",
            $"- 计划：任务预估 {Hours(facts.EstimatedMinutes)}，已排入日程 {Hours(facts.ScheduledMinutes)}",
            $"- 实际：执行 {Hours(facts.ActualMinutes)}",
            $"- 日历占用：{Hours(facts.CalendarMinutes)}，涉及 {Days(facts.CalendarDays)}",
            $"- 收集质量：{string.Join("；", facts.Collection.Select(CollectionLine))}",
            $"- 任务进展：新增 {Count(facts.CreatedTasks)} / 完成 {Count(facts.CompletedTasks)} / 逾期 {Count(facts.OverdueTasks)}",
            string.Empty,
            "## 计划 vs 实际",
            $"- 本周期已排期任务：{Count(facts.HasPeriodFacts ? facts.TaskCount : (long?)null)}（其中已完成 {Count(facts.HasPeriodFacts ? facts.CompletedPeriodTasks : (long?)null)}）",
            $"- 任务预估时长合计：{Hours(facts.EstimatedMinutes)}",
            $"- 已排入日程的时长：{Hours(facts.ScheduledMinutes)}",
            $"- 实际执行时间（task_execution_segments，仅计已开始的段落）：{Hours(facts.ActualMinutes)}",
            string.Empty,
            "## 日历占用",
            $"- 日程总时长：{Hours(facts.CalendarMinutes)}",
            $"- 有日程的天数：{Days(facts.CalendarDays)}",
            string.Empty,
            "## 收集质量"
        };

        if (!facts.HasPeriodFacts)
        {
            lines.Insert(5, "> 本周期无数据：该周期内没有任何任务、日程、计时段落或采集数据。");
        }

        foreach (var snapshot in facts.Collection)
        {
            lines.Add($"- {CollectionLine(snapshot)}");
        }

        lines.AddRange(
        [
            string.Empty,
            "## 任务进展",
            $"- 新增：{Count(facts.CreatedTasks)}",
            $"- 完成：{Count(facts.CompletedTasks)}",
            $"- 逾期（本周期内到期且未按期完成）：{Count(facts.OverdueTasks)}",
            string.Empty,
            "> 无数据的模块标注为「本周期无数据」，不以 0 代替；任务进展的「新增/完成」为周期内发生的次数，"
                + "「逾期」为周期内到期且未按期完成（含到期后才完成）的任务数；报告建议单独保存，需确认后才会改动事实。"
        ]);

        return string.Join('\n', lines);
    }

    private static string CollectionLine(CollectionQualitySnapshot snapshot)
    {
        if (!snapshot.HasAnyFigure)
        {
            return $"{snapshot.Label}：{NoDataMark}";
        }

        var figures = string.Join("；", snapshot.Figures
            .Where(pair => pair.Value.HasValue)
            .Select(pair => $"{FigureLabel(pair.Key)} {pair.Value!.Value.ToString(CultureInfo.InvariantCulture)}"));
        var conclusion = string.IsNullOrWhiteSpace(snapshot.Conclusion) ? string.Empty : $" — {snapshot.Conclusion}";
        return $"{snapshot.Label}：{snapshot.Status}{conclusion}（{figures}）";
    }

    private static JsonObject BuildInputs(
        string kind,
        GenerateReportRequest request,
        DomainProjectEntity? project,
        ReportPeriod period)
        => new()
        {
            ["kind"] = kind,
            ["date"] = BusinessDay.FormatDate(request.Date),
            ["projectId"] = project is null ? null : JsonValue.Create(project.Id),
            ["periodStartUtc"] = period.StartUtc.ToString("O", CultureInfo.InvariantCulture),
            ["periodEndUtc"] = period.EndUtc.ToString("O", CultureInfo.InvariantCulture),
            ["businessDayStartHour"] = BusinessDay.StartHour,
            ["timeZone"] = BusinessDay.TimeZone.Id,
            ["generatedFrom"] = new JsonArray(
                JsonValue.Create("planned-vs-actual"),
                JsonValue.Create("calendar-occupancy"),
                JsonValue.Create("collection-quality"),
                JsonValue.Create("task-progress"))
        };

    private static string KindLabel(string kind)
        => kind switch
        {
            "Daily" => "日报",
            "Weekly" => "周报",
            "Monthly" => "月报",
            "Project" => "项目报告",
            _ => kind
        };

    private static string SourceLabel(string sourceKey)
        => sourceKey switch
        {
            CollectionQualitySources.Mobile => "手机（Android）",
            CollectionQualitySources.Pc => "PC（Windows）",
            _ => sourceKey
        };

    private static string FigureLabel(string figureKey)
        => figureKey switch
        {
            "UsageEvents" => "使用事件",
            "LocationPoints" => "定位点",
            "Events" => "采集事件",
            "MissingHours" => "缺数小时",
            _ => figureKey
        };

    /// <summary>时长结论：取不到数据时显式标注，而不是 0 小时。</summary>
    private static string Hours(long? minutes)
        => minutes is null
            ? NoDataMark
            : $"{(minutes.Value / 60.0).ToString("0.#", CultureInfo.InvariantCulture)} 小时";

    /// <summary>计数结论：取不到数据时显式标注，而不是 0 项。</summary>
    private static string Count(long? value)
        => value is null ? NoDataMark : $"{value.Value.ToString(CultureInfo.InvariantCulture)} 项";

    /// <summary>天数结论：取不到数据时显式标注，而不是 0 天。</summary>
    private static string Days(long? value)
        => value is null ? NoDataMark : $"{value.Value.ToString(CultureInfo.InvariantCulture)} 天";

    private static long ToMinutes(TimeSpan span)
        => (long)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);

    private async Task<ReportArtifactEntity> LoadReportAsync(Guid id, CancellationToken ct)
        => await _db.Set<ReportArtifactEntity>()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct)
            ?? throw new DomainException(02044, "Report does not exist.");

    private static ReportArtifactDto Map(ReportArtifactEntity entity)
        => new(
            entity.Id,
            entity.Kind,
            entity.ProjectId,
            entity.RiskLevel,
            entity.ContentMarkdown,
            entity.MetricsJson,
            entity.GeneratedAt,
            entity.Status);

    private static IReadOnlyList<string> ReadChangedFields(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeKind(string kind)
        => kind switch
        {
            "Daily" or "Weekly" or "Monthly" or "Project" => kind,
            _ => throw new DomainException(02045, "Unsupported report kind.")
        };

    /// <summary>
    /// 某个周期的取数结果（模块结论的唯一来源，正文与 metricsJson 必须同源）。
    /// 所有可空字段的 <c>null</c> 都只有一个含义：**本周期取不到该口径的数据**（§A2.4），
    /// 与"有事实但统计值为 0"区分开。
    /// </summary>
    private sealed record ReportFacts(
        long TaskCount,
        long CompletedPeriodTasks,
        long? EstimatedMinutes,
        long? ScheduledMinutes,
        long? ActualMinutes,
        long? CalendarMinutes,
        long? CalendarDays,
        long? CreatedTasks,
        long? CompletedTasks,
        long? OverdueTasks,
        bool HasPeriodFacts,
        IReadOnlyList<CollectionQualitySnapshot> Collection);
}
