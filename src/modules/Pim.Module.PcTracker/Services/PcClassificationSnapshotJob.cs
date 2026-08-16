using Microsoft.Extensions.Logging;

namespace Pim.Module.PcTracker.Services;

/// <summary>Hangfire 入口薄壳：触发分类快照后台补齐并记录统计。
/// 异常一律记 error 不抛出，避免 Hangfire 重试风暴。</summary>
public sealed class PcClassificationSnapshotJob
{
    private const int DefaultLookbackDays = 14;

    private readonly PcClassificationBackfillService _backfill;
    private readonly ILogger<PcClassificationSnapshotJob> _logger;

    public PcClassificationSnapshotJob(
        PcClassificationBackfillService backfill,
        ILogger<PcClassificationSnapshotJob> logger)
    {
        _backfill = backfill;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        try
        {
            var stats = await _backfill.BackfillAsync(DefaultLookbackDays, CancellationToken.None);
            _logger.LogInformation(
                "PC classification snapshot backfill completed: processed {ProcessedDays} day(s), wrote {WrittenSnapshots} snapshot(s).",
                stats.ProcessedDays,
                stats.WrittenSnapshots);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "PC classification snapshot backfill failed.");
        }
    }
}
