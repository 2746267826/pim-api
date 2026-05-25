using Pim.Core.Common;
using Pim.Core.Operations;

namespace Pim.Api.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/status").RequireAuthorization();

        group.MapGet("/summary", async (
            ISystemStatusService status,
            CancellationToken ct) =>
        {
            var result = await status.GetSummaryAsync(ct);
            return Results.Ok(ApiResponse<SystemStatusSummaryDto>.Ok(result));
        });

        group.MapGet("/", async (
            ISystemStatusService status,
            CancellationToken ct) =>
        {
            var result = await status.GetDetailAsync(ct);
            return Results.Ok(ApiResponse<SystemStatusDetailDto>.Ok(result));
        });
    }
}
