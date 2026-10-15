using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

/// <summary>
/// 执行段的「闭合」原语（工单 03 D3）：闭合 = 写 <c>ends_at</c> + 落终止状态。
/// 与 <see cref="TaskRowLock"/> 放在同一文件，因为它们共同服务同一条不变量：
/// **终态或被删除的任务下不允许存在「正在跑」（<c>ends_at IS NULL</c>）的执行段**。
/// </summary>
internal static class RunningSegments
{
    /// <summary>
    /// 闭合一条开放段：右端取 <paramref name="now"/>，但不得早于 <c>starts_at</c>
    /// （时钟回拨 / 固定时钟下不写出负时长的段）。
    /// </summary>
    internal static void Close(TaskExecutionSegmentEntity segment, string status, DateTimeOffset now)
    {
        segment.EndsAt = now < segment.StartsAt ? segment.StartsAt : now;
        segment.Status = status;
        segment.UpdatedAt = now;
    }

    /// <summary>闭合给定任务集合里所有开放段（软删除 / 归档 / 终态化时调用），返回闭合条数。</summary>
    internal static async Task<int> CloseAllAsync(
        PimDbContext db,
        IReadOnlyCollection<Guid> taskIds,
        string status,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (taskIds.Count == 0)
            return 0;

        var open = await db.Set<TaskExecutionSegmentEntity>()
            .Where(s => taskIds.Contains(s.TaskId) && s.EndsAt == null)
            .ToListAsync(ct);

        foreach (var segment in open)
            Close(segment, status, now);

        return open.Count;
    }
}

/// <summary>
/// 任务级串行化锁（复审 I1/I2）。
///
/// 为什么需要：<c>timer/start</c> 是「查有没有开放段 → 插入开放段」，终态更新是
/// 「查有没有开放段 → 写终态 + 闭合」。两条链都没有共同锁时，交替执行会同时通过检查，
/// 结果是同一任务出现两条开放段，或者「已完成但仍有在跑的段」。
///
/// 实现：显式事务 + PostgreSQL 事务级咨询锁（<c>pg_advisory_xact_lock(hashtext(task_id))</c>）。
/// 锁在事务提交/回滚时自动释放，无需额外清理；键碰撞只会多串行一点，不影响正确性。
/// 非 Npgsql 提供程序（单测用的 InMemory / Sqlite）安全降级为空操作 —— 那里没有并发语义。
/// </summary>
internal static class TaskRowLock
{
    /// <summary>
    /// 在「任务级咨询锁 + 单事务」内执行 <paramref name="action"/>。
    ///
    /// ⚠️ 为什么必须把**整个工作单元**包在 <c>CreateExecutionStrategy()</c> 里：
    /// 本库的 DbContext 配置了 <c>EnableRetryOnFailure</c>（NpgsqlRetryingExecutionStrategy），
    /// 该策略**禁止用户自行发起的事务**——只要在它管理之外开事务，后续任何命令都会抛
    /// 「does not support user-initiated transactions」。所以「开事务 → 加锁 → 业务写入 → 提交」
    /// 必须整体作为可重试单元交给执行策略。
    ///
    /// 非 Npgsql 提供程序（单测用的 InMemory / Sqlite）没有并发语义，直接执行 action。
    /// </summary>
    internal static async Task<T> WithTaskLocksAsync<T>(
        PimDbContext db,
        IReadOnlyCollection<Guid> taskIds,
        Func<Task<T>> action,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || taskIds.Count == 0)
            return await action();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // 复审 N1：执行策略可能在瞬时故障后**重放**整个 lambda。重放必须从干净的跟踪状态开始 ——
            // 否则上一轮已被接受、却随事务一起回滚的实体改动仍留在变更跟踪器里，
            // 而跟踪查询不会用库里的值覆盖已跟踪实体，于是重放时那些字段不会被重新写库，
            // 新建实体也会被重复 Add。清空跟踪器后整个工作单元从头读库，重放语义确定。
            // （提交结果不确定时重放会读到一个已经存在的开放段 → 由唯一索引/前置检查给出干净的 02058，
            //   而不是插出第二条段。）
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await AcquireLocksAsync(db, taskIds, ct);

            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// 总是开一个事务（即使没有任何 id）并交给执行策略，供「加锁集合要在事务内才能确定」的场景：
    /// 调用方在事务内加载真实目标集合后，用 <see cref="AcquireLocksAsync"/> 逐把补锁。
    /// 整本任务簿删除走这条路 —— 用锁外的投影 id 加锁是错的（复审 R1）。
    /// </summary>
    internal static async Task<T> WithTransactionAsync<T>(
        PimDbContext db,
        Func<Task<T>> action,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return await action();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// 在**已存在的事务**内取任务级咨询锁（调用方负责事务边界）。
    /// 供已经有自己事务/执行策略的路径复用（例如数据中心批量归档），避免嵌套 <c>BeginTransaction</c>。
    /// </summary>
    internal static async Task AcquireLocksAsync(
        PimDbContext db,
        IReadOnlyCollection<Guid> taskIds,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || taskIds.Count == 0)
            return;

        // 按 id 排序依次加锁，避免两个批量请求以不同顺序取锁而互相等待。
        foreach (var taskId in taskIds.Distinct().OrderBy(id => id))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({taskId.ToString()}))",
                ct);
        }
    }
}
