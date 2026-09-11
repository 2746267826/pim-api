using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

/// <summary>
/// 管理员引导：保证系统始终存在至少一名管理员。
/// - 新部署：users 表为空时，首个注册用户自动成为管理员；
/// - 已部署系统升级：启动时若无任何管理员，自动将最早注册且仍然有效的用户提升为管理员（幂等）。
/// </summary>
public static class AdminBootstrap
{
    public const string AdminRole = "admin";
    public const string UserRole = "user";

    /// <summary>注册时的角色判定：users 表为空则首个用户为管理员，否则为普通用户。</summary>
    public static async Task<string> DetermineRegistrationRoleAsync(PimDbContext db, CancellationToken ct)
        => await db.Users.AnyAsync(ct) ? UserRole : AdminRole;

    /// <summary>
    /// 并发首注册保护：当前请求把首个用户置为管理员后调用。
    /// 若存在更早创建的管理员（并发竞争中先到者），返回 false，调用方应把当前用户降级为普通用户。
    /// </summary>
    public static async Task<bool> ConfirmFirstAdminAsync(PimDbContext db, Guid userId, DateTimeOffset userCreatedAt, CancellationToken ct)
    {
        var olderAdminExists = await db.Users.AnyAsync(
            u => u.Role == AdminRole && u.Id != userId && (u.CreatedAt < userCreatedAt || (u.CreatedAt == userCreatedAt && u.Id.CompareTo(userId) < 0)), ct);
        return !olderAdminExists;
    }

    /// <summary>
    /// 启动引导（幂等）：无管理员时提升最早注册的活跃用户。
    /// 返回被提升的用户 Id；无需处理（已有活跃管理员或无活跃用户）时返回 null。
    /// </summary>
    public static async Task<Guid?> EnsureAdminExistsAsync(PimDbContext db, CancellationToken ct)
    {
        var hasActiveAdmin = await db.Users.AnyAsync(u => u.Role == AdminRole && u.IsActive, ct);
        if (hasActiveAdmin) return null;

        var earliest = await db.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .FirstOrDefaultAsync(ct);
        if (earliest is null) return null;

        earliest.Role = AdminRole;
        earliest.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return earliest.Id;
    }
}
