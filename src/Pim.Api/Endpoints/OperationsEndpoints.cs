using Pim.Core.Common;
using Pim.Core.Operations;

namespace Pim.Api.Endpoints;

public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations").RequireAuthorization();

        group.MapGet("/confirmations/pending", async (
            IOperationConfirmationService confirmations,
            CancellationToken ct) =>
        {
            var result = await confirmations.ListPendingAsync(ct);
            return Results.Ok(ApiResponse<IReadOnlyList<OperationConfirmationDto>>.Ok(result));
        });

        group.MapPost("/confirmations/{id:guid}/confirm", async (
            Guid id,
            IOperationConfirmationService confirmations,
            CancellationToken ct) =>
        {
            var result = await confirmations.ConfirmAsync(id, null, ct);
            return Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(result));
        });

        group.MapPost("/confirmations/{id:guid}/reject", async (
            Guid id,
            IOperationConfirmationService confirmations,
            CancellationToken ct) =>
        {
            var result = await confirmations.RejectAsync(id, null, ct);
            return Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(result));
        });
    }
}
