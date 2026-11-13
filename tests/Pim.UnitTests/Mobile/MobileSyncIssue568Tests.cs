using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// #568：客户端断开后的批次收敛语义。
///
/// 事故形态：客户端 90 秒超时断开 → 服务端把 pending 行留着带 15 分钟租约 → 重投只拿到 202 空等；
/// 孤儿批次永远不收敛；每次超时还各烧掉一次处理配额。这些用例把三条修法各自钉住。
/// </summary>
public sealed class MobileSyncIssue568Tests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T03:08:31Z");

    [Fact]
    public async Task IngestAsync_ArchivedBatchAsksForANewBatchId()
    {
        var options = CreateOptions();
        var request = Request("android-archived", Now.AddHours(-20));
        await using (var seed = new PimDbContext(options))
        {
            seed.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
            {
                UserId = MobileTestHelpers.UserId,
                DeviceId = request.DeviceId,
                BatchId = request.BatchId,
                WindowStartUtc = request.WindowStartUtc,
                WindowEndUtc = request.WindowEndUtc,
                Status = MobileSyncBatchStatus.Abandoned,
                ErrorJson = MobileSyncBatchEnvelopeCodec.Serialize(
                    [],
                    [],
                    payloadSha256: null,
                    leaseUntilUtc: null,
                    attemptCount: 1,
                    failureCode: "abandoned-lease"),
                CreatedAt = Now.AddDays(-20)
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new PimDbContext(options);
        var error = await Assert.ThrowsAsync<DomainException>(
            () => CreateService(db).IngestAsync(request, CancellationToken.None));

        // 归档批次必须回 40903（客户端据此换批次标识），不能回放成 200 + 全零计数。
        Assert.Equal(MobileSyncErrorCodes.RetryExhausted, error.ErrorCode);
    }

    [Fact]
    public async Task ClientCancellation_ReleasesLeaseAndKeepsTheAttemptBudget()
    {
        var options = CreateOptions();
        var request = Request("android-client-abort", Now.AddHours(-18));

        // 客户端在 pending 行落库后立刻断开（生产表现为 HTTP 499）。
        using (var cts = new CancellationTokenSource())
        {
            await using var aborting = new AbortAfterFirstSaveDbContext(options, cts);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CreateService(aborting).IngestAsync(request, cts.Token));
            Assert.NotNull(error);
        }

        await using (var db = new PimDbContext(options))
        {
            var batch = await db.Set<MobileSyncBatchEntity>()
                .AsNoTracking()
                .SingleAsync(row => row.BatchId == request.BatchId);
            var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);

            Assert.Equal(MobileSyncBatchStatus.Pending, batch.Status);
            Assert.Null(envelope.LeaseUntilUtc);
            Assert.NotNull(envelope.RetryAfterUtc);
            Assert.Equal("processing-interrupted", envelope.FailureCode);
            // 被中断的这次尝试退回配额，否则连续几次超时就会把健康批次判死。
            Assert.Equal(0, envelope.AttemptCount);
            // 租约必须已经放开：不能留下一个没有 owner 的 in-flight 让重投等满 15 分钟。
            Assert.False(MobileSyncBatchEnvelopeCodec.HasActiveLease(batch.CreatedAt, batch.ErrorJson, Now));
            Assert.Equal(
                DateTimeOffset.MinValue,
                MobileSyncBatchEnvelopeCodec.EffectiveLeaseUntil(batch.CreatedAt, envelope));
        }

        // 重投立刻认领并跑完，且这一次仍算第 1 次尝试。
        await using (var db = new PimDbContext(options))
        {
            var result = await CreateService(db).IngestAsync(request, CancellationToken.None);
            Assert.Equal(0, result.FailedCount);
            Assert.Null(result.ProcessingState);
        }

        await using (var verify = new PimDbContext(options))
        {
            var batch = await verify.Set<MobileSyncBatchEntity>()
                .AsNoTracking()
                .SingleAsync(row => row.BatchId == request.BatchId);
            Assert.Equal(MobileSyncBatchStatus.Completed, batch.Status);
            var envelope = MobileSyncBatchEnvelopeCodec.DeserializeOrEmpty(batch.ErrorJson);
            Assert.Equal(1, envelope.AttemptCount);
        }
    }

    [Fact]
    public async Task IngestForUserAsync_WorksWithoutRequestIdentity()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileUsageIngestService(
            db,
            new AnonymousUser(),
            new MobileSessionInterpreter(db, MobileTestHelpers.Time(Now)),
            MobileTestHelpers.Time(Now));

        // 续跑在独立作用域里执行：身份必须由调用方显式传入（HttpContext 已经没了）。
        var result = await service.IngestForUserAsync(
            MobileTestHelpers.UserId,
            Request("android-continuation", Now.AddHours(-1)),
            CancellationToken.None);
        Assert.Equal(1, result.AcceptedCount);

        // 老的入口仍然要求请求身份，避免续跑路径被误用。
        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.IngestAsync(Request("android-anonymous", Now.AddHours(-1)), CancellationToken.None));
        Assert.Equal(6200, error.ErrorCode);
    }

    [Theory]
    [InlineData(null, MobileSyncIngestRunner.DefaultConcurrencyLimit)]
    [InlineData("", MobileSyncIngestRunner.DefaultConcurrencyLimit)]
    [InlineData("not-a-number", MobileSyncIngestRunner.DefaultConcurrencyLimit)]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    [InlineData("99", MobileSyncIngestRunner.MaxConcurrencyLimit)]
    public void ContinuationConcurrencyLimit_IsClamped(string? configured, int expected)
        => Assert.Equal(expected, MobileSyncIngestRunner.ResolveConcurrencyLimit(configured));

    private static DbContextOptions<PimDbContext> CreateOptions()
    {
        MobileTestHelpers.RegisterMobileModule();
        return new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"issue568-{Guid.NewGuid()}")
            .Options;
    }

    private static MobileUsageIngestService CreateService(PimDbContext db) => new(
        db,
        MobileTestHelpers.CurrentUser(),
        new MobileSessionInterpreter(db, MobileTestHelpers.Time(Now)),
        MobileTestHelpers.Time(Now));

    private static MobileUsageEventsUploadRequest Request(string batchId, DateTimeOffset windowStart) => new(
        "android-issue-568",
        batchId,
        windowStart,
        windowStart.AddMinutes(15),
        [],
        [
            new MobileUsageEventDto(
                "com.example.messages",
                "MOVE_TO_FOREGROUND",
                windowStart.AddMinutes(1),
                "MainActivity",
                windowStart.AddMinutes(1),
                "{}",
                "item-1")
        ],
        []);

    private sealed class AnonymousUser : ICurrentUserService
    {
        public Guid? UserId => null;

        public string? Role => null;
    }

    /// <summary>pending 行提交之后客户端断开：模拟 OkHttp callTimeout 打在入库中途。</summary>
    private sealed class AbortAfterFirstSaveDbContext(
        DbContextOptions<PimDbContext> options,
        CancellationTokenSource abort) : PimDbContext(options)
    {
        private int _saves;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var written = await base.SaveChangesAsync(cancellationToken);
            if (Interlocked.Increment(ref _saves) == 1)
                abort.Cancel();
            return written;
        }
    }
}
