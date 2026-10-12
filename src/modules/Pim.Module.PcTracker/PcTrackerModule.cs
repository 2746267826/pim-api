using Pim.Infrastructure.Operations;
using System.Reflection;
using System.Globalization;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Common;
using Pim.Core.Caching;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;

namespace Pim.Module.PcTracker;

public class PcTrackerModule : IModule
{
    public string Name => "pctracker";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.AddScoped<PcTrackerService>();
        services.AddScoped<PcTrackerQualityService>();
        services.AddScoped<PcBrowserSiteService>();
        services.AddScoped<ActivitySuggestionService>();
        services.AddScoped<ActivityClassificationSnapshotService>();
        services.AddScoped<ActivityClassificationRecomputeService>();
        services.AddScoped<ActivityClassificationSettingsService>();
        services.AddScoped<ActivityTimelineSmoothingService>();
        services.AddScoped<ActivityClassificationRuleService>();
        services.AddScoped<ClassificationRuleDraftService>();
        services.AddScoped<ActivityLabelingService>();
        services.AddScoped<PcTrackerSchemaInitializer>();
        services.AddScoped<AppSignatureService>();
        services.AddScoped<AppKnowledgeContextService>();
        services.AddScoped<AppKnowledgeSuggestionService>();
        services.AddScoped<PcCategoryService>();
        services.AddScoped<PcProductivityService>();
        services.AddScoped<PcActivityRecordKeyService>();
        services.AddScoped<PcActivityAnalysisService>();
        services.AddScoped<PcActivityAggregationService>();
        services.AddScoped<PcClassificationBackfillService>();
        services.AddScoped<PcClassificationSnapshotJob>();
        services.AddScoped<PcTrackerUntaggedInspector>();
        services.AddSingleton<IAppLookupProvider, DefaultAppLookupProvider>();
        services.AddScoped<IDataQualityInspector, PcTrackerUntaggedInspector>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // readGroup 曾漏挂鉴权（写组一直有），导致 /api/v1/pc 下的查询端点匿名可读
        // （公网实测 /pc/summary 200 返回真实数据）。与同文件其他组对齐补上；
        // 三个客户端均统一注入 Bearer（web apiFetch 默认 auth=true、windows 全局默认头、
        // android AuthInterceptor），不存在依赖匿名读取的调用方。
        var readGroup = endpoints.MapGroup("/api/v1/pc")
            .RequireAuthorization();
        var writeGroup = endpoints.MapGroup("/api/v1/pc")
            .RequireAuthorization();

        writeGroup.MapPost("/keystats/upload", async (
            [FromBody] KeystatsUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            await svc.UpsertKeystatsAsync(req, ct);
            return Results.Ok(ApiResponse<string>.Ok("已接收"));
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/keystats/samples", async (
            [FromBody] KeystatsSampleUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            await svc.UpsertKeystatsSampleAsync(req, ct);
            return Results.Ok(ApiResponse<string>.Ok("已接收"));
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/aw/upload", async (
            [FromBody] AwEventsUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var count = await svc.UploadAwEventsAsync(req, ct);
            return Results.Ok(ApiResponse<int>.Ok(count));
        })
        .Produces<ApiResponse<int>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/aw/upload-complete", async (
            [FromBody] CompleteAwUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var count = await svc.UploadCompleteAwEventsAsync(req, ct);
            return Results.Ok(ApiResponse<int>.Ok(count));
        })
        .Produces<ApiResponse<int>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/tracker/upload", async (
            [FromBody] TrackerEventsUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            try
            {
                var count = await svc.UploadTrackerEventsAsync(req, ct);
                return Results.Ok(ApiResponse<int>.Ok(count));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<int>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/tracker/health", async (
            [FromBody] TrackerHealthRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            try
            {
                await svc.RecordTrackerHealthAsync(req, ct);
                return Results.Ok(ApiResponse<string>.Ok("ok"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/tracker/health/latest", async (
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var health = await svc.GetLatestTrackerHealthAsync(ct);
            if (health is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "not found"));
            return Results.Ok(ApiResponse<TrackerHealthEntity>.Ok(health));
        })
        // readGroup 当前未挂 RequireAuthorization(现状),无 401。
        .Produces<ApiResponse<TrackerHealthEntity>>(200)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/tracker/health", async (
            [FromQuery] string deviceId,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.BadRequest(ApiResponse<string>.Error(400, "deviceId required"));
            var health = await svc.GetTrackerHealthAsync(deviceId, ct);
            if (health is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "not found"));
            return Results.Ok(ApiResponse<TrackerHealthEntity>.Ok(health));
        })
        .Produces<ApiResponse<TrackerHealthEntity>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        // —— 站点级数据（Time Tracker fork 通道） ——
        writeGroup.MapPost("/browser-tt/upload", async (
            [FromBody] SiteEventsUploadRequest req,
            [FromServices] PcBrowserSiteService svc,
            CancellationToken ct) =>
        {
            try
            {
                var count = await svc.UploadAsync(req, ct);
                return Results.Ok(ApiResponse<int>.Ok(count));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<int>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/browser-tt/import", async (
            [FromBody] SiteImportRequest req,
            [FromServices] PcBrowserSiteService svc,
            CancellationToken ct) =>
        {
            try
            {
                var result = await svc.ImportAsync(req, ct);
                return Results.Ok(ApiResponse<SiteImportResultDto>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<SiteImportResultDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/browser-tt/daily", async (
            [FromQuery] string? date,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromServices] PcBrowserSiteService svc,
            CancellationToken ct) =>
        {
            try
            {
                var rows = await svc.GetDailyAsync(new SiteDailyQuery { Date = date, From = from, To = to }, ct);
                return Results.Ok(ApiResponse<List<SiteDailyRowDto>>.Ok(rows));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<List<SiteDailyRowDto>>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/browser-tt/timeline", async (
            [FromQuery] string date,
            [FromServices] PcBrowserSiteService svc,
            CancellationToken ct) =>
        {
            try
            {
                var rows = await svc.GetTimelineAsync(date, ct);
                return Results.Ok(ApiResponse<List<SiteTimelineRowDto>>.Ok(rows));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<List<SiteTimelineRowDto>>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/browser-tt/summary", async (
            [FromQuery] string? date,
            [FromQuery] string? from,
            [FromQuery] string? to,
            [FromServices] PcBrowserSiteService svc,
            CancellationToken ct) =>
        {
            try
            {
                var (f, t) = date is not null ? (date, date) : (from, to);
                var summary = await svc.GetSummaryAsync(f, t, ct);
                return Results.Ok(ApiResponse<SiteSummaryDto>.Ok(summary));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<SiteSummaryDto>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/summary", async (
            [FromQuery] string? date,
            [FromServices] PcTrackerService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("date", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                force,
                () => svc.GetSummaryAsync(d, ct),
                ct);
            return Results.Ok(ApiResponse<PcSummaryResponse>.Ok(result));
        })
        .Produces<ApiResponse<PcSummaryResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/aw/timeline", async (
            [FromQuery] string? date,
            [FromServices] PcTrackerService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("date", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                force,
                () => svc.GetTimelineAsync(d, ct),
                ct);
            return Results.Ok(ApiResponse<List<TimelineItem>>.Ok(result));
        })
        .Produces<ApiResponse<List<TimelineItem>>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/keystats/range", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcTrackerService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var s = start is not null ? DateTime.Parse(start, CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-7);
            var e = end is not null ? DateTime.Parse(end, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("start", s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), new("end", e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                force,
                () => svc.GetKeystatsRangeAsync(s, e, ct),
                ct);
            return Results.Ok(ApiResponse<List<KeystatsSummary>>.Ok(result));
        })
        .Produces<ApiResponse<List<KeystatsSummary>>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/detail", async (
            [FromQuery] string? dateFrom,
            [FromQuery] string? dateTo,
            [FromQuery] string? dimension,
            [FromQuery] string? deviceId,
            [FromQuery] string? appName,
            [FromQuery] string? categoryName,
            [FromQuery] string? keyName,
            [FromQuery] string? eventType,
            [FromQuery] string? sortBy,
            [FromQuery] string? sortDir,
            [FromQuery] string? domain,
            [FromQuery] string? title,
            [FromQuery] string? url,
            [FromQuery] string? view,
            [FromServices] PcTrackerService svc,
            CancellationToken ct,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20) =>
        {
            var q = new DetailQueryParams(dateFrom, dateTo, dimension, deviceId,
                appName, categoryName, keyName, eventType, sortBy, sortDir, page, pageSize,
                domain, title, url, view);
            var result = await svc.QueryCompleteDetailAsync(q, ct);
            return Results.Ok(ApiResponse<TypedDetailQueryResponse>.Ok(result));
        })
        .Produces<ApiResponse<TypedDetailQueryResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/quality", async (
            [FromQuery] string? date,
            [FromQuery] string? dateFrom,
            [FromQuery] string? dateTo,
            [FromServices] PcTrackerQualityService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var parsedDate = TryParseDate(date);
            var parsedDateFrom = TryParseDate(dateFrom);
            var parsedDateTo = TryParseDate(dateTo);
            var result = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request, overrides:
                [
                    new("date", parsedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
                    new("dateFrom", parsedDateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
                    new("dateTo", parsedDateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
                ]),
                force,
                () => svc.GetQualityAsync(
                    parsedDate,
                    parsedDateFrom,
                    parsedDateTo,
                    ct),
                ct);
            return Results.Ok(ApiResponse<PcQualityResponse>.Ok(result));
        })
        .Produces<ApiResponse<PcQualityResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/categories", async (
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetAllCategoriesAsync(ct);
            return Results.Ok(ApiResponse<List<AppCategoryRule>>.Ok(list));
        })
        .Produces<ApiResponse<List<AppCategoryRule>>>(200)
        .Produces<ApiResponse<string>>(500);

        // 扁平「应用模式→分类规则」保存原注册在 POST /categories,与 catWrite 的树形保存
        // （POST /categories/，web 分类页在用）同方法同路径，运行时路由匹配歧义 500。
        // 按裁决树形留任 /categories，扁平保存迁移到独立资源路径；MCP create_category 已同步重指向。
        writeGroup.MapPost("/app-category-rules", async (
            [FromBody] SaveCategoryRequest req,
            [FromServices] PcTrackerService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var result = await svc.SaveCategoryAsync(req, ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return Results.Ok(ApiResponse<AppCategoryRule>.Ok(result));
        })
        .Produces<ApiResponse<AppCategoryRule>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapDelete("/categories/{id}", async (
            Guid id,
            [FromServices] PcTrackerService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteCategoryAsync(id, ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return ok
                ? Results.Ok(ApiResponse<string>.Ok("已删除"))
                : Results.NotFound(ApiResponse<string>.Error(404, "不存在或为内置项"));
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/classification/rules", async (
            [FromServices] ActivityClassificationRuleService svc,
            CancellationToken ct) =>
        {
            var rules = await svc.ListAsync(ct);
            return Results.Ok(ApiResponse<List<ActivityClassificationRuleDto>>.Ok(rules));
        })
        .Produces<ApiResponse<List<ActivityClassificationRuleDto>>>(200)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/classification/suggestions/v2", async (
            [FromServices] ActivitySuggestionService suggestionService,
            CancellationToken ct) =>
        {
            var suggestions = await suggestionService.GetSuggestionsV2Async(ct);
            return Results.Ok(ApiResponse<List<ActivityClassificationSuggestionV2Dto>>.Ok(suggestions));
        })
        .Produces<ApiResponse<List<ActivityClassificationSuggestionV2Dto>>>(200)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/classification/suggestions", async (
            [FromQuery] string? date,
            [FromServices] PcTrackerService pcTrackerService,
            [FromServices] ActivitySuggestionService suggestionService,
            [FromServices] ActivityClassificationSettingsService settingsService,
            CancellationToken ct) =>
        {
            try
            {
                var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
                var q = new DetailQueryParams(
                    d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    1,
                    500);
                // REQ-1（#362）：建议生成的扫描输入同样必须覆盖整个业务日 ——
                // 走内部分析路径，不再被 /pc/detail 的分页上限（200 且只取第 1 页）截断。
                // REQ-5（#366）：date 参数语义 = 「扫描该业务日的记录并刷新建议」，
                // 列表本身仍返回全量待处理建议（结果每条带 generatedForDate 供归日）。
                var records = (await pcTrackerService.QueryAllDetailRecordsAsync(q, ct))
                    .Where(NeedsClassificationSuggestion)
                    .ToList();
                var settings = await settingsService.GetSettingsAsync(ct);
                var suggestions = await suggestionService.BuildSuggestionsAsync(
                    records,
                    settings.RecommendedMinimumClassificationDurationMinutes,
                    ct);
                return Results.Ok(ApiResponse<List<ActivityClassificationSuggestionDto>>.Ok(suggestions));
            }
            // REQ-2（#371）：内部取数路径的跨度上限与非法参数必须以 400 返回，
            // 不能变成 500 —— 与 /pc/activity-analysis 同一套映射。
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<List<ActivityClassificationSuggestionDto>>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/classification/project-tags/recent", async (
            [FromServices] ActivitySuggestionService suggestionService,
            CancellationToken ct) =>
        {
            var tags = await suggestionService.GetRecentProjectTagsAsync(ct);
            return Results.Ok(ApiResponse<List<string>>.Ok(tags));
        })
        .Produces<ApiResponse<List<string>>>(200)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/classification/settings", async (
            [FromServices] ActivityClassificationSettingsService settingsService,
            CancellationToken ct) =>
        {
            var settings = await settingsService.GetSettingsAsync(ct);
            return Results.Ok(ApiResponse<ActivityClassificationSettingsDto>.Ok(settings));
        })
        .Produces<ApiResponse<ActivityClassificationSettingsDto>>(200)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/classification/queue", async (
            [FromQuery] int limit,
            [FromQuery] string? mode,
            [FromServices] ActivityLabelingService svc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await svc.BuildQueueAsync(limit, currentUser.UserId, mode ?? "queue", ct);
            return Results.Ok(ApiResponse<ActivityLabelingQueueResponse>.Ok(result));
        })
        .Produces<ApiResponse<ActivityLabelingQueueResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/label", async (
            [FromBody] ActivityLabelingRequest req,
            [FromServices] ActivityLabelingService svc,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            try
            {
                var result = await svc.LabelAsync(req, currentUser.UserId, ct);
                return Results.Ok(ApiResponse<ActivityLabelingResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<ActivityLabelingResponse>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/activity-analysis", async (
            [FromQuery] string? date,
            [FromQuery] int? blockMinutes,
            [FromServices] PcActivityAnalysisService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            try
            {
                var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("date", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                    force,
                    () => svc.GetDailyAnalysisAsync(d, blockMinutes ?? 60, ct),
                    ct);
                return Results.Ok(ApiResponse<PcActivityAnalysisResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<PcActivityAnalysisResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/rules", async (
            [FromBody] SaveActivityClassificationRuleRequest req,
            [FromServices] ActivityClassificationRuleService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var rule = await svc.SaveAsync(req, ct);
                cache.EvictByPrefix("/api/v1/pc/");
                return Results.Ok(ApiResponse<ActivityClassificationRuleDto>.Ok(rule));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<ActivityClassificationRuleDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/rules/preview", async (
            [FromBody] ActivityClassificationPreviewRequest req,
            [FromServices] ActivityClassificationRecomputeService svc,
            CancellationToken ct) =>
        {
            try
            {
                var preview = await svc.PreviewRuleAsync(req.Rule, req.Range, ct);
                return Results.Ok(ApiResponse<ActivityClassificationPreviewDto>.Ok(preview));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<ActivityClassificationPreviewDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/rules/apply", async (
            [FromBody] ApplyActivityClassificationRuleRequest req,
            [FromServices] ActivityClassificationRecomputeService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var preview = await svc.ApplyRuleAsync(req.Rule, req.Range, ct);
                cache.EvictByPrefix("/api/v1/pc/");
                return Results.Ok(ApiResponse<ActivityClassificationPreviewDto>.Ok(preview));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<ActivityClassificationPreviewDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPut("/classification/settings", async (
            [FromBody] SaveActivityClassificationSettingsRequest req,
            [FromServices] ActivityClassificationSettingsService settingsService,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var settings = await settingsService.SaveSettingsAsync(
                req.RecommendedMinimumClassificationDurationMinutes,
                ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return Results.Ok(ApiResponse<ActivityClassificationSettingsDto>.Ok(settings));
        })
        .Produces<ApiResponse<ActivityClassificationSettingsDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/suggestions/{id:guid}/preview", async (
            Guid id,
            [FromBody] SuggestionClassificationPreviewRequest req,
            [FromServices] ActivityClassificationRecomputeService recompute,
            [FromServices] ClassificationRuleDraftService drafts,
            CancellationToken ct) =>
        {
            try
            {
                var preview = await recompute.PreviewSuggestionAsync(id, req, drafts, ct);
                return Results.Ok(ApiResponse<ActivityClassificationSuggestionPreviewDto>.Ok(preview));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "未找到分类建议。"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        // 亦被 app-knowledge/suggestions/{id}/preview 复用
        .Produces<ApiResponse<ActivityClassificationSuggestionPreviewDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/suggestions/{id:guid}/apply", async (
            Guid id,
            [FromBody] SuggestionClassificationApplyRequest req,
            [FromServices] ActivityClassificationRecomputeService recompute,
            [FromServices] ClassificationRuleDraftService drafts,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var result = await recompute.ApplySuggestionAsync(id, req, drafts, ct);
                cache.EvictByPrefix("/api/v1/pc/");
                return Results.Ok(ApiResponse<ActivityClassificationSuggestionApplyDto>.Ok(result));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "未找到分类建议。"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<ActivityClassificationSuggestionApplyDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/suggestions/{id:guid}/accept", async (
            Guid id,
            [FromBody] AcceptActivityClassificationSuggestionRequest req,
            [FromServices] ActivitySuggestionService suggestionService,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var rule = await suggestionService.AcceptSuggestionAsync(id, req, ct);
                cache.EvictByPrefix("/api/v1/pc/");
                return Results.Ok(ApiResponse<ActivityClassificationRuleDto>.Ok(rule));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "不存在"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<ActivityClassificationRuleDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/suggestions/batch-accept", async (
            [FromBody] BatchAcceptSuggestionsRequest req,
            [FromServices] ActivitySuggestionService suggestionService,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var result = await suggestionService.BatchAcceptAsync(req, ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return Results.Ok(ApiResponse<BatchAcceptResultDto>.Ok(result));
        })
        .Produces<ApiResponse<BatchAcceptResultDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/suggestions/{id:guid}/reject", async (
            Guid id,
            [FromServices] ActivitySuggestionService suggestionService,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                await suggestionService.RejectSuggestionAsync(id, ct);
                cache.EvictByPrefix("/api/v1/pc/");
                return Results.Ok(ApiResponse<string>.Ok("已拒绝"));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "不存在"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPost("/classification/recompute", async (
            [FromBody] ActivityClassificationRecomputeRequest req,
            [FromServices] ActivityClassificationRecomputeService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var result = await svc.RecomputeAsync(req.Range, ct);
                cache.EvictByPrefix("/api/v1/pc/");
                return Results.Ok(ApiResponse<ActivityClassificationRecomputeDto>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<ActivityClassificationRecomputeDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/heatmap/grid", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcTrackerService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] string dimension = "day",
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var s = start is not null ? DateTime.Parse(start, CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-30);
            var e = end is not null ? DateTime.Parse(end, CultureInfo.InvariantCulture) : DateTime.Today;
            try
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("start", s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), new("end", e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                    force,
                    () => svc.GetHeatmapGridAsync(s, e, dimension, ct),
                    ct);
                return Results.Ok(ApiResponse<HeatmapGridResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                // REQ-6（#367）：hour + 跨日 = 400 + 明确文案，不再静默返回单日结果。
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<HeatmapGridResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // App Knowledge Base endpoints
        var appKnowledgeRead = endpoints.MapGroup("/api/v1/pc/app-knowledge").RequireAuthorization();
        var appKnowledgeWrite = endpoints.MapGroup("/api/v1/pc/app-knowledge").RequireAuthorization();

        appKnowledgeRead.MapGet("/apps", async (
            [FromQuery] string? search,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetKnowledgeAppsAsync(search, ct);
            return Results.Ok(ApiResponse<List<AppKnowledgeAppDto>>.Ok(list));
        })
        .Produces<ApiResponse<List<AppKnowledgeAppDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        appKnowledgeRead.MapGet("/apps/{appId:guid}/contexts", async (
            Guid appId,
            [FromServices] AppKnowledgeContextService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetByAppAsync(appId, ct);
            return Results.Ok(ApiResponse<List<AppKnowledgeContextDto>>.Ok(list));
        })
        .Produces<ApiResponse<List<AppKnowledgeContextDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        appKnowledgeWrite.MapPost("/contexts", async (
            [FromBody] SaveAppKnowledgeContextRequest req,
            [FromServices] AppKnowledgeContextService svc,
            CancellationToken ct) =>
        {
            try
            {
                var result = await svc.SaveAsync(req, ct);
                return Results.Ok(ApiResponse<AppKnowledgeContextDto>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<AppKnowledgeContextDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        appKnowledgeWrite.MapDelete("/contexts/{id:guid}", async (
            Guid id,
            [FromServices] AppKnowledgeContextService svc,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(id, ct);
            return ok
                ? Results.Ok(ApiResponse<string>.Ok("已删除。"))
                : Results.NotFound(ApiResponse<string>.Error(404, "未找到上下文知识。"));
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        appKnowledgeWrite.MapPost("/suggestions/{id:guid}/preview", async (
            Guid id,
            [FromBody] SuggestionClassificationPreviewRequest req,
            [FromServices] ActivityClassificationRecomputeService recompute,
            [FromServices] ClassificationRuleDraftService drafts,
            [FromServices] AppKnowledgeSuggestionService appKnowledge,
            CancellationToken ct) =>
        {
            try
            {
                var classificationPreview = await recompute.PreviewSuggestionAsync(id, req, drafts, ct);
                var result = await appKnowledge.BuildRecommendedContextAsync(
                    id,
                    req,
                    classificationPreview.Preview,
                    ct);
                return Results.Ok(ApiResponse<AppKnowledgeSuggestionPreviewDto>.Ok(result));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "未找到分类建议。"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<AppKnowledgeSuggestionPreviewDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        appKnowledgeWrite.MapPost("/suggestions/{id:guid}/apply", async (
            Guid id,
            [FromBody] SuggestionClassificationApplyRequest req,
            [FromServices] ActivityClassificationRecomputeService recompute,
            [FromServices] ClassificationRuleDraftService drafts,
            [FromServices] AppKnowledgeSuggestionService appKnowledge,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var previewRequest = new SuggestionClassificationPreviewRequest(
                    req.CategoryName,
                    req.ProjectTag,
                    req.Range);
                var classificationPreview = await recompute.PreviewSuggestionAsync(id, previewRequest, drafts, ct);
                var knowledgePreview = await appKnowledge.BuildRecommendedContextAsync(
                    id,
                    previewRequest,
                    classificationPreview.Preview,
                    ct);
                var (applied, savedContext) = await recompute.ApplySuggestionWithSideEffectAsync(
                    id,
                    req,
                    drafts,
                    async (appliedSuggestion, token) =>
                    {
                        var appliedKnowledgePreview = knowledgePreview with
                        {
                            Preview = appliedSuggestion.Preview,
                            RecommendedContext = knowledgePreview.RecommendedContext with
                            {
                                AffectedRecordCount = appliedSuggestion.Preview.AffectedRecordCount,
                                AffectedDurationSeconds = appliedSuggestion.Preview.AffectedDurationSeconds
                            }
                        };
                        return await appKnowledge.SaveRecommendedContextAsync(appliedKnowledgePreview, token);
                    },
                    ct);
                cache.EvictByPrefix("/api/v1/pc/");
                var result = new AppKnowledgeSuggestionApplyDto(
                    id,
                    savedContext,
                    applied.Preview,
                    applied.AuditId,
                    applied.SuggestionStatus,
                    $"已写入 App 知识库：{savedContext.ScopeSummary}。已重算 {applied.Preview.AffectedRecordCount} 条记录。");

                return Results.Ok(ApiResponse<AppKnowledgeSuggestionApplyDto>.Ok(result));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "未找到分类建议。"));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<AppKnowledgeSuggestionApplyDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        var kbRead = endpoints.MapGroup("/api/v1/pc/app-signatures").RequireAuthorization();
        var kbWrite = endpoints.MapGroup("/api/v1/pc/app-signatures").RequireAuthorization();

        kbRead.MapGet("/", async (
            [FromQuery] string? search,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetAllAsync(search, ct);
            return Results.Ok(ApiResponse<List<AppSignatureDto>>.Ok(list));
        })
        .Produces<ApiResponse<List<AppSignatureDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        kbRead.MapGet("/export", async (
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var data = await svc.ExportAsync(ct);
            return Results.Ok(ApiResponse<List<AppSignatureDto>>.Ok(data));
        })
        .Produces<ApiResponse<List<AppSignatureDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        kbWrite.MapPost("/import", async (
            [FromBody] List<SaveAppSignatureRequest> req,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var (imported, updated) = await svc.ImportAsync(req, ct);
            return Results.Ok(ApiResponse<AppSignatureImportResultDto>.Ok(new AppSignatureImportResultDto(imported, updated)));
        })
        .Produces<ApiResponse<AppSignatureImportResultDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        kbWrite.MapPost("/lookup", async (
            [FromBody] LookupAppSignatureRequest req,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var result = await svc.LookupOnlineAsync(req.ProcessName, ct);
            if (result is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "未找到应用签名"));
            return Results.Ok(ApiResponse<AppSignatureDto>.Ok(result));
        })
        .Produces<ApiResponse<AppSignatureDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        kbRead.MapGet("/count", async (
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var count = await svc.GetCountAsync(ct);
            return Results.Ok(ApiResponse<int>.Ok(count));
        })
        .Produces<ApiResponse<int>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        kbRead.MapGet("/lookup/{processName}", async (
            string processName,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var result = await svc.LookupByProcessNameAsync(processName, ct);
            if (result is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "未找到"));
            return Results.Ok(ApiResponse<AppSignatureDto>.Ok(result));
        })
        .Produces<ApiResponse<AppSignatureDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        kbWrite.MapPost("/", async (
            [FromBody] SaveAppSignatureRequest req,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var result = await svc.SaveAsync(req, ct);
            return Results.Ok(ApiResponse<AppSignatureDto>.Ok(result));
        })
        .Produces<ApiResponse<AppSignatureDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        kbWrite.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(id, ct);
            return ok
                ? Results.Ok(ApiResponse<string>.Ok("已删除"))
                : Results.BadRequest(ApiResponse<string>.Error(400, "内置项不可删除或不存在"));
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // === Phase 2: 服务端聚合（专注块 / 应用时长 / 深夜使用 / 分类分布 / 键鼠范围）===
        readGroup.MapGet("/aggregation/focus-blocks", async (
            [FromQuery] string? date,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] string? timezone,
            [FromServices] PcActivityAggregationService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            try
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request,
                        overrides:
                        [
                            new("date", date ?? string.Empty),
                            new("start", start ?? string.Empty),
                            new("end", end ?? string.Empty),
                            new("timezone", timezone ?? string.Empty),
                        ]),
                    force,
                    () => svc.GetFocusBlocksAsync(new PcAggregationQuery(date, start, end, timezone), ct),
                    ct);
                return Results.Ok(ApiResponse<PcFocusBlocksResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<PcFocusBlocksResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/aggregation/app-usage", async (
            [FromQuery] string? date,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] string? timezone,
            [FromQuery] int? limit,
            [FromServices] PcActivityAggregationService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            try
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request,
                        overrides:
                        [
                            new("date", date ?? string.Empty),
                            new("start", start ?? string.Empty),
                            new("end", end ?? string.Empty),
                            new("timezone", timezone ?? string.Empty),
                            new("limit", limit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                        ]),
                    force,
                    () => svc.GetAppUsageAsync(new PcAggregationQuery(date, start, end, timezone), limit, ct),
                    ct);
                return Results.Ok(ApiResponse<PcAppUsageResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<PcAppUsageResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/aggregation/late-night", async (
            [FromQuery] string? date,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] string? timezone,
            [FromServices] PcActivityAggregationService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            try
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request,
                        overrides:
                        [
                            new("date", date ?? string.Empty),
                            new("start", start ?? string.Empty),
                            new("end", end ?? string.Empty),
                            new("timezone", timezone ?? string.Empty),
                        ]),
                    force,
                    () => svc.GetLateNightAsync(new PcAggregationQuery(date, start, end, timezone), ct),
                    ct);
                return Results.Ok(ApiResponse<PcLateNightResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<PcLateNightResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/aggregation/category-distribution", async (
            [FromQuery] string? date,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] string? timezone,
            [FromServices] PcActivityAggregationService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            try
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request,
                        overrides:
                        [
                            new("date", date ?? string.Empty),
                            new("start", start ?? string.Empty),
                            new("end", end ?? string.Empty),
                            new("timezone", timezone ?? string.Empty),
                        ]),
                    force,
                    () => svc.GetCategoryDistributionAsync(new PcAggregationQuery(date, start, end, timezone), ct),
                    ct);
                return Results.Ok(ApiResponse<PcCategoryDistributionResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<PcCategoryDistributionResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // REQ-7（#368）：键鼠范围聚合。与单日版 pc/summary.keystats 字段同构，前端复用同一组件；
        // 结果规模不随天数线性膨胀（按键分布按 keyName 合并，Top 列表只留 TopN）。
        readGroup.MapGet("/aggregation/keystats", async (
            [FromQuery] string? date,
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromQuery] string? timezone,
            [FromServices] PcActivityAggregationService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            try
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request,
                        overrides:
                        [
                            new("date", date ?? string.Empty),
                            new("start", start ?? string.Empty),
                            new("end", end ?? string.Empty),
                            new("timezone", timezone ?? string.Empty),
                        ]),
                    force,
                    () => svc.GetKeystatsRangeAsync(new PcAggregationQuery(date, start, end, timezone), ct),
                    ct);
                return Results.Ok(ApiResponse<PcKeystatsRangeResponse>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        })
        .Produces<ApiResponse<PcKeystatsRangeResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // === Phase 2: 分类树 ===
        var catRead = endpoints.MapGroup("/api/v1/pc/categories").RequireAuthorization();
        var catWrite = endpoints.MapGroup("/api/v1/pc/categories").RequireAuthorization();

        // 原 catRead.MapGet("/")（树形列表）与 readGroup.MapGet("/categories")（扁平规则列表）
        // 同方法同路径，路由匹配歧义 500；树形读取的唯一入口是 /tree（web 与 MCP
        // get_pc_category_tree 都走它），重复的 "/" 注册已删除。
        catRead.MapGet("/tree", async (
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            var tree = await svc.GetTreeAsync(ct);
            return Results.Ok(ApiResponse<List<CategoryTreeNode>>.Ok(tree));
        })
        .Produces<ApiResponse<List<CategoryTreeNode>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        catRead.MapGet("/dictionary", async (
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetDictionaryAsync(ct);
            return Results.Ok(ApiResponse<List<CategoryDictionaryItemDto>>.Ok(list));
        })
        .Produces<ApiResponse<List<CategoryDictionaryItemDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        catWrite.MapPost("/", async (
            [FromBody] CategorySaveRequest req,
            [FromServices] PcCategoryService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            // Name 必须非空白：缺失/null/空串/纯空白都在进入 SaveAsync 之前拒绝，
            // 否则新建会落库空名、带 id 的更新会把已有合法节点整行覆盖（含 parentId 被清空）。
            // 只判 IsNullOrWhiteSpace，带前后空格的合法名称保持原样通过。
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, "分类名称不能为空"));
            }

            var result = await svc.SaveAsync(req, ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return Results.Ok(ApiResponse<CategoryTreeNode>.Ok(result));
        })
        .Produces<ApiResponse<CategoryTreeNode>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        catWrite.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] PcCategoryService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var ok = await svc.DeleteAsync(id, ct);
                cache.EvictByPrefix("/api/v1/pc/");
                return ok
                    ? Results.Ok(ApiResponse<string>.Ok("已删除"))
                    : Results.BadRequest(ApiResponse<string>.Error(400, "内置项不可删除或不存在"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        catWrite.MapPut("/reorder", async (
            [FromBody] ReorderCategoriesRequest req,
            [FromServices] PcCategoryService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            await svc.ReorderAsync(req, ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return Results.Ok(ApiResponse<string>.Ok("排序已更新"));
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        catWrite.MapPost("/seed", async (
            [FromServices] PcCategoryService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            await svc.SeedDefaultsAsync(ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return Results.Ok(ApiResponse<string>.Ok("种子数据已初始化"));
        })
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(500);

        // === Phase 2: Productivity ===
        var prodRead = endpoints.MapGroup("/api/v1/pc/productivity").RequireAuthorization();

        prodRead.MapGet("/dashboard", async (
            [FromQuery] string? date,
            [FromServices] PcProductivityService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("date", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                force,
                () => svc.GetDashboardAsync(d, ct),
                ct);
            return Results.Ok(ApiResponse<ProductivityDashboardDto>.Ok(result));
        })
        .Produces<ApiResponse<ProductivityDashboardDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        prodRead.MapGet("/range", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcProductivityService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var s = start is not null ? DateTime.Parse(start, CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-7);
            var e = end is not null ? DateTime.Parse(end, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("start", s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), new("end", e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                force,
                () => svc.GetRangeAsync(s, e, ct),
                ct);
            return Results.Ok(ApiResponse<List<DailyProductivityDto>>.Ok(result));
        })
        .Produces<ApiResponse<List<DailyProductivityDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // === Phase 2: 时间线 v2 ===
        readGroup.MapGet("/productivity/goals", async (
            [FromServices] PcProductivityService svc,
            CancellationToken ct) =>
        {
            var goal = await svc.GetGoalsAsync(ct);
            return Results.Ok(ApiResponse<ProductivityGoalDto>.Ok(goal));
        })
        .Produces<ApiResponse<ProductivityGoalDto>>(200)
        .Produces<ApiResponse<string>>(500);

        writeGroup.MapPut("/productivity/goals", async (
            [FromBody] ProductivityGoalDto req,
            [FromServices] PcProductivityService svc,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var goal = await svc.UpdateGoalsAsync(req, ct);
            cache.EvictByPrefix("/api/v1/pc/");
            return Results.Ok(ApiResponse<ProductivityGoalDto>.Ok(goal));
        })
        .Produces<ApiResponse<ProductivityGoalDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        readGroup.MapGet("/timeline/v2", async (
            [FromQuery] string? date,
            [FromServices] PcProductivityService svc,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var d = svc.ResolveBusinessDay(date is not null
                ? DateTime.Parse(date, CultureInfo.InvariantCulture)
                : null);
            var result = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request, overrides: [new("date", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
                force,
                () => svc.GetTimelineV2Async(d, ct),
                ct);
            return Results.Ok(ApiResponse<List<TimelineV2Item>>.Ok(result));
        })
        .Produces<ApiResponse<List<TimelineV2Item>>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<PcTrackerSchemaInitializer>();
        await initializer.InitializeAsync();

        var categories = scope.ServiceProvider.GetRequiredService<PcCategoryService>();
        await categories.SeedDefaultsAsync(CancellationToken.None);

        // Schedule recurring classification snapshot backfill
        try
        {
            var recurring = serviceProvider.GetService<IRecurringJobManager>();
            if (recurring is null)
            {
                serviceProvider.GetService<ILogger<PcTrackerModule>>()?.LogWarning(
                    "Background job infrastructure is not available; classification snapshot backfill is disabled.");
            }
            else
            {
                recurring.AddOrUpdate<PcClassificationSnapshotJob>(
                    "pc-classification-snapshot",
                    job => job.RunAsync(),
                    "*/30 * * * *");
            }
        }
        catch (Exception exception)
        {
            serviceProvider.GetService<ILogger<PcTrackerModule>>()?.LogWarning(
                exception,
                "Failed to schedule the recurring classification snapshot backfill job.");
        }
    }

    private static DateTime? TryParseDate(string? value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.Date
            : null;
    }

    private static bool NeedsClassificationSuggestion(PcDetailRecord record)
    {
        return string.Equals(record.ClassificationSource, "fallback", StringComparison.OrdinalIgnoreCase)
            || (record.ClassificationConfidence is not null && record.ClassificationConfidence < 0.5);
    }
}

/// <summary>契约注解用：签名导入结果（与原匿名对象 JSON 等价：camelCase 属性、同字段同序）。</summary>
public sealed record AppSignatureImportResultDto(int Imported, int Updated);
