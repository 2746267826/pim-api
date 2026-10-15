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
    LastAdminProtected,

    /// <summary>建号时用户名已被占用 → 409 / 01003。</summary>
    DuplicateUsername,

    /// <summary>建号时邮箱已被占用（按小写归一比较）→ 409 / 01004。</summary>
    DuplicateEmail
}

public record AdminUserActionResult(AdminUserActionStatus Status, AdminUserDto? User)
{
    public static AdminUserActionResult Success(AdminUserDto user) => new(AdminUserActionStatus.Ok, user);
    public static AdminUserActionResult Fail(AdminUserActionStatus status) => new(status, null);
}

/// <summary>
/// 管理员用户管理：列表、角色变更、启用/停用、建号、重置密码。
/// 硬性规则：系统必须始终保留至少一名有效管理员（禁止降级/停用最后一名管理员）。
/// </summary>
public interface IAdminUserService
{
    Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(CancellationToken ct);
    Task<AdminUserActionResult> ChangeRoleAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct);
    Task<AdminUserActionResult> SetActiveAsync(Guid actorUserId, Guid targetUserId, bool isActive, CancellationToken ct);

    /// <summary>
    /// 管理员建号（REQ-3）。入参已由端点按 40001–40008 校验；本方法负责角色枚举、
    /// 唯一性冲突与写库，并在成功后写审计。
    /// </summary>
    Task<AdminUserActionResult> CreateUserAsync(
        Guid actorUserId, string username, string email, string? displayName, string password, string role, CancellationToken ct);

    /// <summary>
    /// 管理员重置密码（REQ-4）。成功后目标用户**全部**未撤销会话失效（决策记录 §3.3）。
    /// </summary>
    Task<AdminUserActionResult> ResetPasswordAsync(
        Guid actorUserId, Guid targetUserId, string newPassword, CancellationToken ct);
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

        // 降级管理员时，必须至少保留另一名有效管理员。
        // 仅当目标本身是「有效管理员」时降级才会减少有效管理员数量；
        // 目标已是停用状态时不减少，因此不应以 LastAdminProtected 拒绝（与 SetActiveAsync 一致）。
        if (target.IsActive && target.Role == AdminBootstrap.AdminRole && newRole == AdminBootstrap.UserRole)
        {
            var otherActiveAdmins = await db.Users.CountAsync(
                u => u.Role == AdminBootstrap.AdminRole && u.IsActive && u.Id != targetUserId, ct);
            if (otherActiveAdmins == 0)
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

        // 停用管理员时，必须至少保留另一名有效管理员。
        // 上面的 `target.IsActive == isActive` 提前返回已保证此处 target.IsActive 为 true，
        // 因此无需再重复判断目标的有效状态。
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

    public async Task<AdminUserActionResult> CreateUserAsync(
        Guid actorUserId, string username, string email, string? displayName, string password, string role, CancellationToken ct)
    {
        if (role != AdminBootstrap.AdminRole && role != AdminBootstrap.UserRole)
            return AdminUserActionResult.Fail(AdminUserActionStatus.InvalidRole);

        // 邮箱落库前小写归一（与注册口径一致）
        var normalizedEmail = email.ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            return AdminUserActionResult.Fail(AdminUserActionStatus.DuplicateUsername);
        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct))
            return AdminUserActionResult.Fail(AdminUserActionStatus.DuplicateEmail);

        var user = new UserEntity
        {
            Username = username,
            Email = normalizedEmail,
            PasswordHash = PasswordHasher.Hash(password),
            // 显示名留空时回落为用户名（与注册一致）
            DisplayName = string.IsNullOrEmpty(displayName) ? username : displayName,
            Role = role,
            IsActive = true
        };

        db.Users.Add(user);
        try
        {
            // 建号行与审计行由**同一次** SaveChanges 落库：审计只入队，落库由这里显式完成，
            // 因此二者要么都成功、要么都不留痕（EF 对多条改动自动包一个事务）。
            //
            // 不用显式事务：生产装配开了 EnableRetryOnFailure，NpgsqlRetryingExecutionStrategy
            // 明确拒绝「用户自己发起的事务」——事务内第一条命令就抛 InvalidOperationException
            //（本仓 DeviceManagementService / MobileAnalyticsMaterializationService 有同款注释）。
            EnqueueAudit(actorUserId, "admin.user.create", user,
                new Dictionary<string, string> { ["role"] = role });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
        {
            // 并发下唯一索引兜底：先到的请求赢，本次按冲突返回。
            // 上面两次 AnyAsync 之间仍可能插入，只有数据库是最终裁判。
            var isEmailConflict = pg.ConstraintName?.Contains("email", StringComparison.OrdinalIgnoreCase) == true;
            return AdminUserActionResult.Fail(
                isEmailConflict ? AdminUserActionStatus.DuplicateEmail : AdminUserActionStatus.DuplicateUsername);
        }

        return AdminUserActionResult.Success(ToDto(user));
    }

    public Task<AdminUserActionResult> ResetPasswordAsync(
        Guid actorUserId, Guid targetUserId, string newPassword, CancellationToken ct)
        // 重置要让目标用户全部会话失效，必须与 refresh 的「轮换」、login 的「发会话」互斥：
        // 否则并发请求可能在撤销提交之后插入一条从未被撤销的新令牌（REQ-6 落空）。
        => UserSessionLock.RunAsync(
            db, targetUserId, () => ResetPasswordCoreAsync(actorUserId, targetUserId, newPassword, ct), ct);

    private async Task<AdminUserActionResult> ResetPasswordCoreAsync(
        Guid actorUserId, Guid targetUserId, string newPassword, CancellationToken ct)
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (target is null)
            return AdminUserActionResult.Fail(AdminUserActionStatus.NotFound);

        var now = DateTimeOffset.UtcNow;
        target.PasswordHash = PasswordHasher.Hash(newPassword);
        target.UpdatedAt = now;

        // 重置没有「当前设备」概念：该用户全部未撤销会话失效（决策记录 §3.3）
        await RefreshTokenSessions.RevokeForUserAsync(db, target.Id, keepTokenHash: null, now, ct);

        // 密码变更 + 会话撤销 + 审计由同一次 SaveChanges 落库（理由同 CreateUserAsync）：
        // 审计只入队、不自行保存，失败时整体回滚，不会留下
        // 「密码已改、旧设备还在线、审计缺失」的中间状态。
        EnqueueAudit(actorUserId, "admin.user.reset_password", target,
            new Dictionary<string, string>());
        await db.SaveChangesAsync(ct);

        return AdminUserActionResult.Success(ToDto(target));
    }

    private static CreateAuditLogRequest BuildAuditRequest(
        Guid actorUserId, string action, UserEntity target, IReadOnlyDictionary<string, string> metadata)
        => new(
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
            ErrorMessage: null);

    private async Task AuditAsync(Guid actorUserId, string action, UserEntity target,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        await auditLog.RecordAsync(BuildAuditRequest(actorUserId, action, target, metadata), ct);
    }

    /// <summary>
    /// 只把审计行入队（不落库），由调用方随业务改动**一次**提交。
    /// 用于「业务与审计必须原子」的新流程；既有流程继续用 <see cref="AuditAsync"/>（自行保存），
    /// 行为不变。
    /// </summary>
    private void EnqueueAudit(Guid actorUserId, string action, UserEntity target,
        IReadOnlyDictionary<string, string> metadata)
    {
        auditLog.Enqueue(BuildAuditRequest(actorUserId, action, target, metadata));
    }

    private static AdminUserDto ToDto(UserEntity u)
        => new(u.Id, u.Username, u.Email, u.DisplayName, u.Role, u.IsActive, u.CreatedAt);
}
