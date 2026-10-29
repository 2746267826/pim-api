using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;
using Pim.Infrastructure.Operations;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileSyncBacklogInspector : IDataQualityInspector
{
    /// <summary>
    /// 处理中的状态词表。写入侧在开始处理前先落一条 <c>pending</c> 批次（#243），
    /// 因此"上传中断 / 进程崩溃 / 长时间未完成"在这里是可见的，而不是永远查不到。
    /// </summary>
    private static readonly string[] ActiveStatuses =
        [MobileSyncBatchStatus.Pending, "processing", "syncing"];

    private readonly PimDbContext _db;
    private readonly ILogger<MobileSyncBacklogInspector> _logger;

    public MobileSyncBacklogInspector(PimDbContext db, ILogger<MobileSyncBacklogInspector> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string CheckName => "sync_backlog";

    public async Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            var activeBatches = await _db.Set<MobileSyncBatchEntity>()
                .AsNoTracking()
                .Where(batch => ActiveStatuses.Contains(batch.Status))
                .ToListAsync(ct);
            var inspected = activeBatches
                .Select(batch =>
                {
                    var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
                    var leaseUntilUtc = MobileSyncBatchEnvelopeCodec.EffectiveLeaseUntil(
                        batch.CreatedAt,
                        envelope);
                    var retryDue = envelope.RetryAfterUtc is null
                        || envelope.RetryAfterUtc <= now;
                    return new
                    {
                        Batch = batch,
                        Envelope = envelope,
                        ActiveLease = leaseUntilUtc > now,
                        ScheduledRetry = envelope.RetryAfterUtc is { } retryAfter && retryAfter > now,
                        Overdue = leaseUntilUtc <= now && retryDue
                    };
                })
                .ToList();

            var overdueBatches = inspected
                .Where(item => item.Overdue)
                .Select(item => item.Batch)
                .ToList();
            var backlogCount = overdueBatches.Count;
            try
            {
                PimMetrics.SyncBatchBacklog.Set(backlogCount);
            }
            catch { }

            var activeLeaseCount = inspected.Count(item => item.ActiveLease);
            var scheduledRetryCount = inspected.Count(item => item.ScheduledRetry);
            var legacyPendingCount = inspected.Count(item =>
                item.Batch.Status == MobileSyncBatchStatus.Pending
                && item.Envelope.LeaseUntilUtc is null
                && item.Envelope.AttemptCount == 0);
            var isHealthy = backlogCount == 0;
            var message = isHealthy
                ? "No overdue mobile sync batches found."
                : $"{backlogCount} mobile sync batch(es) have no active lease or due retry.";

            var details = new Dictionary<string, string>
            {
                ["overdueCount"] = backlogCount.ToString(),
                ["processingLeaseMinutes"] = MobileUsageIngestService.PendingBatchLease.TotalMinutes.ToString(),
                ["activeLeaseCount"] = activeLeaseCount.ToString(),
                ["scheduledRetryCount"] = scheduledRetryCount.ToString(),
                ["legacyPendingCount"] = legacyPendingCount.ToString()
            };

            if (overdueBatches.Count > 0)
            {
                var oldest = overdueBatches.OrderBy(batch => batch.CreatedAt).First();
                details["oldestBatchId"] = oldest.BatchId;
                details["oldestBatchCreatedAt"] = oldest.CreatedAt.ToString("O");
                details["oldestBatchDeviceId"] = oldest.DeviceId;
            }

            return new DataQualityInspectionResult(
                CheckName,
                isHealthy,
                backlogCount,
                message,
                details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query mobile sync batches for backlog inspection");
            return new DataQualityInspectionResult(
                CheckName,
                false,
                1,
                $"Database error querying mobile sync batches: {ex.Message}");
        }
    }
}
