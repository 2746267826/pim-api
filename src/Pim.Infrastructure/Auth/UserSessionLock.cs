using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

/// <summary>
/// 按用户把「会话相关写操作」串行化的原语。
///
/// <para>
/// 要解决的问题：<c>POST /auth/refresh</c> 是「先读有效令牌 → 撤销旧的 → 插入新的」两段式。
/// 若在它的「读」与「写」之间，改密 / 管理员重置把该用户全部会话撤销并**提交**，
/// refresh 仍会插入一条**从未被撤销**的新令牌——被踢的设备因此保留登录态，
/// REQ-6「只踢其它设备 / 重置后全部失效」的安全承诺落空。
/// </para>
/// <para>
/// 登录同理：用旧口令校验通过后、插入会话之前若发生重置，这条新会话会逃过撤销。
/// </para>
/// <para>
/// 做法：同一用户的 refresh / 登录 / 改密 / 重置都在同一把事务级 advisory lock 内执行，
/// 并在锁内**重读**要判定的状态，从而消除「读后写」窗口。锁键由 userId 稳定派生，
/// 全程只取一把锁，因此不存在加锁顺序造成的死锁。
/// </para>
/// <para>
/// 本仓已知约束：生产装配 <c>EnableRetryOnFailure(3)</c>，
/// <c>NpgsqlRetryingExecutionStrategy</c> 拒绝用户发起的事务，因此自管事务必须交给
/// <c>CreateExecutionStrategy()</c> 作为可重试单元执行，并在每次尝试开始时清空变更跟踪
/// （与 <c>DeviceManagementService</c> / <c>MobileAnalyticsMaterializationService</c> 同款注释）。
/// </para>
/// </summary>
public static class UserSessionLock
{
    /// <summary>
    /// 由用户 id 稳定派生的锁键（同一 GUID 在任何实例上都映射到同一个 bigint）。
    /// 取高低两个 64 位异或，用满 128 位熵——即便碰撞也只会多一次串行化，不会串号。
    /// </summary>
    public static long KeyFor(Guid userId)
    {
        var bytes = userId.ToByteArray();
        return BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8);
    }

    /// <summary>
    /// 在「该用户独占」的前提下执行 <paramref name="operation"/>。
    /// 内存库（测试环境）不做并发控制，直接执行——那里没有真实的并发写者。
    /// </summary>
    public static async Task<T> RunAsync<T>(
        PimDbContext db, Guid userId, Func<Task<T>> operation, CancellationToken ct)
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
                "SELECT pg_advisory_xact_lock({0})", new object[] { KeyFor(userId) }, ct);

            var result = await operation();

            await transaction.CommitAsync(ct);
            return result;
        });
    }
}
