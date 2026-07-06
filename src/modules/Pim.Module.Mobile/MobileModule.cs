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
