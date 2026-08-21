using Microsoft.AspNetCore.Mvc;
using Pim.Api.Services;
using Pim.Core.Common;

namespace Pim.Api.Endpoints;

public static class OpsDbEndpoints
{
    public static void MapOpsDbEndpoints(this IEndpointRouteBuilder e)
    {
        var g = e.MapGroup("/api/v1/ops/db");
        g.MapGet("/tables", async (OpsDbService svc, CancellationToken ct) =>
        {
            var tables = await svc.ListTablesAsync(ct);
            return Results.Ok(ApiResponse<IReadOnlyList<TableInfo>>.Ok(tables));
        });
        g.MapGet("/describe", async (string table, OpsDbService svc, CancellationToken ct) =>
        {
            var cols = await svc.DescribeAsync(table, ct);
            return Results.Ok(ApiResponse<IReadOnlyList<ColumnInfo>>.Ok(cols));
        });
        g.MapPost("/query", async (OpsDbQueryRequest req, OpsDbService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var r = await svc.QueryAsync(req.Sql, req.Params, req.MaxRows, ct);
            if (r.Truncated) ctx.Response.Headers["X-Truncated"] = "true";
            return r.Truncated
                ? Results.Json(ApiResponse<OpsDbQueryResult>.Ok(r), statusCode: 206)
                : Results.Ok(ApiResponse<OpsDbQueryResult>.Ok(r));
        });
    }
}

public record OpsDbQueryRequest(string Sql, Dictionary<string, object?>? Params, int? MaxRows);
