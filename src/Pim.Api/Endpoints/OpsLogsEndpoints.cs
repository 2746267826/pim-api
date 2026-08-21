using Microsoft.AspNetCore.Mvc;
using Pim.Api.Services;
using Pim.Core.Common;

namespace Pim.Api.Endpoints;

public static class OpsLogsEndpoints
{
    public static void MapOpsLogsEndpoints(this IEndpointRouteBuilder e)
    {
        var g = e.MapGroup("/api/v1/ops/logs");
        g.MapGet("/files", async (OpsLogsService svc, CancellationToken ct) =>
        {
            var files = await svc.ListFilesAsync(ct);
            return Results.Ok(ApiResponse<IReadOnlyList<LogFileInfo>>.Ok(files));
        });
        g.MapGet("/tail", async (string file, int? lines, string? level, string? keyword, OpsLogsService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var r = await svc.TailAsync(file, lines ?? 50, level, keyword, ct);
            if (r.Truncated) ctx.Response.Headers["X-Truncated"] = "true";
            return r.Truncated
                ? Results.Json(ApiResponse<OpsLogsResult>.Ok(r), statusCode: 206)
                : Results.Ok(ApiResponse<OpsLogsResult>.Ok(r));
        });
        g.MapGet("/query", async ([AsParameters] OpsLogsQuery q, OpsLogsService svc, HttpContext ctx, CancellationToken ct) =>
        {
            // default limit if not provided
            if (q.Limit == 0) q.Limit = 50;
            var r = await svc.QueryAsync(q, ct);
            if (r.Truncated) ctx.Response.Headers["X-Truncated"] = "true";
            // add X-Truncated header even when truncated; status 206 when truncated
            if (r.Truncated) return Results.Json(ApiResponse<OpsLogsResult>.Ok(r), statusCode: 206);
            return Results.Ok(ApiResponse<OpsLogsResult>.Ok(r));
        });
        g.MapGet("/health", () => Results.Ok(new { opsEnabled = true }));
    }
}
