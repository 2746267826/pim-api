using Microsoft.AspNetCore.Mvc;
using Pim.Api.DTOs;
using Pim.Api.Services;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;

namespace Pim.Api.Endpoints;

public static class RecycleBinEndpoints
{
    public static IEndpointRouteBuilder MapUnifiedRecycleBinEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/recycle-bin")
            .RequireAuthorization()
            .WithTags("Recycle bin");

        group.MapGet("", async (
            [FromQuery] string? domain,
            [FromQuery] string? search,
            [FromQuery] DateTimeOffset? deletedFrom,
            [FromQuery] DateTimeOffset? deletedTo,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            UnifiedRecycleBinService service,
            ICurrentUserService user,
            CancellationToken ct) =>
        {
            _ = UserId(user);
            return Results.Ok(ApiResponse<RecycleBinPageDto>.Ok(
                await service.ListAsync(
                    domain,
                    search,
                    deletedFrom,
                    deletedTo,
                    page ?? 1,
                    pageSize ?? 50,
                    ct)));
        })
        .Produces<ApiResponse<RecycleBinPageDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400);

        group.MapPost("/restore", async (
            RecycleBinBatchRequest request,
            UnifiedRecycleBinService service,
            ICurrentUserService user,
            CancellationToken ct) =>
        {
            _ = UserId(user);
            return Results.Ok(ApiResponse<RecycleBinBatchReceiptDto>.Ok(
                await service.RestoreAsync(request, ct)));
        })
        .Produces<ApiResponse<RecycleBinBatchReceiptDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400);

        group.MapPost("/purge", async (
            RecycleBinBatchRequest request,
            UnifiedRecycleBinService service,
            ICurrentUserService user,
            CancellationToken ct) =>
        {
            _ = UserId(user);
            return Results.Ok(ApiResponse<RecycleBinBatchReceiptDto>.Ok(
                await service.PurgeAsync(request, ct)));
        })
        .Produces<ApiResponse<RecycleBinBatchReceiptDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400);

        return endpoints;
    }

    private static Guid UserId(ICurrentUserService user) =>
        user.UserId ?? throw new DomainException(40101, "请先登录");
}
