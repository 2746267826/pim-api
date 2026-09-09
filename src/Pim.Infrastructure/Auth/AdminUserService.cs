using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Auth;

/// <summary>管理员视角的用户信息。</summary>
public record AdminUserDto(
    Guid Id,
    string Username,
    string Email,
    string? DisplayName,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt);

public enum AdminUserActionStatus
{
    Ok,
    NotFound,
    InvalidRole,
    LastAdminProtected
}

public record AdminUserActionResult(AdminUserActionStatus Status, AdminUserDto? User)
{
    public static AdminUserActionResult Success(AdminUserDto user) => new(AdminUserActionStatus.Ok, user);
    public static AdminUserActionResult Fail(AdminUserActionStatus status) => new(status, null);
}

/// <summary>
/// 管理员用户管理：列表、角色变更、启用/停用。
/// 硬性规则：系统必须始终保留至少一名有效管理员（禁止降级/停用最后一名管理员）。
/// </summary>
public interface IAdminUserService
{
    Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(CancellationToken ct);
    Task<AdminUserActionResult> ChangeRoleAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct);
    Task<AdminUserActionResult> SetActiveAsync(Guid actorUserId, Guid targetUserId, bool isActive, CancellationToken ct);
}

public sealed class AdminUserService(PimDbContext db, IAuditLogService auditLog) : IAdminUserService
{
    private const string AuditSource = "admin";

    public async Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(CancellationToken ct)
        => await db.Users
            .OrderBy(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Select(u => new AdminUserDto(u.Id, u.Username, u.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt))
            .ToListAsync(ct);

    public async Task<AdminUserActionResult> ChangeRoleAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct)
    {
        if (newRole != AdminBootstrap.AdminRole && newRole != AdminBootstrap.UserRole)
            return AdminUserActionResult.Fail(AdminUserActionStatus.InvalidRole);

        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (target is null)
            return AdminUserActionResult.Fail(AdminUserActionStatus.NotFound);

        if (target.Role == newRole)
            return AdminUserActionResult.Success(ToDto(target));

        // 降级管理员时，必须至少保留另一名管理员
        if (target.Role == AdminBootstrap.AdminRole && newRole == AdminBootstrap.UserRole)
        {
            var otherAdmins = await db.Users.CountAsync(
                u => u.Role == AdminBootstrap.AdminRole && u.Id != targetUserId, ct);
            if (otherAdmins == 0)
                return AdminUserActionResult.Fail(AdminUserActionStatus.LastAdminProtected);
        }

        var oldRole = target.Role;
        target.Role = newRole;
        target.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await AuditAsync(actorUserId, "admin.user.change_role", target,
            new Dictionary<string, string> { ["from"] = oldRole, ["to"] = newRole }, ct);

        return AdminUserActionResult.Success(ToDto(target));
    }

    public async Task<AdminUserActionResult> SetActiveAsync(Guid actorUserId, Guid targetUserId, bool isActive, CancellationToken ct)
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (target is null)
            return AdminUserActionResult.Fail(AdminUserActionStatus.NotFound);

        if (target.IsActive == isActive)
            return AdminUserActionResult.Success(ToDto(target));

        // 停用管理员时，必须至少保留另一名有效管理员
        if (!isActive && target.Role == AdminBootstrap.AdminRole)
        {
            var otherActiveAdmins = await db.Users.CountAsync(
                u => u.Role == AdminBootstrap.AdminRole && u.IsActive && u.Id != targetUserId, ct);
            if (otherActiveAdmins == 0)
                return AdminUserActionResult.Fail(AdminUserActionStatus.LastAdminProtected);
        }

        target.IsActive = isActive;
        target.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await AuditAsync(actorUserId, isActive ? "admin.user.enable" : "admin.user.disable", target,
            new Dictionary<string, string> { ["isActive"] = isActive.ToString() }, ct);

        return AdminUserActionResult.Success(ToDto(target));
    }

    private async Task AuditAsync(Guid actorUserId, string action, UserEntity target,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        await auditLog.RecordAsync(new CreateAuditLogRequest(
            UserId: actorUserId,
            ActorType: AuditActorType.User,
            Action: action,
            ResourceType: "user",
            ResourceId: target.Id.ToString(),
            Source: AuditSource,
            Result: AuditResult.Success,
            IpAddress: null,
            UserAgent: null,
            CorrelationId: null,
            Metadata: metadata,
            ErrorCode: null,
            ErrorMessage: null), ct);
    }

    private static AdminUserDto ToDto(UserEntity u)
        => new(u.Id, u.Username, u.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt);
}
