using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;

namespace Pim.Api.Endpoints;

public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations").RequireAuthorization();

        group.MapGet("/confirmations/pending", async (
            IOperationConfirmationService confirmations,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await confirmations.ListPendingForUserAsync(RequireCurrentUserId(currentUser), ct);
            return Results.Ok(ApiResponse<IReadOnlyList<OperationConfirmationDto>>.Ok(result));
        });

        group.MapGet("/confirmations/{id:guid}", async (
            Guid id,
            IOperationConfirmationService confirmations,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await confirmations.GetAsync(id, ct)
                ?? throw new DomainException(3001, "Confirmation record does not exist.");
            var userId = RequireCurrentUserId(currentUser);
            if (result.RequestedByUserId is { } requestedByUserId && requestedByUserId != userId)
            {
                throw new DomainException(3005, "Confirmation record is not assigned to the current user.");
            }

            return Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(result));
        });

        group.MapPost("/confirmations/{id:guid}/confirm", async (
            Guid id,
            IOperationConfirmationService confirmations,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await confirmations.ConfirmAsync(id, RequireCurrentUserId(currentUser), ct);
            return Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(result));
        });

        group.MapPost("/confirmations/{id:guid}/reject", async (
            Guid id,
            IOperationConfirmationService confirmations,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await confirmations.RejectAsync(id, RequireCurrentUserId(currentUser), ct);
            return Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(result));
        });
    }

    private static Guid RequireCurrentUserId(ICurrentUserService currentUser)
    {
        return currentUser.UserId ?? throw new DomainException(01002, "未登录");
    }
}
