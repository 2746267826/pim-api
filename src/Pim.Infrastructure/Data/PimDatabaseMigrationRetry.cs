namespace Pim.Infrastructure.Data;

/// <summary>
/// 启动期数据库迁移的重试策略。
///
/// 背景：迁移失败曾经只记一条 <c>Log.Warning</c> 就继续启动，结果是进程以"半套 schema"
/// 对外提供服务 —— 后续所有读写以 500 收场，而启动日志里看不出根因是 schema。
/// 现在改为「有限次重试（覆盖 DB 慢启动）→ 仍失败则把异常抛给调用方显式失败退出」，
/// 重启交给编排层；这样"迁移没成功"不会再被降级成一个 Warning。
/// </summary>
public static class PimDatabaseMigrationRetry
{
    /// <summary>默认重试次数（含首次执行）：覆盖 DB 慢启动，又不至于让编排层等太久。</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>默认重试间隔。</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 执行 <paramref name="migrate"/>，失败时最多尝试 <paramref name="maxAttempts"/> 次；
    /// 用尽次数后抛出最后一次异常（由调用方决定如何终止进程），取消时同样抛出。
    /// </summary>
    /// <param name="migrate">一次迁移尝试（含 schema 收养 + Migrate）。</param>
    /// <param name="onRetry">每次重试前的回调（记录日志/指标），入参为异常与已失败的尝试序号。</param>
    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> migrate,
        Func<Exception, int, Task>? onRetry = null,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts),
                maxAttempts,
                "maxAttempts must be at least 1.");

        var retryDelay = delay ?? DefaultDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await migrate(ct);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                if (onRetry is not null)
                    await onRetry(ex, attempt);
                if (retryDelay > TimeSpan.Zero)
                    await Task.Delay(retryDelay, ct);
            }
        }
    }
}
