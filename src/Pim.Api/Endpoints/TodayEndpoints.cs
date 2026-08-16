using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pim.Api.Today;
using Pim.Core.Caching;
using Pim.Core.Common;
using Pim.Core.Today;

namespace Pim.Api.Endpoints;

public static class TodayEndpointPaths
{
    public const string Sections = "/api/v1/today/sections";

    public static string Section(string sectionId)
        => $"{Sections}/{Uri.EscapeDataString(sectionId)}";
}

public static class TodayEndpoints
{
    public static void MapTodayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/today").RequireAuthorization();

        group.MapGet("/sections", async (
            string? date,
            TodaySectionService today,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            return await RunWithDateValidationAsync(async () =>
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request),
                    force,
                    () => today.GetRegistryAsync(date, ct),
                    ct);
                return Results.Ok(ApiResponse<TodaySectionRegistryDto>.Ok(result));
            });
        });

        group.MapGet("/sections/{sectionId}", async (
            string sectionId,
            string? date,
            TodaySectionService today,
            [FromServices] IAggregateResultCache cache,
            HttpContext httpContext,
            [FromQuery] bool force = false,
            CancellationToken ct = default) =>
        {
            return await RunWithDateValidationAsync(async () =>
            {
                var result = await cache.GetOrCreateAsync(
                    AggregateResultCacheKeys.Build(httpContext.Request),
                    force,
                    () => today.GetSectionAsync(sectionId, date, ct),
                    ct);
                return result is null
                    ? Results.NotFound(ApiResponse<string>.Error(404, "今日模块不存在。"))
                    : Results.Ok(ApiResponse<TodaySectionDto>.Ok(result));
            });
        });
    }

    public static IResult ToInvalidDateResult()
        => Results.BadRequest(ApiResponse<string>.Error(
            400,
            "Invalid Today date. Expected YYYY-MM-DD or a parseable date/time value."));

    private static async Task<IResult> RunWithDateValidationAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (FormatException)
        {
            return ToInvalidDateResult();
        }
    }
}
