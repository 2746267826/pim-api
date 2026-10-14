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
internal sealed class TaskRowLock : IAsyncDisposable
{
    private readonly IDbContextTransaction? _transaction;

    private TaskRowLock(IDbContextTransaction? transaction) => _transaction = transaction;

    /// <summary>取单任务锁。返回的对象释放时提交事务（即释放锁）。</summary>
    internal static Task<TaskRowLock> AcquireAsync(PimDbContext db, Guid taskId, CancellationToken ct)
        => AcquireManyAsync(db, new[] { taskId }, ct);

    /// <summary>取多任务锁（批量终态更新用）；按 id 排序依次加锁，避免与其它批量请求死等。</summary>
    internal static async Task<TaskRowLock> AcquireManyAsync(
        PimDbContext db,
        IReadOnlyCollection<Guid> taskIds,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || taskIds.Count == 0)
            return new TaskRowLock(null);

        var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var taskId in taskIds.OrderBy(id => id))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({taskId.ToString()}))",
                ct);
        }

        return new TaskRowLock(transaction);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is null)
            return;

        await _transaction.CommitAsync();
        await _transaction.DisposeAsync();
    }
}
