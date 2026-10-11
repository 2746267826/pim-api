using Pim.Api.Infrastructure.Ops;
using Pim.Api.Services;
using Pim.Core.Common;

namespace Pim.Api.Endpoints;

public static class OpsHealthEndpoints
{
    public static void MapOpsHealthEndpoints(this IEndpointRouteBuilder e)
    {
        var g = e.MapGroup("/api/v1/ops");
        g.MapGet("/health", async (IConfiguration cfg, OpsLogsService logsSvc, OpsDbService dbSvc, CancellationToken ct) =>
        {
            var validator = new OpsKeyValidator(cfg["PIM_OPS_KEY"] ?? cfg["Ops:Key"]);
            var opsEnabled = validator.HasKeys;

            int tablesCount = 0;
            int logFilesCount = 0;
            try
            {
                var tables = await dbSvc.ListTablesAsync(ct);
                tablesCount = tables.Count;
            }
            catch { /* ops health should not fail when db unavailable */ }

            try
            {
                var files = await logsSvc.ListFilesAsync(ct);
                logFilesCount = files.Count;
            }
            catch { }

            return Results.Ok(new OpsHealthResponse(opsEnabled, tablesCount, logFilesCount));
        })
        .Produces<OpsHealthResponse>(200)
        .Produces(401)
        .Produces(403)
        .Produces(429)
        .Produces(503)
        .Produces<ApiResponse<string>>(500);
    }
}

/// <summary>契约注解用：ops 健康摘要（与原匿名对象 JSON 等价：camelCase 属性、同字段同序）。</summary>
public sealed record OpsHealthResponse(bool OpsEnabled, int TablesCount, int LogFiles);
