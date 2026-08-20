using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Audit;

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

        group.MapPost("/confirmations/{id:guid}/confirm-second-level", async (
            Guid id,
            IOperationConfirmationService confirmations,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await confirmations.ConfirmSecondLevelAsync(id, RequireCurrentUserId(currentUser), ct);
            return Results.Ok(ApiResponse<OperationConfirmationDto>.Ok(result));
        });

        group.MapPost("/confirmations/{id:guid}/confirm-strict", async (
            Guid id,
            IOperationConfirmationService confirmations,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var result = await confirmations.ConfirmStrictAsync(id, RequireCurrentUserId(currentUser), ct);
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

        group.MapGet("/audit/{objectType}/{objectId:guid}", async (
            string objectType,
            Guid objectId,
            AuditVersionService audit,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = RequireCurrentUserId(currentUser);
            var result = await audit.GetTimelineAsync(objectType, objectId, userId, ct);
            return Results.Ok(ApiResponse<object>.Ok(result));
        });

        group.MapPost("/audit/{auditVersionId:guid}/restore-preview", async (
            Guid auditVersionId,
            AuditVersionService audit,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = RequireCurrentUserId(currentUser);
            var result = await audit.PreviewRestoreAsync(auditVersionId, userId, ct);
            return Results.Ok(ApiResponse<object>.Ok(result));
        });

        group.MapGet("/audit/export", async (
            DateTimeOffset? start,
            DateTimeOffset? end,
            AuditVersionService audit,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = RequireCurrentUserId(currentUser);
            var result = await audit.ExportAsync(
                start ?? DateTimeOffset.MinValue,
                end ?? DateTimeOffset.MaxValue,
                userId,
                ct);
            return Results.Ok(ApiResponse<object>.Ok(result));
        });
    }

    private static Guid RequireCurrentUserId(ICurrentUserService currentUser)
    {
        return currentUser.UserId ?? throw new DomainException(01002, "未登录");
    }
}
