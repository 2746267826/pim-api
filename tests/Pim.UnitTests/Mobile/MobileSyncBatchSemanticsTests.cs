using Microsoft.EntityFrameworkCore;
using Pim.Core.Invariants;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Metrics;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 手机同步批次的"状态语义 + 信号"回归测试（#241 状态失真 / #242 缺口误判 / #243 可观测性缺失）。
/// </summary>
public sealed class MobileSyncBatchSemanticsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T04:00:00Z");

    private static MobileUsageIngestService IngestService(PimDbContext db, DateTimeOffset? now = null)
        => new(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            MobileTestHelpers.Time(now ?? Now));

    // ===================== #241 状态语义 =====================

    [Fact]
    public async Task Ingest_RejectedItemKeepsBatchCompletedInsteadOfFailing()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);
        var start = Now.AddHours(-2);
        var request = new MobileUsageEventsUploadRequest(
            "android-main",
            "batch-rejected",
            start,
            start.AddHours(1),
            [
                // 显示名缺失 ⇒ 条目级拒绝：不该把整批打成失败（#241）
                new MobileAppMetadataDto(
                    "com.example.bad",
                    string.Empty,
                    "1.0",
                    1,
                    false,
                    "tools",
                    null,
                    null,
                    null,
                    "{}")
            ],
            [],
            [new MobileUsageSummaryDto("com.example.app", start, start.AddHours(1), 60_000, start, "usage-stats-fallback", "{}")]);

        var result = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(1, result.RejectedCount);
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
        Assert.Equal(1, batch.RejectedCount);
        Assert.Equal(1, batch.AcceptedCount);
        Assert.Equal(0, batch.FailedCount);

        // EPIC #254 S11 / INV-M21：状态与计数必须自洽
        var invariant = DataReliabilityInvariants.CheckS11_StatusSemantics(
            [new BatchSyncStatusRecord
            {
                BatchId = batch.BatchId,
                Status = batch.Status,
                AcceptedCount = batch.AcceptedCount,
                FailedCount = batch.FailedCount,
                RejectedCount = batch.RejectedCount,
                TotalCount = batch.AcceptedCount + batch.FailedCount + batch.RejectedCount
            }]);
        Assert.True(invariant.IsPass, invariant.Detail);
    }

    [Fact]
    public async Task Ingest_RecordsReadableBatchErrorsInsteadOfLeavingThemEmpty()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);
        var start = Now.AddHours(-2);
        var request = new MobileUsageEventsUploadRequest(
            "android-main",
            "batch-error-summary",
            start,
            start.AddHours(1),
            [],
            [],
            [new MobileUsageSummaryDto("com.example.app", start, start.AddHours(1), -5, start, "usage-stats-fallback", "{}")]);

        var result = await service.IngestAsync(request, CancellationToken.None);
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();

        Assert.Equal(1, result.RejectedCount);
        Assert.True(MobileSyncBatchEnvelopeCodec.TryDeserialize(batch.ErrorJson, out var envelope));
        Assert.NotEmpty(envelope.BatchErrors);
        Assert.Contains(envelope.BatchErrors, error => error.Contains("invalid-duration", StringComparison.Ordinal));
        // DTO 侧不再恒为 null（#243）
        Assert.NotNull(MobileSyncBatchEnvelopeCodec.ErrorMessage(batch.ErrorJson));
    }

    [Fact]
    public async Task Quality_DoesNotCountLegacyCompletedWithErrorsAsFailedBatch()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var windowStart = Now.AddHours(-3);
        var windowEnd = Now.AddHours(-1);
        db.Set<MobileSyncBatchEntity>().AddRange(
            new MobileSyncBatchEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = "android-main",
                BatchId = "batch-legacy-with-errors",
                WindowStartUtc = windowStart,
                WindowEndUtc = windowEnd,
                AcceptedCount = 120,
                RejectedCount = 170,
                FailedCount = 0,
                Status = MobileSyncBatchStatus.LegacyCompletedWithErrors,
                ErrorJson = "{}",
                CreatedAt = Now.AddHours(-4),
                CompletedAtUtc = Now.AddHours(-4)
            });
        await db.SaveChangesAsync();

        var service = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));
        var quality = await service.GetQualityAsync(Now.AddHours(-6), Now, CancellationToken.None);

        var sync = Assert.Single(quality.Components, component => component.Key == "mobile-sync");
        Assert.Equal("0", sync.Details["failedBatchCount"]);
        Assert.Equal("170", sync.Details["rejectedCount"]);
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "mobile-sync-failed-batch");
    }

    [Fact]
    public async Task Quality_FlagsFailedAndStalledBatchesSeparately()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().AddRange(
            new MobileSyncBatchEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = "android-main",
                BatchId = "batch-failed",
                WindowStartUtc = Now.AddHours(-3),
                WindowEndUtc = Now.AddHours(-2),
                AcceptedCount = 1,
                FailedCount = 3,
                Status = MobileSyncBatchStatus.Failed,
                ErrorJson = "{}",
                CreatedAt = Now.AddHours(-3),
                CompletedAtUtc = Now.AddHours(-3)
            },
            new MobileSyncBatchEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = "android-main",
                BatchId = "batch-stalled",
                WindowStartUtc = Now.AddHours(-2),
                WindowEndUtc = Now.AddHours(-1),
                AcceptedCount = 0,
                Status = MobileSyncBatchStatus.Pending,
                ErrorJson = "{}",
                CreatedAt = Now.AddHours(-1),
                CompletedAtUtc = null
            });
        await db.SaveChangesAsync();

        var service = new MobileQualityService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));
        var quality = await service.GetQualityAsync(Now.AddHours(-6), Now, CancellationToken.None);

        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-sync-failed-batch");
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-sync-stalled-batch");
        var sync = Assert.Single(quality.Components, component => component.Key == "mobile-sync");
        Assert.Equal("1", sync.Details["failedBatchCount"]);
        Assert.Equal("1", sync.Details["stalledBatchCount"]);
    }

    [Fact]
    public async Task UsageQuery_DoesNotReportLegacyCompletedWithErrorsAsFailedBatch()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-legacy",
            WindowStartUtc = Now.AddHours(-2),
            WindowEndUtc = Now.AddHours(-1),
            AcceptedCount = 10,
            FailedCount = 0,
            Status = MobileSyncBatchStatus.LegacyCompletedWithErrors,
            ErrorJson = "{}",
            CreatedAt = Now.AddHours(-2),
            CompletedAtUtc = Now.AddHours(-2)
        });
        await db.SaveChangesAsync();

        var service = new MobileUsageQueryService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var summary = await service.GetSummaryAsync(
            new MobileSummaryQuery("android-main", Now.AddHours(-3), Now),
            CancellationToken.None);

        Assert.Equal(0, summary.QualityIssueCount);
    }

    // ===================== #242 缺口判定 =====================

    [Fact]
    public async Task GapService_TreatsLegacyCompletedWithErrorsBatchAsCoverage()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var start = Now.AddHours(-4);
        var end = Now.AddHours(-2);
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-covered-with-rejections",
            WindowStartUtc = start,
            WindowEndUtc = end,
            AcceptedCount = 66_289,
            RejectedCount = 170,
            FailedCount = 0,
            Status = MobileSyncBatchStatus.LegacyCompletedWithErrors,
            ErrorJson = "{}",
            CreatedAt = start,
            CompletedAtUtc = start
        });
        await db.SaveChangesAsync();

        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));
        var response = await service.GetGapsAsync(
            new MobileGapRequest("android-main", start, end, "{}"),
            CancellationToken.None);

        Assert.Empty(response.Windows);
    }

    [Fact]
    public async Task GapService_StillReportsGapForBatchWithRealFailures()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var start = Now.AddHours(-4);
        var end = Now.AddHours(-2);
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-real-failure",
            WindowStartUtc = start,
            WindowEndUtc = end,
            AcceptedCount = 0,
            FailedCount = 5,
            Status = MobileSyncBatchStatus.Failed,
            ErrorJson = "{}",
            CreatedAt = start,
            CompletedAtUtc = start
        });
        await db.SaveChangesAsync();

        var service = new MobileGapService(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));
        var response = await service.GetGapsAsync(
            new MobileGapRequest("android-main", start, end, "{}"),
            CancellationToken.None);

        Assert.NotEmpty(response.Windows);
    }

    // ===================== #243 可观测性 =====================

    [Fact]
    public async Task BacklogInspector_ReportsOverduePendingBatches()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-stuck",
            WindowStartUtc = Now.AddHours(-3),
            WindowEndUtc = Now.AddHours(-2),
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = "{}",
            CreatedAt = Now.AddMinutes(-45),
            CompletedAtUtc = null
        });
        await db.SaveChangesAsync();

        var inspector = new MobileSyncBacklogInspector(
            db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileSyncBacklogInspector>.Instance);

        var result = await inspector.InspectAsync(Now, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Equal(1, result.IssueCount);
        Assert.Equal("1", result.Details["overdueCount"]);
        Assert.Equal("batch-stuck", result.Details["oldestBatchId"]);
        Assert.Equal(1d, PimMetrics.SyncBatchBacklog.Value);
    }

    [Fact]
    public async Task BacklogInspector_IgnoresRecentPendingBatches()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-in-flight",
            WindowStartUtc = Now.AddHours(-1),
            WindowEndUtc = Now,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = "{}",
            CreatedAt = Now.AddMinutes(-1),
            CompletedAtUtc = null
        });
        await db.SaveChangesAsync();

        var inspector = new MobileSyncBacklogInspector(
            db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileSyncBacklogInspector>.Instance);

        var result = await inspector.InspectAsync(Now, CancellationToken.None);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task Ingest_TakesOverStalePendingBatchAndFinalizesIt()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var windowStart = Now.AddHours(-3);
        var windowEnd = Now.AddHours(-2);
        // 上一次处理者消失后留下的 pending 批次（超过租约）
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-interrupted",
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = "{}",
            CreatedAt = Now.AddMinutes(-30),
            CompletedAtUtc = null
        });
        await db.SaveChangesAsync();

        var service = IngestService(db);
        var result = await service.IngestAsync(
            new MobileUsageEventsUploadRequest(
                "android-main",
                "batch-interrupted",
                windowStart,
                windowEnd,
                [
                    new MobileAppMetadataDto(
                        "com.example.app",
                        "Example",
                        "1.0",
                        1,
                        false,
                        "tools",
                        null,
                        null,
                        null,
                        "{}")
                ],
                [],
                []),
            CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
        Assert.NotNull(batch.CompletedAtUtc);
        Assert.Equal(1, batch.AcceptedCount);
        // 认领时把租约刷新到本次尝试的开始时间（"还在动"由此可判），而不是保留 30 分钟前的时间
        Assert.Equal(Now, batch.CreatedAt);
    }

    [Fact]
    public async Task Ingest_DoesNotReprocessABatchStillOwnedByAnotherRequest()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var windowStart = Now.AddHours(-3);
        var windowEnd = Now.AddHours(-2);
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            BatchId = "batch-in-flight",
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = "{}",
            CreatedAt = Now.AddSeconds(-30),
            CompletedAtUtc = null
        });
        await db.SaveChangesAsync();

        var service = IngestService(db);
        var result = await service.IngestAsync(
            new MobileUsageEventsUploadRequest(
                "android-main",
                "batch-in-flight",
                windowStart,
                windowEnd,
                [],
                [
                    new MobileUsageEventDto(
                        "com.example.app",
                        "MOVE_TO_FOREGROUND",
                        windowStart.AddMinutes(5),
                        "Main",
                        windowStart.AddMinutes(6),
                        "{}",
                        "event-1")
                ],
                []),
            CancellationToken.None);

        Assert.Empty(result.ItemResults);
        Assert.Empty(await db.Set<MobileUsageEventEntity>().ToListAsync());
    }
}
