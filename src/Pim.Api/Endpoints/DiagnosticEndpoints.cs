using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Pim.Api.Services;
using Pim.Core.Common;
using Pim.Module.Mobile.Diagnostics;
using Pim.Module.Mobile.DTOs;

namespace Pim.Api.Endpoints;

public static class DiagnosticEndpoints
{
    public static void MapDiagnosticEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/diagnostics")
            .RequireAuthorization();

        group.MapPost("/events", async (
            HttpContext httpContext,
            [FromBody] DiagnosticEventUploadRequest request,
            [FromServices] DiagnosticEventService service,
            CancellationToken ct) =>
        {
            try
            {
                var result = await service.UploadAsync(request, ct);
                return Results.Ok(ApiResponse<DiagnosticUploadResult>.Ok(result));
            }
            catch (DiagnosticQuotaExceededException ex)
            {
                httpContext.Response.Headers.RetryAfter =
                    ex.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return Results.Json(
                    ApiResponse<string>.Error(42901, ex.Message),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        })
        .Produces<ApiResponse<DiagnosticUploadResult>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces(401)
        .Produces<ApiResponse<string>>(413)
        .Produces<ApiResponse<string>>(429)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/overview", async (
            [FromQuery] string? deviceId,
            [FromQuery] string? platform,
            [FromQuery] int? limit,
            [FromServices] DiagnosticEventService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<DiagnosticOverviewDto>.Ok(
                await service.GetOverviewAsync(deviceId, platform, limit, ct))))
        .Produces<ApiResponse<DiagnosticOverviewDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/export", async (
            [FromQuery] string? deviceId,
            [FromQuery] string? platform,
            [FromQuery] int? days,
            [FromServices] DiagnosticExportService service,
            CancellationToken ct) =>
        {
            var bundle = await service.BuildAsync(deviceId, platform, days ?? 7, ct);
            return Results.File(bundle.Content, "application/zip", bundle.FileName);
        })
        .Produces<byte[]>(200, "application/zip")
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(413)
        .Produces<ApiResponse<string>>(500);
    }
}
