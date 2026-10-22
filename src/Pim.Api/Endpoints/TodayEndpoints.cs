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
                // 键与查数共用同一个时钟读数：跨 04:00 的瞬间不会把新业务日写进旧键（review P3-6）。
                var nowUtc = DateTimeOffset.UtcNow;
                var result = await cache.GetOrCreateAsync(
                    BuildCacheKey(httpContext.Request, date, nowUtc),
                    force,
                    () => today.GetRegistryAsync(date, nowUtc, ct),
                    ct);
                return Results.Ok(ApiResponse<TodaySectionRegistryDto>.Ok(result));
            });
        })
        .Produces<ApiResponse<TodaySectionRegistryDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

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
                var nowUtc = DateTimeOffset.UtcNow;
                var result = await cache.GetOrCreateAsync(
                    BuildCacheKey(httpContext.Request, date, nowUtc),
                    force,
                    () => today.GetSectionAsync(sectionId, date, nowUtc, ct),
                    ct);
                return result is null
                    ? Results.NotFound(ApiResponse<string>.Error(404, "今日模块不存在。"))
                    : Results.Ok(ApiResponse<TodaySectionDto>.Ok(result));
            });
        })
        .Produces<ApiResponse<TodaySectionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);
    }

    /// <summary>
    /// 端点实际使用的缓存键：不带 date 时把服务端算出的业务日放进键（带 date 时键里本来就有 date）。
    /// 否则 03:59 的快照会在 04:00 之后继续被当作「今天」返回（review P2-5）。
    /// 抽成可传时钟的纯函数，测试才能覆盖「端点真的用了它」这条接线（review P3-2）。
    /// </summary>
    internal static string BuildCacheKey(HttpRequest request, string? date, DateTimeOffset nowUtc)
        => AggregateResultCacheKeys.Build(request, overrides: ImplicitDateOverrides(date, nowUtc));

    private static IReadOnlyList<KeyValuePair<string, string>>? ImplicitDateOverrides(
        string? date, DateTimeOffset nowUtc)
        => string.IsNullOrWhiteSpace(date)
            ? [new("implicitDate", TodaySectionService.ImplicitDateCacheKey(nowUtc))]
            : null;

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
