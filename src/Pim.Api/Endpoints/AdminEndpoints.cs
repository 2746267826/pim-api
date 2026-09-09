using Microsoft.AspNetCore.Authorization;
using Pim.Api.DTOs;
using Pim.Core.Common;
using Pim.Infrastructure.Auth;

namespace Pim.Api.Endpoints;

/// <summary>
/// 管理员端点：用户列表、角色变更、启用/停用。全部仅 admin 可访问。
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin")
            .RequireAuthorization(new AuthorizeAttribute { Roles = AdminBootstrap.AdminRole });

        group.MapGet("/users", async (IAdminUserService svc, CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<AdminUserDto>>.Ok(await svc.ListUsersAsync(ct))));

        group.MapPost("/users/{id:guid}/role", async (
            Guid id,
            UpdateUserRoleRequest request,
            ICurrentUserService currentUser,
            IAdminUserService svc,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not Guid actorId)
                return Results.Unauthorized();

            var result = await svc.ChangeRoleAsync(actorId, id, request.Role?.Trim() ?? string.Empty, ct);
            IResult response = result.Status switch
            {
                AdminUserActionStatus.Ok => Results.Ok(ApiResponse<AdminUserDto>.Ok(result.User!)),
                AdminUserActionStatus.NotFound => Results.NotFound(ApiResponse<string>.Error(40040, "用户不存在")),
                AdminUserActionStatus.InvalidRole => Results.BadRequest(ApiResponse<string>.Error(40041, "无效的角色，仅支持 admin / user")),
                AdminUserActionStatus.LastAdminProtected => Results.BadRequest(ApiResponse<string>.Error(40042, "至少需要保留一名管理员")),
                _ => Results.BadRequest(ApiResponse<string>.Error(40043, "操作失败"))
            };
            return response;
        });

        group.MapPost("/users/{id:guid}/status", async (
            Guid id,
            UpdateUserStatusRequest request,
            ICurrentUserService currentUser,
            IAdminUserService svc,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not Guid actorId)
                return Results.Unauthorized();

            var result = await svc.SetActiveAsync(actorId, id, request.IsActive, ct);
            IResult response = result.Status switch
            {
                AdminUserActionStatus.Ok => Results.Ok(ApiResponse<AdminUserDto>.Ok(result.User!)),
                AdminUserActionStatus.NotFound => Results.NotFound(ApiResponse<string>.Error(40040, "用户不存在")),
                AdminUserActionStatus.LastAdminProtected => Results.BadRequest(ApiResponse<string>.Error(40042, "至少需要保留一名有效管理员")),
                _ => Results.BadRequest(ApiResponse<string>.Error(40043, "操作失败"))
            };
            return response;
        });
    }
}
