using Pim.Api.Infrastructure.Ops;
using Pim.Api.Services;

namespace Pim.Api.Endpoints;

public static class OpsHealthEndpoints
{
    public static void MapOpsHealthEndpoints(this IEndpointRouteBuilder e)
    {
        var g = e.MapGroup("/api/v1/ops");
        g.MapGet("/health", async (IConfiguration cfg, OpsLogsService logsSvc, OpsDbService dbSvc, CancellationToken ct) =>
        {
            var validator = new OpsKeyValidator(cfg["PIM_OPS_KEY"] ?? cfg["Ops:Key"], cfg["PIM_OPS_ALLOWED_CIDRS"] ?? cfg["Ops:AllowedCidrs"]);
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

            return Results.Ok(new { opsEnabled, tablesCount, logFiles = logFilesCount });
        });
    }
}
