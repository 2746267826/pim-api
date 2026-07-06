using System.Reflection;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
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
        services.AddScoped<ActivitySuggestionService>();
        services.AddScoped<ActivityClassificationSnapshotService>();
        services.AddScoped<ActivityClassificationRecomputeService>();
        services.AddScoped<ActivityClassificationSettingsService>();
        services.AddScoped<ActivityTimelineSmoothingService>();
        services.AddScoped<ActivityClassificationRuleService>();
        services.AddScoped<ClassificationRuleDraftService>();
        services.AddScoped<PcTrackerSchemaInitializer>();
        services.AddScoped<AppSignatureService>();
        services.AddScoped<AppKnowledgeContextService>();
        services.AddScoped<AppKnowledgeSuggestionService>();
        services.AddScoped<PcCategoryService>();
        services.AddScoped<PcProductivityService>();
        services.AddScoped<PcActivityRecordKeyService>();
        services.AddScoped<PcActivityAnalysisService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var readGroup = endpoints.MapGroup("/api/v1/pc");
        var writeGroup = endpoints.MapGroup("/api/v1/pc")
            .RequireAuthorization();

        writeGroup.MapPost("/keystats/upload", async (
            [FromBody] KeystatsUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            await svc.UpsertKeystatsAsync(req, ct);
            return Results.Ok(ApiResponse<string>.Ok("已接收"));
        });

        writeGroup.MapPost("/keystats/samples", async (
            [FromBody] KeystatsSampleUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            await svc.UpsertKeystatsSampleAsync(req, ct);
            return Results.Ok(ApiResponse<string>.Ok("已接收"));
        });

        writeGroup.MapPost("/aw/upload", async (
            [FromBody] AwEventsUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var count = await svc.UploadAwEventsAsync(req, ct);
            return Results.Ok(ApiResponse<int>.Ok(count));
        });

        writeGroup.MapPost("/aw/upload-complete", async (
            [FromBody] CompleteAwUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var count = await svc.UploadCompleteAwEventsAsync(req, ct);
            return Results.Ok(ApiResponse<int>.Ok(count));
        });

        readGroup.MapGet("/summary", async (
            [FromQuery] string? date,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetSummaryAsync(d, ct);
            return Results.Ok(ApiResponse<PcSummaryResponse>.Ok(result));
        });

        readGroup.MapGet("/aw/timeline", async (
            [FromQuery] string? date,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetTimelineAsync(d, ct);
            return Results.Ok(ApiResponse<List<TimelineItem>>.Ok(result));
        });

        readGroup.MapGet("/aw/heatmap", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var s = start is not null ? DateTime.Parse(start, CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-7);
            var e = end is not null ? DateTime.Parse(end, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetHeatmapAsync(s, e, ct);
            return Results.Ok(ApiResponse<List<HeatmapBucket>>.Ok(result));
        });

        readGroup.MapGet("/keystats/range", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var s = start is not null ? DateTime.Parse(start, CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-7);
            var e = end is not null ? DateTime.Parse(end, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetKeystatsRangeAsync(s, e, ct);
            return Results.Ok(ApiResponse<List<KeystatsSummary>>.Ok(result));
        });

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
        });

        readGroup.MapGet("/quality", async (
            [FromQuery] string? date,
            [FromQuery] string? dateFrom,
            [FromQuery] string? dateTo,
            [FromServices] PcTrackerQualityService svc,
            CancellationToken ct) =>
        {
            var result = await svc.GetQualityAsync(
                TryParseDate(date),
                TryParseDate(dateFrom),
                TryParseDate(dateTo),
                ct);
            return Results.Ok(ApiResponse<PcQualityResponse>.Ok(result));
        });

        readGroup.MapGet("/categories", async (
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetAllCategoriesAsync(ct);
            return Results.Ok(ApiResponse<List<AppCategoryRule>>.Ok(list));
        });

        writeGroup.MapPost("/categories", async (
            [FromBody] SaveCategoryRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var result = await svc.SaveCategoryAsync(req, ct);
            return Results.Ok(ApiResponse<AppCategoryRule>.Ok(result));
        });

        writeGroup.MapDelete("/categories/{id}", async (
            Guid id,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteCategoryAsync(id, ct);
            return ok
                ? Results.Ok(ApiResponse<string>.Ok("已删除"))
                : Results.NotFound(ApiResponse<string>.Error(404, "不存在或为内置项"));
        });

        readGroup.MapGet("/classification/rules", async (
            [FromServices] ActivityClassificationRuleService svc,
            CancellationToken ct) =>
        {
            var rules = await svc.ListAsync(ct);
            return Results.Ok(ApiResponse<List<ActivityClassificationRuleDto>>.Ok(rules));
        });

        readGroup.MapGet("/classification/suggestions", async (
            [FromQuery] string? date,
            [FromServices] PcTrackerService pcTrackerService,
            [FromServices] ActivitySuggestionService suggestionService,
            [FromServices] ActivityClassificationSettingsService settingsService,
            CancellationToken ct) =>
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
            var detail = await pcTrackerService.QueryCompleteDetailAsync(q, ct);
            var records = detail.Items
                .Where(NeedsClassificationSuggestion)
                .ToList();
            var settings = await settingsService.GetSettingsAsync(ct);
            var suggestions = await suggestionService.BuildSuggestionsAsync(
                records,
                settings.RecommendedMinimumClassificationDurationMinutes,
                ct);
            return Results.Ok(ApiResponse<List<ActivityClassificationSuggestionDto>>.Ok(suggestions));
        });

        readGroup.MapGet("/classification/project-tags/recent", async (
            [FromServices] ActivitySuggestionService suggestionService,
            CancellationToken ct) =>
        {
            var tags = await suggestionService.GetRecentProjectTagsAsync(ct);
            return Results.Ok(ApiResponse<List<string>>.Ok(tags));
        });

        readGroup.MapGet("/classification/settings", async (
            [FromServices] ActivityClassificationSettingsService settingsService,
            CancellationToken ct) =>
        {
            var settings = await settingsService.GetSettingsAsync(ct);
            return Results.Ok(ApiResponse<ActivityClassificationSettingsDto>.Ok(settings));
        });

        readGroup.MapGet("/activity-analysis", async (
            [FromQuery] string? date,
            [FromQuery] int? blockMinutes,
            [FromServices] PcActivityAnalysisService svc,
            CancellationToken ct) =>
        {
            try
            {
                var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
                var result = await svc.GetDailyAnalysisAsync(d, blockMinutes ?? 60, ct);
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
        });

        writeGroup.MapPost("/classification/rules", async (
            [FromBody] SaveActivityClassificationRuleRequest req,
            [FromServices] ActivityClassificationRuleService svc,
            CancellationToken ct) =>
        {
            try
            {
                var rule = await svc.SaveAsync(req, ct);
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
        });

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
        });

        writeGroup.MapPost("/classification/rules/apply", async (
            [FromBody] ApplyActivityClassificationRuleRequest req,
            [FromServices] ActivityClassificationRecomputeService svc,
            CancellationToken ct) =>
        {
            try
            {
                var preview = await svc.ApplyRuleAsync(req.Rule, req.Range, ct);
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
        });

        writeGroup.MapPut("/classification/settings", async (
            [FromBody] SaveActivityClassificationSettingsRequest req,
            [FromServices] ActivityClassificationSettingsService settingsService,
            CancellationToken ct) =>
        {
            var settings = await settingsService.SaveSettingsAsync(
                req.RecommendedMinimumClassificationDurationMinutes,
                ct);
            return Results.Ok(ApiResponse<ActivityClassificationSettingsDto>.Ok(settings));
        });

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
                return Results.NotFound(ApiResponse<string>.Error(404, "Suggestion not found."));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        });

        writeGroup.MapPost("/classification/suggestions/{id:guid}/apply", async (
            Guid id,
            [FromBody] SuggestionClassificationApplyRequest req,
            [FromServices] ActivityClassificationRecomputeService recompute,
            [FromServices] ClassificationRuleDraftService drafts,
            CancellationToken ct) =>
        {
            try
            {
                var result = await recompute.ApplySuggestionAsync(id, req, drafts, ct);
                return Results.Ok(ApiResponse<ActivityClassificationSuggestionApplyDto>.Ok(result));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "Suggestion not found."));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        });

        writeGroup.MapPost("/classification/suggestions/{id:guid}/accept", async (
            Guid id,
            [FromBody] AcceptActivityClassificationSuggestionRequest req,
            [FromServices] ActivitySuggestionService suggestionService,
            CancellationToken ct) =>
        {
            try
            {
                var rule = await suggestionService.AcceptSuggestionAsync(id, req, ct);
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
        });

        writeGroup.MapPost("/classification/suggestions/{id:guid}/reject", async (
            Guid id,
            [FromServices] ActivitySuggestionService suggestionService,
            CancellationToken ct) =>
        {
            try
            {
                await suggestionService.RejectSuggestionAsync(id, ct);
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
        });

        writeGroup.MapPost("/classification/recompute", async (
            [FromBody] ActivityClassificationRecomputeRequest req,
            [FromServices] ActivityClassificationRecomputeService svc,
            CancellationToken ct) =>
        {
            try
            {
                var result = await svc.RecomputeAsync(req.Range, ct);
                return Results.Ok(ApiResponse<ActivityClassificationRecomputeDto>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
        });

        readGroup.MapGet("/heatmap/grid", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcTrackerService svc,
            CancellationToken ct,
            [FromQuery] string dimension = "day") =>
        {
            var s = start is not null ? DateTime.Parse(start, CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-30);
            var e = end is not null ? DateTime.Parse(end, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetHeatmapGridAsync(s, e, dimension, ct);
            return Results.Ok(ApiResponse<HeatmapGridResponse>.Ok(result));
        });

        // App Knowledge Base endpoints
        var appKnowledgeRead = endpoints.MapGroup("/api/v1/pc/app-knowledge").AllowAnonymous();
        var appKnowledgeWrite = endpoints.MapGroup("/api/v1/pc/app-knowledge").RequireAuthorization();

        appKnowledgeRead.MapGet("/apps", async (
            [FromQuery] string? search,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetKnowledgeAppsAsync(search, ct);
            return Results.Ok(ApiResponse<List<AppKnowledgeAppDto>>.Ok(list));
        });

        appKnowledgeRead.MapGet("/apps/{appId:guid}/contexts", async (
            Guid appId,
            [FromServices] AppKnowledgeContextService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetByAppAsync(appId, ct);
            return Results.Ok(ApiResponse<List<AppKnowledgeContextDto>>.Ok(list));
        });

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
        });

        appKnowledgeWrite.MapDelete("/contexts/{id:guid}", async (
            Guid id,
            [FromServices] AppKnowledgeContextService svc,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(id, ct);
            return ok
                ? Results.Ok(ApiResponse<string>.Ok("Deleted."))
                : Results.NotFound(ApiResponse<string>.Error(404, "Context not found."));
        });

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
                return Results.NotFound(ApiResponse<string>.Error(404, "Suggestion not found."));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        });

        appKnowledgeWrite.MapPost("/suggestions/{id:guid}/apply", async (
            Guid id,
            [FromBody] SuggestionClassificationApplyRequest req,
            [FromServices] ActivityClassificationRecomputeService recompute,
            [FromServices] ClassificationRuleDraftService drafts,
            [FromServices] AppKnowledgeSuggestionService appKnowledge,
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
                var savedContext = await appKnowledge.SaveRecommendedContextAsync(knowledgePreview, ct);
                var applied = await recompute.ApplySuggestionAsync(id, req, drafts, ct);
                var result = new AppKnowledgeSuggestionApplyDto(
                    id,
                    savedContext,
                    applied.Preview,
                    applied.AuditId,
                    applied.SuggestionStatus,
                    $"Saved to App Knowledge: {savedContext.ScopeSummary}. Recomputed {applied.Preview.AffectedRecordCount} records.");

                return Results.Ok(ApiResponse<AppKnowledgeSuggestionApplyDto>.Ok(result));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse<string>.Error(404, "Suggestion not found."));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        });

        var kbRead = endpoints.MapGroup("/api/v1/pc/app-signatures").AllowAnonymous();
        var kbWrite = endpoints.MapGroup("/api/v1/pc/app-signatures").RequireAuthorization();

        kbRead.MapGet("/", async (
            [FromQuery] string? search,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var list = await svc.GetAllAsync(search, ct);
            return Results.Ok(ApiResponse<List<AppSignatureDto>>.Ok(list));
        });

        kbRead.MapGet("/count", async (
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var count = await svc.GetCountAsync(ct);
            return Results.Ok(ApiResponse<int>.Ok(count));
        });

        kbRead.MapGet("/lookup/{processName}", async (
            string processName,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var result = await svc.LookupByProcessNameAsync(processName, ct);
            if (result is null)
                return Results.NotFound(ApiResponse<string>.Error(404, "未找到"));
            return Results.Ok(ApiResponse<AppSignatureDto>.Ok(result));
        });

        kbWrite.MapPost("/", async (
            [FromBody] SaveAppSignatureRequest req,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var result = await svc.SaveAsync(req, ct);
            return Results.Ok(ApiResponse<AppSignatureDto>.Ok(result));
        });

        kbWrite.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] AppSignatureService svc,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(id, ct);
            return ok
                ? Results.Ok(ApiResponse<string>.Ok("已删除"))
                : Results.BadRequest(ApiResponse<string>.Error(400, "内置项不可删除或不存在"));
        });

        // === Phase 2: 分类树 ===
        var catRead = endpoints.MapGroup("/api/v1/pc/categories").AllowAnonymous();
        var catWrite = endpoints.MapGroup("/api/v1/pc/categories").RequireAuthorization();

        catRead.MapGet("/", async (
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            var tree = await svc.GetTreeAsync(ct);
            return Results.Ok(ApiResponse<List<CategoryTreeNode>>.Ok(tree));
        });

        catRead.MapGet("/tree", async (
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            var tree = await svc.GetTreeAsync(ct);
            return Results.Ok(ApiResponse<List<CategoryTreeNode>>.Ok(tree));
        });

        catWrite.MapPost("/", async (
            [FromBody] CategorySaveRequest req,
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            var result = await svc.SaveAsync(req, ct);
            return Results.Ok(ApiResponse<CategoryTreeNode>.Ok(result));
        });

        catWrite.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            try
            {
                var ok = await svc.DeleteAsync(id, ct);
                return ok
                    ? Results.Ok(ApiResponse<string>.Ok("已删除"))
                    : Results.BadRequest(ApiResponse<string>.Error(400, "内置项不可删除或不存在"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ApiResponse<string>.Error(409, ex.Message));
            }
        });

        catWrite.MapPut("/reorder", async (
            [FromBody] ReorderCategoriesRequest req,
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            await svc.ReorderAsync(req, ct);
            return Results.Ok(ApiResponse<string>.Ok("排序已更新"));
        });

        catWrite.MapPost("/seed", async (
            [FromServices] PcCategoryService svc,
            CancellationToken ct) =>
        {
            await svc.SeedDefaultsAsync(ct);
            return Results.Ok(ApiResponse<string>.Ok("种子数据已初始化"));
        });

        // === Phase 2: Productivity ===
        var prodRead = endpoints.MapGroup("/api/v1/pc/productivity").AllowAnonymous();

        prodRead.MapGet("/dashboard", async (
            [FromQuery] string? date,
            [FromServices] PcProductivityService svc,
            CancellationToken ct) =>
        {
            var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetDashboardAsync(d, ct);
            return Results.Ok(ApiResponse<ProductivityDashboardDto>.Ok(result));
        });

        prodRead.MapGet("/range", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcProductivityService svc,
            CancellationToken ct) =>
        {
            var s = start is not null ? DateTime.Parse(start, CultureInfo.InvariantCulture) : DateTime.Today.AddDays(-7);
            var e = end is not null ? DateTime.Parse(end, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetRangeAsync(s, e, ct);
            return Results.Ok(ApiResponse<List<DailyProductivityDto>>.Ok(result));
        });

        // === Phase 2: 时间线 v2 ===
        readGroup.MapGet("/timeline/v2", async (
            [FromQuery] string? date,
            [FromServices] PcProductivityService svc,
            CancellationToken ct) =>
        {
            var d = date is not null ? DateTime.Parse(date, CultureInfo.InvariantCulture) : DateTime.Today;
            var result = await svc.GetTimelineV2Async(d, ct);
            return Results.Ok(ApiResponse<List<TimelineV2Item>>.Ok(result));
        });
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<PcTrackerSchemaInitializer>();
        await initializer.InitializeAsync();

        var categories = scope.ServiceProvider.GetRequiredService<PcCategoryService>();
        await categories.SeedDefaultsAsync(CancellationToken.None);
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
