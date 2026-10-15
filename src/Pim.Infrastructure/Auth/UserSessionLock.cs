using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

/// <summary>
/// 按用户把「会话相关写操作」串行化的原语（见 <see cref="AdvisoryLock"/> 的实现）。
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
    public static Task<T> RunAsync<T>(
        PimDbContext db, Guid userId, Func<Task<T>> operation, CancellationToken ct)
        => AdvisoryLock.RunAsync(db, KeyFor(userId), operation, ct);
}
