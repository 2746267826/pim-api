using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

[Trait("DataSource", "RealDb")]
public sealed class ActivityClassificationSnapshotRealDbTests
{
    [SkippableFact]
    public async Task EnsureClassificationsAsync_ConcurrentMaterializationDoesNotThrow()
    {
        var connectionString = RealDbTestConnection.Require();
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;

        var records = Enumerable.Range(0, 4)
            .Select(index => new PcDetailRecord(
                "window",
                $"2026-09-17T08:{index:00}:00Z",
                $"2026-09-17T08:{index:00}:30Z",
                30,
                $"issue-284-{Guid.NewGuid():N}",
                "Code.exe",
                "Code.exe",
                "其他",
                $"issue-284-{index}.cs",
                null,
                null,
                null,
                null,
                null,
                null))
            .ToList();
        var keys = records.Select(ActivityClassificationRecordKey.FromRecord).ToArray();

        try
        {
            var tasks = Enumerable.Range(0, 8)
                .Select(_ => EnsureAsync(options, records))
                .ToArray();
            await Task.WhenAll(tasks);

            await using var verify = new PimDbContext(options);
            Assert.Equal(keys.Length, await verify.Set<ActivityClassificationEntity>()
                .CountAsync(snapshot => keys.Contains(snapshot.RecordKey)));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM pc_activity_classifications WHERE record_key = ANY(@keys)", cleanup);
            command.Parameters.AddWithValue("keys", keys);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureAsync(
        DbContextOptions<PimDbContext> options,
        IReadOnlyCollection<PcDetailRecord> records)
    {
        await using var db = new PimDbContext(options);
        var service = new ActivityClassificationSnapshotService(
            db,
            NullLogger<ActivityClassificationSnapshotService>.Instance);
        await service.EnsureClassificationsAsync(records, [], null, CancellationToken.None);
    }

    /// <summary>
    /// #339 复审回归：**处于外层显式事务中时，唯一键冲突仍必须被就地恢复**。
    ///
    /// <para>
    /// 用户触发的手动重算走 <c>ExecuteInTransactionAsync</c>：先开事务，再调用
    /// <c>EnsureClassificationsAsync(saveChanges: true)</c>。此时他方（后台补齐/另一请求）
    /// 可能已写入同 record_key。EF 在事务内保存时会用 <c>SAVEPOINT</c> 包裹，
    /// PostgreSQL 的 23505 回滚到该 savepoint 后**事务仍然可用**，因此必须继续
    /// detach + 重试；把整个事务期都排除在重试之外，会让手动重算直接失败
    /// （复审指出的 Important 回归）。
    /// </para>
    ///
    /// <para>
    /// 与之相对，40P01 死锁会中止整个事务（后续语句报 25P02），那种情形不能在事务内
    /// 就地重试 —— 见 <c>SaveWithUniqueKeyRetryAsync</c> 的门控注释。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task EnsureClassificationsAsync_InsideExplicitTransaction_RecoversFromUniqueViolation()
    {
        var connectionString = RealDbTestConnection.Require();
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;

        var records = Enumerable.Range(0, 3)
            .Select(index => new PcDetailRecord(
                "window",
                $"2026-09-18T08:{index:00}:00Z",
                $"2026-09-18T08:{index:00}:30Z",
                30,
                $"issue-339-tx-{Guid.NewGuid():N}",
                "Code.exe",
                "Code.exe",
                "其他",
                $"issue-339-tx-{index}.cs",
                null,
                null,
                null,
                null,
                null,
                null))
            .ToList();
        var keys = records.Select(ActivityClassificationRecordKey.FromRecord).ToArray();

        try
        {
            // 在**显式事务内**物化这批 key，并在首次保存时由他方抢写同 key
            //（CompetingWritePimDbContext 用独立连接提交后，让本次保存以 23505 失败）：
            // 必须恢复，而不是把 23505 抛给调用方。
            // 走与生产 ExecuteInTransactionAsync 相同的执行策略模式
            //（NpgsqlRetryingExecutionStrategy 不允许直接开用户事务）。
            await using (var db = new CompetingWritePimDbContext(options, connectionString, keys[0]))
            {
                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    var service = new ActivityClassificationSnapshotService(
                        db,
                        NullLogger<ActivityClassificationSnapshotService>.Instance);

                    await service.EnsureClassificationsAsync(records, [], null, CancellationToken.None);
                    await transaction.CommitAsync();
                });
            }

            await using var verify = new PimDbContext(options);
            Assert.Equal(keys.Length, await verify.Set<ActivityClassificationEntity>()
                .CountAsync(snapshot => keys.Contains(snapshot.RecordKey)));
            // 抢写方先提交的那条必须保留（本批应 detach 自己的重复实体，而不是覆盖它）。
            Assert.Equal("competing writer", await verify.Set<ActivityClassificationEntity>()
                .Where(snapshot => snapshot.RecordKey == keys[0])
                .Select(snapshot => snapshot.Explanation)
                .SingleAsync());
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM pc_activity_classifications WHERE record_key = ANY(@keys)", cleanup);
            command.Parameters.AddWithValue("keys", keys);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 与上一用例相对：**处于外层显式事务中时，死锁（40P01）不得就地重试**。
    ///
    /// <para>
    /// 死锁会中止整个事务，此后任何语句都报 25P02（current transaction is aborted）。
    /// 若仍在事务内原地重试，一个可诊断的死锁会退化成一串 "25P02" 噪音，且把真正的
    /// 失败原因掩埋。正确行为是把 40P01 原样交给外层执行策略（它会重跑整个工作单元）。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task EnsureClassificationsAsync_InsideExplicitTransaction_PropagatesDeadlockWithoutRetry()
    {
        var connectionString = RealDbTestConnection.Require();
        PimDbContext.RegisterModuleAssembly(typeof(ActivityClassificationEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;

        var record = new PcDetailRecord(
            "window",
            "2026-09-18T09:00:00Z",
            "2026-09-18T09:00:30Z",
            30,
            $"issue-339-dl-{Guid.NewGuid():N}",
            "Code.exe",
            "Code.exe",
            "其他",
            "issue-339-dl.cs",
            null, null, null, null, null, null);
        var key = ActivityClassificationRecordKey.FromRecord(record);

        try
        {
            await using var db = new DeadlockPimDbContext(options);
            var strategy = db.Database.CreateExecutionStrategy();

            // 40P01 会被外层 NpgsqlRetryingExecutionStrategy 视为暂时性错误并重跑整个工作单元，
            // 重试用尽后以 RetryLimitExceededException 浮出（内层是 DbUpdateException）。
            // 两种形态都表示"死锁原样交给了外层"，而不是被本方法悄悄吞掉。
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                var service = new ActivityClassificationSnapshotService(
                    db,
                    NullLogger<ActivityClassificationSnapshotService>.Instance);

                await service.EnsureClassificationsAsync([record], [], null, CancellationToken.None);
                await transaction.CommitAsync();
            }));

            Assert.Contains(
                "deadlock",
                exception.ToString(),
                StringComparison.OrdinalIgnoreCase);

            // 关键门控断言：事务内**不得**就地重试。
            // EnableRetryOnFailure(3) 下外层策略最多尝试 1 + 3 = 4 次；
            // 正确实现每次外层尝试只保存一次 → 保存次数 ≤ 4。
            // 若在事务内就地重试死锁，每次外层尝试会跑满 5 次保存（最多 20 次），
            // 且后续语句只会得到 25P02，把真实失败原因掩埋。
            const int outerAttemptBudget = 4;
            Assert.True(
                db.SaveAttemptCount <= outerAttemptBudget,
                $"事务内不应就地重试死锁，但保存次数达到 {db.SaveAttemptCount}（外层策略上限 {outerAttemptBudget}）。");
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM pc_activity_classifications WHERE record_key = @key", cleanup);
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>在首次保存时抛死锁（40P01），用于验证事务内的门控行为。</summary>
    private sealed class DeadlockPimDbContext(DbContextOptions<PimDbContext> options) : PimDbContext(options)
    {
        public int SaveAttemptCount { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttemptCount++;
            throw new DbUpdateException(
                "Simulated deadlock inside a user transaction.",
                new PostgresException("deadlock detected", "ERROR", "ERROR",
                    PostgresErrorCodes.DeadlockDetected));
        }
    }

    /// <summary>
    /// 在首次保存时用**独立连接**提交同 record_key 的记录，然后让本次保存以 23505 失败。
    /// 这样能在单个用例内确定性地复现「事务中遇到唯一键冲突」，而不依赖真并发时序。
    /// </summary>
    private sealed class CompetingWritePimDbContext(
        DbContextOptions<PimDbContext> options,
        string connectionString,
        string conflictKey) : PimDbContext(options)
    {
        private bool _injected;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_injected)
            {
                _injected = true;

                await using (var competitor = new NpgsqlConnection(connectionString))
                {
                    await competitor.OpenAsync();
                    await using var insert = new NpgsqlCommand(
                        """
                        INSERT INTO pc_activity_classifications
                            (id, record_key, record_type, device_id, source_event_ids, started_at, ended_at,
                             category_name, category_color, source, explanation, classified_at)
                        VALUES (gen_random_uuid(), @key, 'window', 'device-1', '[]',
                                TIMESTAMPTZ '2026-09-18T08:00:00Z', TIMESTAMPTZ '2026-09-18T08:00:30Z',
                                '其他', '#64748b', 'fallback', 'competing writer', now())
                        """,
                        competitor);
                    insert.Parameters.AddWithValue("key", conflictKey);
                    await insert.ExecuteNonQueryAsync();
                }

                throw new DbUpdateException(
                    "Simulated concurrent record_key insert inside a user transaction.",
                    new PostgresException("duplicate key value", "ERROR", "ERROR",
                        PostgresErrorCodes.UniqueViolation));
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
