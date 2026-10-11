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
        })
        .Produces<ApiResponse<DaemonHeartbeatDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        group.MapPost("/planned-offline", async (
            PlannedOfflineRequest request,
            IDaemonHeartbeatService heartbeats,
            CancellationToken ct) =>
        {
            var result = await heartbeats.RecordPlannedOfflineAsync(request, ct);
            return Results.Ok(ApiResponse<DaemonHeartbeatDto?>.Ok(result));
        })
        .Produces<ApiResponse<DaemonHeartbeatDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/heartbeats", async (
            IDaemonHeartbeatService heartbeats,
            CancellationToken ct) =>
        {
            var result = await heartbeats.ListAsync(ct);
            return Results.Ok(ApiResponse<IReadOnlyList<DaemonHeartbeatDto>>.Ok(result));
        })
        .Produces<ApiResponse<IReadOnlyList<DaemonHeartbeatDto>>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);
    }
}
