using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileSyncLifecycleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T06:00:00Z");
    private const string DeviceId = "android-lifecycle";

    [Fact]
    public void Envelope_RemainsCompatibleWithExistingThreeFieldJson()
    {
        var legacy = MobileSyncBatchEnvelopeCodec.Serialize(
            [
                new MobileIngestItemResult(
                    "event-1",
                    "usage-event",
                    "accepted",
                    "accepted",
                    "Accepted.")
            ],
            []);

        Assert.True(MobileSyncBatchEnvelopeCodec.TryDeserialize(legacy, out var envelope));
        Assert.Null(envelope.PayloadSha256);
        Assert.Null(envelope.LeaseUntilUtc);
        Assert.Null(envelope.LeaseId);
        Assert.Null(envelope.RetryAfterUtc);
        Assert.Equal(0, envelope.AttemptCount);
    }

    [Fact]
    public void ValidateRequestBudget_AllowsDailySummaryAndLegacyClientMaximum()
    {
        var request = Request(
            "batch-budget-allowed",
            Now,
            Now.AddHours(24),
            events: BuildEvents(1_498),
            summaries:
            [
                Summary(
                    "com.example.daily",
                    Now,
                    Now.AddHours(24),
                    Now.AddHours(23),
                    "summary-1")
            ]);

        Assert.Equal(1_500, request.Apps.Count + request.Events.Count + request.Summaries.Count);
        MobileUsageIngestService.ValidateRequestBudget(request);

        var exactWindow = Request("batch-window-25h", Now, Now.AddHours(25));
        MobileUsageIngestService.ValidateRequestBudget(exactWindow);
    }

    [Fact]
    public async Task IngestAsync_AcceptsTwentyFourHourDailySummary()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var end = Now.AddHours(24);
        var request = Request(
            "batch-daily-summary",
            Now,
            end,
            summaries:
            [
                Summary(
                    "com.example.daily",
                    Now,
                    end,
                    end.AddMinutes(-1),
                    "daily-summary",
                    totalVisibleMs: 10L * 60L * 60L * 1000L)
            ]);

        var result = await CreateService(db, Now).IngestAsync(request, CancellationToken.None);

        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.RejectedCount);
        var summary = await db.Set<MobileUsageSummaryEntity>().SingleAsync();
        Assert.Equal(Now, summary.WindowStartUtc);
        Assert.Equal(end, summary.WindowEndUtc);
        Assert.Equal(10L * 60L * 60L * 1000L, summary.TotalTimeVisibleMs);
        Assert.DoesNotContain("duration-clamped", summary.QualityFlagsJson);
    }

    [Fact]
    public async Task IngestAsync_RejectsFortyHourWindowBeforeCreatingPending()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db, Now);
        var request = Request("batch-window-40h", Now, Now.AddHours(40));

        var error = await Assert.ThrowsAsync<MobileSyncBatchRequestValidationException>(
            () => service.IngestAsync(request, CancellationToken.None));

        Assert.Equal(40001, error.ErrorCode);
        Assert.False(await db.Set<MobileSyncBatchEntity>().AnyAsync());
        Assert.False(await db.Set<MobileAppCatalogEntity>().AnyAsync());
    }

    [Fact]
    public async Task IngestAsync_RejectsTooManyItemsBeforeCreatingPending()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db, Now);
        var request = Request(
            "batch-items-over",
            Now,
            Now.AddHours(1),
            events: BuildEvents(1_501));

        var error = await Assert.ThrowsAsync<MobileSyncBatchRequestValidationException>(
            () => service.IngestAsync(request, CancellationToken.None));

        Assert.Equal(40001, error.ErrorCode);
        Assert.False(await db.Set<MobileSyncBatchEntity>().AnyAsync());
    }

    [Fact]
    public async Task IngestAsync_RejectsEventOutsideDeclaredWindowBeforePending()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var request = Request(
            "batch-event-outside-window",
            Now,
            Now.AddHours(1),
            events:
            [
                new MobileUsageEventDto(
                    "com.example.lifecycle",
                    "USER_INTERACTION",
                    Now.AddHours(40),
                    null,
                    Now.AddHours(40),
                    "{}",
                    "event-outside-window")
            ]);

        var error = await Assert.ThrowsAsync<MobileSyncBatchRequestValidationException>(
            () => CreateService(db, Now).IngestAsync(request, CancellationToken.None));

        Assert.Equal(40001, error.ErrorCode);
        Assert.False(await db.Set<MobileSyncBatchEntity>().AnyAsync());
        Assert.False(await db.Set<MobileUsageEventEntity>().AnyAsync());
    }

    [Fact]
    public async Task IngestAsync_RejectsSummaryOutsideDeclaredWindowBeforePending()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var request = Request(
            "batch-summary-outside-window",
            Now,
            Now.AddHours(1),
            summaries:
            [
                Summary(
                    "com.example.daily",
                    Now,
                    Now.AddHours(24),
                    Now.AddHours(23),
                    "summary-outside-window")
            ]);

        var error = await Assert.ThrowsAsync<MobileSyncBatchRequestValidationException>(
            () => CreateService(db, Now).IngestAsync(request, CancellationToken.None));

        Assert.Equal(40001, error.ErrorCode);
        Assert.False(await db.Set<MobileSyncBatchEntity>().AnyAsync());
        Assert.False(await db.Set<MobileUsageSummaryEntity>().AnyAsync());
    }

    [Fact]
    public async Task IngestAsync_ActiveLeaseReplayReturnsRetryableNonZeroFailure()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var request = Request("batch-active", Now, Now.AddHours(1));
        var leaseId = Guid.NewGuid();
        var created = Now.AddMinutes(-40);
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                [],
                MobileUsageIngestService.ComputePayloadSha256(request),
                Now.AddMinutes(5),
                attemptCount: 2,
                leaseId: leaseId.ToString("N")),
            CreatedAt = created
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db, Now).IngestAsync(request, CancellationToken.None);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Empty(result.ItemResults);
        Assert.False(await db.Set<MobileAppCatalogEntity>().AnyAsync());
        db.ChangeTracker.Clear();
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(created, batch.CreatedAt);
        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
        Assert.Equal(2, envelope.AttemptCount);
        Assert.Equal(leaseId.ToString("N"), envelope.LeaseId);
    }

    [Fact]
    public async Task IngestAsync_ScheduledRetryWaitsForCooldownThenTakesOver()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var request = Request("batch-retry", Now, Now.AddHours(1));
        var created = Now.AddMinutes(-2);
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                ["processing-timeout"],
                MobileUsageIngestService.ComputePayloadSha256(request),
                leaseUntilUtc: null,
                attemptCount: 1,
                failureCode: "processing-timeout",
                leaseId: Guid.NewGuid().ToString("N"),
                retryAfterUtc: Now.AddMinutes(1)),
            CreatedAt = created
        });
        await db.SaveChangesAsync();

        var duringCooldown = await CreateService(db, Now)
            .IngestAsync(request, CancellationToken.None);
        Assert.Equal(1, duringCooldown.FailedCount);
        Assert.False(await db.Set<MobileAppCatalogEntity>().AnyAsync());

        var afterCooldown = await CreateService(db, Now.AddMinutes(2))
            .IngestAsync(request, CancellationToken.None);
        Assert.Equal(1, afterCooldown.AcceptedCount);
        db.ChangeTracker.Clear();
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(created, batch.CreatedAt);
        Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
        var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
        Assert.Equal(2, envelope.AttemptCount);
    }

    [Fact]
    public async Task IngestAsync_RecoverableFailureIsScheduledThenBoundedRetryCanSucceed()
    {
        MobileTestHelpers.RegisterMobileModule();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"mobile-lifecycle-retry-{Guid.NewGuid()}")
            .Options;
        await using var db = new ThrowOnceAfterPendingPimDbContext(options);
        var time = new MutableTimeProvider(Now);
        var service = CreateService(db, time);
        var request = Request("batch-recoverable", Now, Now.AddHours(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IngestAsync(request, CancellationToken.None));

        db.ChangeTracker.Clear();
        var afterFailure = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(MobileSyncBatchStatus.Pending, afterFailure.Status);
        Assert.Equal(Now, afterFailure.CreatedAt);
        Assert.Null(afterFailure.CompletedAtUtc);
        var failureEnvelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(afterFailure.ErrorJson);
        Assert.Equal(1, failureEnvelope.AttemptCount);
        Assert.Equal("processing-InvalidOperationException", failureEnvelope.FailureCode);
        Assert.NotNull(failureEnvelope.RetryAfterUtc);
        Assert.DoesNotContain("Synthetic recoverable", afterFailure.ErrorJson);

        var duringCooldown = await service.IngestAsync(request, CancellationToken.None);
        Assert.Equal(1, duringCooldown.FailedCount);
        Assert.False(await db.Set<MobileAppCatalogEntity>().AnyAsync());

        time.Advance(TimeSpan.FromMinutes(2));
        var recovered = await service.IngestAsync(request, CancellationToken.None);
        Assert.Equal(1, recovered.AcceptedCount);

        db.ChangeTracker.Clear();
        var completed = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(MobileSyncBatchStatus.Completed, completed.Status);
        Assert.Equal(Now, completed.CreatedAt);
        Assert.Single(await db.Set<MobileAppCatalogEntity>().ToListAsync());
        var completedEnvelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(completed.ErrorJson);
        Assert.Equal(2, completedEnvelope.AttemptCount);
        Assert.Null(completedEnvelope.FailureCode);
    }

    [Fact]
    public async Task IngestAsync_PayloadChangeForSameBatchReturnsConflictWithoutOverwrite()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db, Now);
        var original = Request("batch-conflict", Now, Now.AddHours(1));
        await service.IngestAsync(original, CancellationToken.None);
        var changed = original with
        {
            Apps =
            [
                original.Apps[0] with { DisplayName = "Changed display name" }
            ]
        };

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.IngestAsync(changed, CancellationToken.None));

        Assert.Equal(MobileSyncErrorCodes.PayloadConflict, error.ErrorCode);
        db.ChangeTracker.Clear();
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
        Assert.Equal("Lifecycle", (await db.Set<MobileAppCatalogEntity>().SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task IngestAsync_ExhaustedBatchReplayReturnsRecognizableConflict()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var request = Request("batch-exhausted", Now, Now.AddHours(1));
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            Status = MobileSyncBatchStatus.Failed,
            FailedCount = 1,
            ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                ["processing-timeout"],
                MobileUsageIngestService.ComputePayloadSha256(request),
                attemptCount: MobileUsageIngestService.MaxProcessingAttempts,
                failureCode: "processing-timeout",
                leaseId: Guid.NewGuid().ToString("N")),
            CreatedAt = Now.AddHours(-1),
            CompletedAtUtc = Now
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(
            () => CreateService(db, Now).IngestAsync(request, CancellationToken.None));

        Assert.Equal(MobileSyncErrorCodes.RetryExhausted, error.ErrorCode);
        Assert.False(await db.Set<MobileAppCatalogEntity>().AnyAsync());
    }

    [Fact]
    public async Task IngestAsync_UnrecordedFifthAttemptIsTerminalizedBeforeReprocessing()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var request = Request("batch-attempt-cap", Now, Now.AddHours(1));
        var created = Now.AddHours(-1);
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BatchId = request.BatchId,
            WindowStartUtc = request.WindowStartUtc,
            WindowEndUtc = request.WindowEndUtc,
            Status = MobileSyncBatchStatus.Pending,
            ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                [],
                ["processing-timeout"],
                MobileUsageIngestService.ComputePayloadSha256(request),
                leaseUntilUtc: null,
                attemptCount: MobileUsageIngestService.MaxProcessingAttempts,
                failureCode: "processing-timeout",
                leaseId: Guid.NewGuid().ToString("N"),
                retryAfterUtc: Now.AddMinutes(-1)),
            CreatedAt = created
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(
            () => CreateService(db, Now).IngestAsync(request, CancellationToken.None));

        Assert.Equal(MobileSyncErrorCodes.RetryExhausted, error.ErrorCode);
        db.ChangeTracker.Clear();
        var batch = await db.Set<MobileSyncBatchEntity>().SingleAsync();
        Assert.Equal(MobileSyncBatchStatus.Failed, batch.Status);
        Assert.Equal(created, batch.CreatedAt);
        Assert.Equal(1, batch.FailedCount);
        Assert.False(await db.Set<MobileAppCatalogEntity>().AnyAsync());
    }

    private static MobileUsageIngestService CreateService(PimDbContext db, DateTimeOffset now)
        => CreateService(db, MobileTestHelpers.Time(now));

    private static MobileUsageIngestService CreateService(PimDbContext db, TimeProvider time)
        => new(
            db,
            MobileTestHelpers.CurrentUser(),
            new MobileSessionInterpreter(db),
            time);

    private static MobileUsageEventsUploadRequest Request(
        string batchId,
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<MobileUsageEventDto>? events = null,
        IReadOnlyList<MobileUsageSummaryDto>? summaries = null)
        => new(
            DeviceId,
            batchId,
            start,
            end,
            [
                new MobileAppMetadataDto(
                    "com.example.lifecycle",
                    "Lifecycle",
                    "1.0",
                    1,
                    false,
                    "tools",
                    null,
                    null,
                    null,
                    "{}",
                    "app-lifecycle")
            ],
            events ?? [],
            summaries ?? []);

    private static List<MobileUsageEventDto> BuildEvents(int count)
        => Enumerable.Range(0, count)
            .Select(index => new MobileUsageEventDto(
                "com.example.lifecycle",
                "USER_INTERACTION",
                Now.AddSeconds(index * 30L),
                null,
                Now.AddSeconds(index * 30L),
                "{}",
                $"event-{index}"))
            .ToList();

    private static MobileUsageSummaryDto Summary(
        string packageName,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset lastUsed,
        string clientItemKey,
        long totalVisibleMs = 60_000)
        => new(
            packageName,
            start,
            end,
            totalVisibleMs,
            lastUsed,
            "usage-stats-fallback",
            "{}",
            clientItemKey);

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ThrowOnceAfterPendingPimDbContext(DbContextOptions<PimDbContext> options)
        : PimDbContext(options)
    {
        private int _saveCount;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCount++;
            if (_saveCount == 2)
                throw new InvalidOperationException("Synthetic recoverable processing failure.");

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
