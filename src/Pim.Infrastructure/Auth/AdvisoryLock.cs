using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

/// <summary>
/// 按 bigint 键取 PostgreSQL **事务级 advisory lock** 并在锁内执行操作的通用原语。
///
/// <para>
/// 用途：把「读后写」的临界区串行化。目前两处使用：
/// 每用户的会话操作（<see cref="UserSessionLock"/>）与全新部署的首注册引导
/// （<see cref="BootstrapRegistrationKey"/>）。
/// </para>
/// <para>
/// 本仓已知约束：生产装配 <c>EnableRetryOnFailure(3)</c>，
/// <c>NpgsqlRetryingExecutionStrategy</c> 拒绝用户发起的事务，因此自管事务必须交给
/// <c>CreateExecutionStrategy()</c> 作为可重试单元执行，并在每次尝试开始时清空变更跟踪
/// （与 <c>DeviceManagementService</c> / <c>MobileAnalyticsMaterializationService</c> 同款注释）。
/// </para>
/// </summary>
public static class AdvisoryLock
{
    /// <summary>
    /// 「物理空库首注册」用的固定键。
    ///
    /// <para>
    /// 只有当 <c>users</c> 表为空（全新部署引导）时，注册才会取这把全局锁：
    /// 两个并发匿名首注册会同时看到空库，不串行化的话第二个仍会建号（核心验收口径
    /// 「库非空时未登录第三方无法通过公开端点创建账号」就被绕过了）。
    /// </para>
    /// <para>
    /// 与用户键（<see cref="UserSessionLock.KeyFor"/>）碰撞的概率约 2⁻⁶⁴，且即便碰撞
    /// 也只会多一次串行化、不会串号。
    /// </para>
    /// </summary>
    public const long BootstrapRegistrationKey = 0x50494D5F424F4F54L; // "PIM_BOOT"

    public static async Task<T> RunAsync<T>(
        PimDbContext db, long key, Func<Task<T>> operation, CancellationToken ct)
    {
        // 只有 PostgreSQL 有 advisory lock；其它关系型提供程序（例如 SQLite）直接执行，
        // 避免把 pg_advisory_xact_lock 发过去（同 MobileAnalyticsMaterializationService 的判定）。
        if (!db.Database.IsRelational() ||
            db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return await operation();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // 策略可能整体重跑：清掉上一次尝试残留的跟踪实体，否则重试会重复插入
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})", new object[] { key }, ct);

            var result = await operation();

            await transaction.CommitAsync(ct);
            return result;
        });
    }
}
