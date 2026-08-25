using System.Reflection;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Core.Caching;
using Pim.Core.Modules;
using Pim.Module.Mobile.Entities;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Services;

namespace Pim.Module.Mobile;

public sealed class MobileModule : IModule
{
    public string Name => "mobile";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<MobileDeviceService>();
        services.AddScoped<MobileGapService>();
        services.AddScoped<MobileUsageIngestService>();
        services.AddScoped<MobileSessionInterpreter>();
        services.AddScoped<MobileLocationService>();
        services.AddScoped<MobileLocationQueryService>();
        services.AddScoped<MobileLocationAggregationService>();
        services.AddScoped<MobileFrequentPlaceService>();
        services.AddScoped<MobileMovementStatsService>();
        services.AddScoped<MobileUsageQueryService>();
        services.AddScoped<MobileQualityService>();
        services.AddScoped<MobileAnalyticsQueryService>();
        services.AddScoped<MobileAppClassificationService>();
        services.AddScoped<MobileAppCatalogOverrideService>();
        services.AddScoped<MobileUsageGoalService>();
        services.AddScoped<MobileUsageAggregationService>();
        services.AddScoped<MobileTimelineBlockService>();
        services.AddScoped<DeviceManagementService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(MobileEndpointPaths.Root)
            .RequireAuthorization();

        group.MapGet("/devices", async (
            [FromServices] MobileDeviceService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileDeviceDto>>.Ok(await service.ListAsync(ct))));

        group.MapGet("/devices/manage", async ([FromServices] DeviceManagementService svc, [FromQuery] string? sortBy, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<DeviceListDto>>.Ok(await svc.ListDevicesAsync(sortBy, ct))));
        group.MapGet("/devices/{deviceId}/detail", async ([FromRoute] string deviceId, [FromServices] DeviceManagementService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<DeviceDetailDto>.Ok(await svc.GetDetailAsync(deviceId, ct))));
        group.MapPost("/devices/{deviceId}/rename", async ([FromRoute] string deviceId, [FromBody] DeviceRenameRequest req, [FromServices] DeviceManagementService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<DeviceDto>.Ok(await svc.RenameAsync(deviceId, req.DisplayName, ct))));
        group.MapPost("/devices/merge/preview", async ([FromBody] DeviceMergeRequest req, [FromServices] DeviceManagementService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<DeviceMergePreviewDto>.Ok(await svc.PreviewMergeAsync(req.SourceDeviceIds, req.TargetDeviceId, ct))));
        group.MapPost("/devices/merge", async ([FromBody] DeviceMergeRequest req, [FromServices] DeviceManagementService svc, CancellationToken ct) =>
        {
            await svc.MergeAsync(req.SourceDeviceIds, req.TargetDeviceId, ct);
            return Results.Ok(ApiResponse<string>.Ok("merged"));
        });
        group.MapGet("/devices/{deviceId}/delete-preview", async ([FromRoute] string deviceId, [FromServices] DeviceManagementService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<DeviceDeletePreviewDto>.Ok(await svc.PreviewDeleteAsync(deviceId, ct))));
        group.MapDelete("/devices/{deviceId}", async ([FromRoute] string deviceId, [FromServices] DeviceManagementService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(deviceId, ct);
            return Results.Ok(ApiResponse<string>.Ok("deleted"));
        });
        group.MapGet("/devices/{deviceId}/export", async ([FromRoute] string deviceId, [FromServices] DeviceManagementService svc, CancellationToken ct) =>
        {
            var exp = await svc.ExportAsync(deviceId, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(exp.Json), "application/json", exp.FileName);
        });

        group.MapPost("/devices/register", async (
            [FromBody] MobileDeviceRegisterRequest request,
            [FromServices] MobileDeviceService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileDeviceDto>.Ok(await service.RegisterAsync(request, ct))));

        group.MapPost("/sync/gaps", async (
            [FromBody] MobileGapRequest request,
            [FromServices] MobileGapService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileGapResponse>.Ok(await service.GetGapsAsync(request, ct))));

        group.MapPost("/usage/events", async (
            [FromBody] MobileUsageEventsUploadRequest request,
            [FromServices] MobileUsageIngestService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileUsageIngestResult>.Ok(await service.IngestAsync(request, ct))));

        group.MapPost("/location/points", async (
            [FromBody] MobileLocationPointRequest request,
            [FromServices] MobileLocationService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileLocationPointDto>.Ok(await service.SubmitAsync(request, ct))));

        group.MapGet("/summary", async (
            [FromQuery] string? date,
            [FromQuery] string? deviceId,
            [FromQuery] DateTimeOffset? rangeStartUtc,
            [FromQuery] DateTimeOffset? rangeEndUtc,
            [FromServices] MobileUsageQueryService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileUsageSummaryResponse>.Ok(await service.GetSummaryAsync(
                BuildSummaryQuery(deviceId, date, rangeStartUtc, rangeEndUtc),
                ct))));

        group.MapGet("/timeline", async (
            [FromQuery] string? date,
            [FromQuery] string? deviceId,
            [FromQuery] DateTimeOffset? rangeStartUtc,
            [FromQuery] DateTimeOffset? rangeEndUtc,
            [FromServices] MobileUsageQueryService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileTimelineResponse>.Ok(await service.GetTimelineAsync(
                BuildSummaryQuery(deviceId, date, rangeStartUtc, rangeEndUtc),
                ct))));

        group.MapGet("/location/history", async (
            [FromQuery] string? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end,
            [FromQuery] double? maxAccuracyMeters,
            [FromQuery] DateTimeOffset? rangeStartUtc,
            [FromQuery] DateTimeOffset? rangeEndUtc,
            [FromServices] MobileLocationService service,
            CancellationToken ct) =>
        {
            var effectiveStart = start ?? rangeStartUtc;
            var effectiveEnd = end ?? rangeEndUtc;
            var effectiveMaxAccuracy = maxAccuracyMeters is > 0 ? maxAccuracyMeters.Value : 50;
            var points = await service.GetHistoryAsync(
                deviceId,
                effectiveStart,
                effectiveEnd,
                effectiveMaxAccuracy,
                ct);
            return Results.Ok(ApiResponse<MobileLocationHistoryResponse>.Ok(new MobileLocationHistoryResponse(
                effectiveStart,
                effectiveEnd,
                deviceId,
                effectiveMaxAccuracy,
                points)));
        });

        group.MapGet("/location/analytics/overview", async (
            [AsParameters] MobileLocationEndpointQuery query,
            [FromServices] MobileLocationAggregationService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<MobileLocationAnalyticsOverviewResponse>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetOverviewAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/location/analytics/tracks", async (
            [AsParameters] MobileLocationEndpointQuery query,
            [FromServices] MobileLocationAggregationService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileLocationTrackDto>>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetTracksAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/location/analytics/segments/{segmentId}", async (
            [FromRoute] string segmentId,
            [AsParameters] MobileLocationEndpointQuery query,
            [FromServices] MobileLocationAggregationService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            var segment = await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetSegmentAsync(segmentId, query.ToRequest(), ct),
                ct);
            return segment is null
                ? Results.NotFound(ApiResponse<string>.Error(404, "Location segment not found."))
                : Results.Ok(ApiResponse<MobileLocationSegmentDto>.Ok(segment));
        });

        group.MapGet("/location/analytics/frequent-places", async (
            [AsParameters] MobileLocationEndpointQuery query,
            [FromServices] MobileFrequentPlaceService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<MobileFrequentPlacesResponse>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetFrequentPlacesAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/location/analytics/movement-stats", async (
            [AsParameters] MobileLocationEndpointQuery query,
            [FromServices] MobileMovementStatsService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<MobileMovementStatsResponse>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetMovementStatsAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/location/analytics/segments/{segmentId}/points", async (
            [FromRoute] string segmentId,
            [AsParameters] MobileLocationEndpointQuery query,
            [FromServices] MobileLocationAggregationService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileLocationSegmentPointPageDto>.Ok(await service.GetSegmentPointsAsync(segmentId, query.ToRequest(), ct))));

        group.MapGet("/quality", async (
            [FromQuery] string? date,
            [FromQuery] string? deviceId,
            [FromQuery] DateTimeOffset? rangeStartUtc,
            [FromQuery] DateTimeOffset? rangeEndUtc,
            [FromServices] MobileQualityService service,
            CancellationToken ct) =>
        {
            var query = BuildSummaryQuery(deviceId, date, rangeStartUtc, rangeEndUtc);
            return Results.Ok(ApiResponse<MobileQualityResponse>.Ok(await service.GetQualityAsync(
                query.RangeStartUtc,
                query.RangeEndUtc,
                ct,
                query.DeviceId)));
        });

        group.MapGet("/analytics/overview", async (
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileUsageAggregationService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<MobileAnalyticsOverviewResponse>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetOverviewAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/analytics/heatmap", async (
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileUsageAggregationService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileHeatmapBucketDto>>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetHeatmapAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/analytics/charts", async (
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileUsageAggregationService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileAnalyticsChartDto>>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetChartsAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/analytics/timeline-blocks", async (
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileTimelineBlockService service,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
            Results.Ok(ApiResponse<MobileTimelineBlockPageDto>.Ok(await cache.GetOrCreateAsync(
                AggregateResultCacheKeys.Build(httpContext.Request),
                force,
                () => service.GetBlocksAsync(query.ToRequest(), ct),
                ct))));

        group.MapGet("/analytics/timeline-blocks/{blockId}/sessions", async (
            [FromRoute] string blockId,
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileTimelineBlockService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileTimelineBlockSessionDto>>.Ok(await service.GetSessionsForBlockAsync(blockId, query.ToRequest(), ct))));

        group.MapGet("/analytics/sessions/{sessionId}/events", async (
            [FromRoute] string sessionId,
            [FromServices] MobileTimelineBlockService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileSessionEventDto>>.Ok(await service.GetSessionEventsAsync(sessionId, ct))));

        group.MapGet("/analytics/goals", async (
            [FromServices] MobileUsageGoalService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileUsageGoalDto>>.Ok(await service.ListAsync(ct))));

        group.MapPost("/analytics/goals", async (
            [FromBody] MobileUsageGoalUpsertRequest request,
            [FromServices] MobileUsageGoalService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var result = await service.SaveAsync(request, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<MobileUsageGoalDto>.Ok(result));
        });

        group.MapDelete("/analytics/goals/{goalId}", async (
            [FromRoute] string goalId,
            [FromServices] MobileUsageGoalService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var ok = await service.DeleteAsync(goalId, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<string>.Ok(ok ? goalId : string.Empty));
        });

        group.MapGet("/apps/catalog-overrides", async (
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileAppCatalogOverrideDto>>.Ok(await service.ListOverridesAsync(ct))));

        group.MapPut("/apps/catalog-overrides", async (
            [FromBody] MobileAppCatalogOverrideUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var result = await service.UpsertOverrideAsync(request, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<MobileAppCatalogOverrideDto>.Ok(result));
        });

        group.MapPut("/apps/catalog-overrides/{packageName}", async (
            [FromRoute] string packageName,
            [FromBody] MobileAppCatalogOverrideUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var result = await service.UpsertOverrideAsync(request with { PackageName = packageName }, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<MobileAppCatalogOverrideDto>.Ok(result));
        });

        group.MapDelete("/apps/catalog-overrides/{packageName}", async (
            [FromRoute] string packageName,
            [FromServices] MobileAppCatalogOverrideService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var ok = await service.DeleteOverrideAsync(packageName, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<string>.Ok(ok ? packageName : string.Empty));
        });

        group.MapGet("/apps/category-rules", async (
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileAppCategoryRuleDto>>.Ok(await service.ListCategoryRulesAsync(ct))));

        group.MapPost("/apps/category-rules", async (
            [FromBody] MobileAppCategoryRuleUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var result = await service.CreateCategoryRuleAsync(request, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<MobileAppCategoryRuleDto>.Ok(result));
        });

        group.MapPut("/apps/category-rules/{ruleId}", async (
            [FromRoute] string ruleId,
            [FromBody] MobileAppCategoryRuleUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var result = await service.UpdateCategoryRuleAsync(ruleId, request, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<MobileAppCategoryRuleDto>.Ok(result));
        });

        group.MapDelete("/apps/category-rules/{ruleId}", async (
            [FromRoute] string ruleId,
            [FromServices] MobileAppCatalogOverrideService service,
            [FromServices] IAggregateResultCache cache,
            CancellationToken ct) =>
        {
            var ok = await service.DeleteCategoryRuleAsync(ruleId, ct);
            cache.EvictByPrefix("/api/v1/mobile/");
            return Results.Ok(ApiResponse<string>.Ok(ok ? ruleId : string.Empty));
        });
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        try
        {
            var quality = scope.ServiceProvider.GetService<MobileQualityService>();
            if (quality != null)
            {
                // trigger cleanup on startup; use current user context if available – for tests this is no-op
                // best effort: iterate known users via devices
                var db = scope.ServiceProvider.GetService<Pim.Infrastructure.Data.PimDbContext>();
                if (db != null)
                {
                    var userIds = await db.Set<MobileDeviceEntity>().Select(d => d.UserId).Distinct().ToListAsync();
                    foreach (var uid in userIds)
                    {
                        // cleanup is per-user via CurrentUser; skip auto for now to avoid missing context
                    }
                }
            }
        }
        catch { }
    }

    private sealed record DeviceRenameRequest(string DisplayName);
    public sealed record DeviceMergeRequest(IReadOnlyList<string> SourceDeviceIds, string TargetDeviceId);

    private static MobileSummaryQuery BuildSummaryQuery(
        string? deviceId,
        string? date,
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc)
    {
        if (string.IsNullOrWhiteSpace(date))
            return new MobileSummaryQuery(deviceId, rangeStartUtc, rangeEndUtc);

        if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return new MobileSummaryQuery(deviceId, rangeStartUtc, rangeEndUtc);

        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new MobileSummaryQuery(deviceId, start, start.AddDays(1));
    }
}

public static class MobileEndpointPaths
{
    public const string Root = "/api/v1/mobile";
    public const string Devices = $"{Root}/devices";
    public const string RegisterDevice = $"{Devices}/register";
    public const string SyncGaps = $"{Root}/sync/gaps";
    public const string UsageEvents = $"{Root}/usage/events";
    public const string LocationPoints = $"{Root}/location/points";
    public const string Summary = $"{Root}/summary";
    public const string Timeline = $"{Root}/timeline";
    public const string LocationHistory = $"{Root}/location/history";
    public const string Quality = $"{Root}/quality";
}

public sealed record MobileAnalyticsEndpointQuery(
    DateTimeOffset? RangeStartUtc,
    DateTimeOffset? RangeEndUtc,
    string? Timezone,
    string? DeviceId,
    string? Category,
    string? PackageName,
    string? Source,
    bool? IncludeSystemNoise,
    int? MinDurationSeconds,
    string? Granularity,
    string? Cursor,
    int? Page,
    int? PageSize)
{
    public MobileAnalyticsQueryRequest ToRequest()
        => new(
            RangeStartUtc,
            RangeEndUtc,
            Timezone,
            DeviceId,
            Category,
            PackageName,
            Source,
            IncludeSystemNoise,
            MinDurationSeconds,
            Granularity,
            Cursor,
            Page,
            PageSize);
}

public sealed record MobileLocationEndpointQuery(
    DateTimeOffset? RangeStartUtc,
    DateTimeOffset? RangeEndUtc,
    string? Timezone,
    string? DeviceId,
    double? MaxAccuracyMeters,
    bool? IncludeRejected,
    string? Cursor,
    int? PageSize)
{
    public MobileLocationQueryRequest ToRequest()
        => new(
            RangeStartUtc,
            RangeEndUtc,
            Timezone,
            DeviceId,
            MaxAccuracyMeters,
            IncludeRejected,
            Cursor,
            PageSize);
}
