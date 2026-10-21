using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 清理过期的幂等记录（契约语义 <c>docs/client-contract-semantics.md</c> §2.4）。
///
/// <para>
/// 幂等记录只在 24 小时内有意义；表里存的是完整响应体，不清会无限增长。
/// 过期行还占着唯一索引，虽然有「接管过期行」的兜底，但定期清理才是常态。
/// 由 Hangfire 每小时跑一次（在 <c>Pim.Api/Program.cs</c> 的 RecurringJob 注册块里）；Hangfire 被禁用时不运行，
/// 此时接管逻辑仍能保证同一个 key 不会永久 409。
/// </para>
/// </summary>
public sealed class IdempotencyCleanupJob
{
    private readonly PimDbContext _db;
    private readonly ILogger<IdempotencyCleanupJob> _logger;
    private readonly TimeProvider _timeProvider;

    public IdempotencyCleanupJob(
        PimDbContext db,
        ILogger<IdempotencyCleanupJob> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var removed = await _db.IdempotencyRecords
            .IgnoreQueryFilters()
            .Where(r => r.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            _logger.LogInformation("清理过期幂等记录 {Count} 条", removed);
    }
}
