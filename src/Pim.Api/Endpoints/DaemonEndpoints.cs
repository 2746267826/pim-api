using Pim.Core.Common;
using Pim.Core.Operations;

namespace Pim.Api.Endpoints;

public static class DaemonEndpoints
{
    public static void MapDaemonEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/daemon").RequireAuthorization();

        group.MapPost("/heartbeat", async (
            DaemonHeartbeatRequest request,
            IDaemonHeartbeatService heartbeats,
            CancellationToken ct) =>
        {
            var result = await heartbeats.UpsertAsync(request, ct);
            return Results.Ok(ApiResponse<DaemonHeartbeatDto>.Ok(result));
        });
    }
}
