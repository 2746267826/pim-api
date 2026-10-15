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
            Results.Ok(ApiResponse<IReadOnlyList<AdminUserDto>>.Ok(await svc.ListUsersAsync(ct))))
        .Produces<ApiResponse<IReadOnlyList<AdminUserDto>>>(200)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(500);

        // REQ-3：管理员建号（公开注册收敛后的唯一建号入口）
        group.MapPost("/users", async (
            CreateUserRequest request,
            ICurrentUserService currentUser,
            IAdminUserService svc,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not Guid actorId)
                return Results.Unauthorized();

            var username = request.Username?.Trim() ?? string.Empty;
            var email = request.Email?.Trim() ?? string.Empty;
            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrEmpty(displayName))
                displayName = username;

            // 与公开注册同一份校验实现（40001–40008）
            if (UserCredentialsValidator.Validate(username, email, request.Password, displayName) is { } error)
                return Results.BadRequest(ApiResponse<string>.Error(error.Code, error.Message));

            // role 缺省或为空 → user（不默认创建管理员）
            var role = string.IsNullOrWhiteSpace(request.Role) ? AdminBootstrap.UserRole : request.Role.Trim();

            var result = await svc.CreateUserAsync(
                actorId, username, email, displayName, request.Password, role, ct);

            IResult response = result.Status switch
            {
                AdminUserActionStatus.Ok => Results.Created(
                    $"/api/v1/admin/users/{result.User!.Id}",
                    ApiResponse<AdminUserDto>.Ok(result.User!)),
                AdminUserActionStatus.DuplicateUsername => Results.Conflict(ApiResponse<string>.Error(01003, "用户名已存在")),
                AdminUserActionStatus.DuplicateEmail => Results.Conflict(ApiResponse<string>.Error(01004, "邮箱已存在")),
                AdminUserActionStatus.InvalidRole => Results.BadRequest(ApiResponse<string>.Error(40041, "无效的角色，仅支持 admin / user")),
                _ => Results.BadRequest(ApiResponse<string>.Error(40043, "操作失败"))
            };
            return response;
        })
        .Produces<ApiResponse<AdminUserDto>>(201)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        // REQ-4：管理员重置任意用户密码（该用户全部会话失效）
        group.MapPost("/users/{id:guid}/password", async (
            Guid id,
            ResetPasswordRequest request,
            ICurrentUserService currentUser,
            IAdminUserService svc,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not Guid actorId)
                return Results.Unauthorized();

            if (UserCredentialsValidator.ValidatePassword(request.NewPassword) is { } error)
                return Results.BadRequest(ApiResponse<string>.Error(error.Code, error.Message));

            var result = await svc.ResetPasswordAsync(actorId, id, request.NewPassword, ct);

            IResult response = result.Status switch
            {
                AdminUserActionStatus.Ok => Results.Ok(ApiResponse<AdminUserDto>.Ok(result.User!)),
                AdminUserActionStatus.NotFound => Results.NotFound(ApiResponse<string>.Error(40040, "用户不存在")),
                _ => Results.BadRequest(ApiResponse<string>.Error(40043, "操作失败"))
            };
            return response;
        })
        .Produces<ApiResponse<AdminUserDto>>(200)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

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
        })
        .Produces<ApiResponse<AdminUserDto>>(200)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

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
        })
        .Produces<ApiResponse<AdminUserDto>>(200)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);
    }
}
