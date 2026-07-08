using Pim.Core.Common;
using Pim.Core.Endpoints;
using Pim.Infrastructure.Endpoints;

namespace Pim.Api.Endpoints;

public static class EndpointEndpoints
{
    public static void MapEndpointEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/endpoints").RequireAuthorization();

        group.MapGet("", async (
            EndpointStatusService endpointStatus,
            CancellationToken ct) =>
        {
            var result = await endpointStatus.ListAsync(ct);
            return Results.Ok(ApiResponse<IReadOnlyList<EndpointStatusDto>>.Ok(result));
        });

        group.MapPost("/{deviceId}/heartbeat", async (
            string deviceId,
            EndpointHeartbeatRequest request,
            EndpointStatusService endpointStatus,
            CancellationToken ct) =>
        {
            var result = await endpointStatus.UpsertHeartbeatAsync(deviceId, request, ct);
            return Results.Ok(ApiResponse<EndpointStatusDto>.Ok(result));
        });

        group.MapGet("/{deviceId}/collection-quality", async (
            string deviceId,
            EndpointStatusService endpointStatus,
            CancellationToken ct) =>
        {
            var result = await endpointStatus.GetCollectionQualityAsync(deviceId, ct);
            return Results.Ok(ApiResponse<EndpointCollectionQualityDto>.Ok(result));
        });

        group.MapPost("/{deviceId}/notification-actions", async (
            string deviceId,
            EndpointNotificationActionRequest request,
            EndpointStatusService endpointStatus,
            CancellationToken ct) =>
        {
            var result = await endpointStatus.HandleNotificationActionAsync(deviceId, request, ct);
            return Results.Ok(ApiResponse<EndpointNotificationActionResponse>.Ok(result));
        });
    }
}
