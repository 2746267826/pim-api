using System.Reflection;
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
        services.AddScoped<PcTrackerSchemaInitializer>();
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
            return Results.Ok(ApiResponse<string>.Ok("ok"));
        });

        writeGroup.MapPost("/keystats/samples", async (
            [FromBody] KeystatsSampleUploadRequest req,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            await svc.UpsertKeystatsSampleAsync(req, ct);
            return Results.Ok(ApiResponse<string>.Ok("ok"));
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
            var d = date is not null ? DateTime.Parse(date) : DateTime.Today;
            var result = await svc.GetSummaryAsync(d, ct);
            return Results.Ok(ApiResponse<PcSummaryResponse>.Ok(result));
        });

        readGroup.MapGet("/aw/timeline", async (
            [FromQuery] string? date,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var d = date is not null ? DateTime.Parse(date) : DateTime.Today;
            var result = await svc.GetTimelineAsync(d, ct);
            return Results.Ok(ApiResponse<List<TimelineItem>>.Ok(result));
        });

        readGroup.MapGet("/aw/heatmap", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var s = start is not null ? DateTime.Parse(start) : DateTime.Today.AddDays(-7);
            var e = end is not null ? DateTime.Parse(end) : DateTime.Today;
            var result = await svc.GetHeatmapAsync(s, e, ct);
            return Results.Ok(ApiResponse<List<HeatmapBucket>>.Ok(result));
        });

        readGroup.MapGet("/keystats/range", async (
            [FromQuery] string start,
            [FromQuery] string end,
            [FromServices] PcTrackerService svc,
            CancellationToken ct) =>
        {
            var s = DateTime.Parse(start);
            var e = DateTime.Parse(end);
            var result = await svc.GetHeatmapAsync(s, e, ct);
            return Results.Ok(ApiResponse<List<HeatmapBucket>>.Ok(result));
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
            [FromServices] PcTrackerService svc,
            CancellationToken ct,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20) =>
        {
            var q = new DetailQueryParams(dateFrom, dateTo, dimension, deviceId,
                appName, categoryName, keyName, eventType, sortBy, sortDir, page, pageSize);
            var result = await svc.QueryCompleteDetailAsync(q, ct);
            return Results.Ok(ApiResponse<TypedDetailQueryResponse>.Ok(result));
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
                ? Results.Ok(ApiResponse<string>.Ok("deleted"))
                : Results.NotFound(ApiResponse<string>.Error(404, "not found or builtin"));
        });

        readGroup.MapGet("/heatmap/grid", async (
            [FromQuery] string? start,
            [FromQuery] string? end,
            [FromServices] PcTrackerService svc,
            CancellationToken ct,
            [FromQuery] string dimension = "day") =>
        {
            var s = start is not null ? DateTime.Parse(start) : DateTime.Today.AddDays(-30);
            var e = end is not null ? DateTime.Parse(end) : DateTime.Today;
            var result = await svc.GetHeatmapGridAsync(s, e, dimension, ct);
            return Results.Ok(ApiResponse<HeatmapGridResponse>.Ok(result));
        });
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<PcTrackerSchemaInitializer>();
        await initializer.InitializeAsync();
    }
}
