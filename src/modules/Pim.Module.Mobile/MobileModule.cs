using System.Reflection;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pim.Core.Common;
using Pim.Core.Modules;
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
        services.AddScoped<MobileUsageQueryService>();
        services.AddScoped<MobileQualityService>();
        services.AddScoped<MobileAnalyticsQueryService>();
        services.AddScoped<MobileAppClassificationService>();
        services.AddScoped<MobileAppCatalogOverrideService>();
        services.AddScoped<MobileUsageGoalService>();
        services.AddScoped<MobileUsageAggregationService>();
        services.AddScoped<MobileTimelineBlockService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(MobileEndpointPaths.Root)
            .RequireAuthorization();

        group.MapGet("/devices", async (
            [FromServices] MobileDeviceService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileDeviceDto>>.Ok(await service.ListAsync(ct))));

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
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileAnalyticsOverviewResponse>.Ok(await service.GetOverviewAsync(query.ToRequest(), ct))));

        group.MapGet("/analytics/heatmap", async (
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileUsageAggregationService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileHeatmapBucketDto>>.Ok(await service.GetHeatmapAsync(query.ToRequest(), ct))));

        group.MapGet("/analytics/charts", async (
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileUsageAggregationService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileAnalyticsChartDto>>.Ok(await service.GetChartsAsync(query.ToRequest(), ct))));

        group.MapGet("/analytics/timeline-blocks", async (
            [AsParameters] MobileAnalyticsEndpointQuery query,
            [FromServices] MobileTimelineBlockService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileTimelineBlockPageDto>.Ok(await service.GetBlocksAsync(query.ToRequest(), ct))));

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
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileUsageGoalDto>.Ok(await service.SaveAsync(request, ct))));

        group.MapDelete("/analytics/goals/{goalId}", async (
            [FromRoute] string goalId,
            [FromServices] MobileUsageGoalService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<string>.Ok(await service.DeleteAsync(goalId, ct) ? goalId : string.Empty)));

        group.MapGet("/apps/catalog-overrides", async (
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileAppCatalogOverrideDto>>.Ok(await service.ListOverridesAsync(ct))));

        group.MapPut("/apps/catalog-overrides", async (
            [FromBody] MobileAppCatalogOverrideUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileAppCatalogOverrideDto>.Ok(await service.UpsertOverrideAsync(request, ct))));

        group.MapPut("/apps/catalog-overrides/{packageName}", async (
            [FromRoute] string packageName,
            [FromBody] MobileAppCatalogOverrideUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileAppCatalogOverrideDto>.Ok(await service.UpsertOverrideAsync(request with { PackageName = packageName }, ct))));

        group.MapDelete("/apps/catalog-overrides/{packageName}", async (
            [FromRoute] string packageName,
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<string>.Ok(await service.DeleteOverrideAsync(packageName, ct) ? packageName : string.Empty)));

        group.MapGet("/apps/category-rules", async (
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<MobileAppCategoryRuleDto>>.Ok(await service.ListCategoryRulesAsync(ct))));

        group.MapPost("/apps/category-rules", async (
            [FromBody] MobileAppCategoryRuleUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileAppCategoryRuleDto>.Ok(await service.CreateCategoryRuleAsync(request, ct))));

        group.MapPut("/apps/category-rules/{ruleId}", async (
            [FromRoute] string ruleId,
            [FromBody] MobileAppCategoryRuleUpsertRequest request,
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<MobileAppCategoryRuleDto>.Ok(await service.UpdateCategoryRuleAsync(ruleId, request, ct))));

        group.MapDelete("/apps/category-rules/{ruleId}", async (
            [FromRoute] string ruleId,
            [FromServices] MobileAppCatalogOverrideService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<string>.Ok(await service.DeleteCategoryRuleAsync(ruleId, ct) ? ruleId : string.Empty)));
    }

    public Task InitializeAsync(IServiceProvider serviceProvider) => Task.CompletedTask;

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
            PageSize);
}
