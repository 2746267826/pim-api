using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileLocationServiceTests
{
    [Fact]
    public async Task SubmitAsync_AcceptsAccuracyUnderFiftyMetersAsUsable()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var point = await service.SubmitAsync(Request(49.9), CancellationToken.None);

        Assert.Equal("usable", point.Quality);
        Assert.Equal(1, await db.Set<MobileLocationPointEntity>().CountAsync());
    }

    [Fact]
    public async Task SubmitAsync_RejectsFiftyMeterAccuracy()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SubmitAsync(Request(50), CancellationToken.None));

        Assert.Equal(6202, error.ErrorCode);
    }

    [Fact]
    public async Task SubmitAsync_RejectsAccuracyGreaterThanFiftyMeters()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SubmitAsync(Request(50.01), CancellationToken.None));

        Assert.Equal(6202, error.ErrorCode);
        var rejected = Assert.Single(await db.Set<MobileLocationPointEntity>().ToListAsync());
        Assert.Equal("rejected", rejected.Quality);
        Assert.Equal(50.01m, rejected.HorizontalAccuracyMeters);
    }

    [Fact]
    public async Task SubmitAsync_RejectsInvalidCoordinates()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var request = Request(10) with { Latitude = 91 };
        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SubmitAsync(request, CancellationToken.None));

        Assert.Equal(6201, error.ErrorCode);
    }

    [Fact]
    public async Task SubmitAsync_AcceptsNullAltitudeWithQualityFlagInRawJson()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));
        const string rawJson = "{\"qualityFlags\":[\"altitude-missing-timeout\"]}";

        var point = await service.SubmitAsync(
            Request(18) with
            {
                AltitudeMeters = null,
                RawJson = rawJson
            },
            CancellationToken.None);

        Assert.Null(point.AltitudeMeters);
        Assert.Equal(rawJson, point.RawJson);
    }

    [Fact]
    public async Task GetHistoryAsync_ExcludesRejectedAndNonStrictAccuracyPoints()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        var accepted = await service.SubmitAsync(Request(49.9), CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(() => service.SubmitAsync(Request(50), CancellationToken.None));
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            RecordedAtUtc = DateTimeOffset.Parse("2026-07-06T11:59:00Z"),
            Latitude = 31.230416m,
            Longitude = 121.473701m,
            HorizontalAccuracyMeters = 50m,
            Provider = "gps",
            Source = "manual",
            RawJson = "{}",
            Quality = "usable",
            CreatedAt = DateTimeOffset.Parse("2026-07-06T12:00:00Z")
        });
        await db.SaveChangesAsync();

        var history = await service.GetHistoryAsync(
            "android-main",
            DateTimeOffset.Parse("2026-07-06T11:00:00Z"),
            DateTimeOffset.Parse("2026-07-06T12:30:00Z"),
            50,
            CancellationToken.None);

        var point = Assert.Single(history);
        Assert.Equal(accepted.Id, point.Id);
        Assert.Equal("usable", point.Quality);
        Assert.True(point.HorizontalAccuracyMeters < 50);
    }

    // ---------------------------------------------------------------- #246 重复点与批量通道

    [Fact]
    public async Task SubmitAsync_IsIdempotentForTheSameNaturalKey()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);

        var first = await service.SubmitAsync(Request(12), CancellationToken.None);
        var second = await service.SubmitAsync(Request(12), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        var point = Assert.Single(await db.Set<MobileLocationPointEntity>().ToListAsync());
        Assert.Equal(first.Id, point.Id);
    }

    [Fact]
    public async Task SubmitAsync_UpgradesRejectedPointWhenABetterAccuracyArrives()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DomainException>(() => service.SubmitAsync(Request(120), CancellationToken.None));
        var rejected = Assert.Single(await db.Set<MobileLocationPointEntity>().ToListAsync());
        Assert.Equal("rejected", rejected.Quality);

        var accepted = await service.SubmitAsync(Request(12), CancellationToken.None);

        Assert.Equal(rejected.Id, accepted.Id);
        Assert.Equal("usable", accepted.Quality);
        var point = Assert.Single(await db.Set<MobileLocationPointEntity>().ToListAsync());
        Assert.Equal("usable", point.Quality);
        Assert.Equal(12m, point.HorizontalAccuracyMeters);
    }

    [Fact]
    public async Task SubmitBatchAsync_AcceptsNewPointsAndDeduplicatesWithinTheSameBatch()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = new MobileLocationPointsUploadRequest([Request(12), Request(12), Request(12) with { Longitude = 121.5 }]);

        var result = await service.SubmitBatchAsync(request, CancellationToken.None);

        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(2, await db.Set<MobileLocationPointEntity>().CountAsync());
        var duplicate = Assert.Single(result.ItemResults.Where(item => item.Outcome == "skipped"));
        Assert.Equal("duplicate", duplicate.Code);
        Assert.Equal(MobileLocationService.LocationPointEntityType, duplicate.EntityType);
        Assert.All(result.ItemResults, item => Assert.False(string.IsNullOrWhiteSpace(item.ClientItemKey)));
    }

    [Fact]
    public async Task SubmitBatchAsync_ReportsBadPointsWithoutFailingTheWholeBatch()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = new MobileLocationPointsUploadRequest(
        [
            Request(12),
            Request(120) with { Longitude = 121.6 },
            Request(12) with { Latitude = 91, Longitude = 200 },
            Request(12) with { DeviceId = " " }
        ]);

        var result = await service.SubmitBatchAsync(request, CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(3, result.RejectedCount);
        Assert.Contains(result.ItemResults, item => item.Code == "unusable-accuracy");
        Assert.Contains(result.ItemResults, item => item.Code == "invalid-coordinates");
        Assert.Contains(result.ItemResults, item => item.Code == "invalid-device-id");
        // 精度不够的点仍然落库（与单点接口一致），坐标/设备非法的点不落库。
        var stored = await db.Set<MobileLocationPointEntity>().ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(1, stored.Count(point => point.Quality == "usable"));
        Assert.Equal(1, stored.Count(point => point.Quality == "rejected"));
    }

    [Fact]
    public async Task SubmitBatchAsync_IsIdempotentAcrossBatches()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var request = new MobileLocationPointsUploadRequest([Request(12), Request(12) with { Longitude = 121.5 }]);

        var first = await service.SubmitBatchAsync(request, CancellationToken.None);
        var second = await service.SubmitBatchAsync(request, CancellationToken.None);

        Assert.Equal(2, first.AcceptedCount);
        Assert.Equal(0, second.AcceptedCount);
        Assert.Equal(2, second.SkippedCount);
        Assert.Equal(2, await db.Set<MobileLocationPointEntity>().CountAsync());
    }

    [Fact]
    public async Task SubmitBatchAsync_RejectsBatchesLargerThanTheLimit()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateService(db);
        var points = Enumerable.Range(0, 1001)
            .Select(index => Request(12) with { RecordedAtUtc = DateTimeOffset.Parse("2026-07-06T11:58:00Z").AddSeconds(index) })
            .ToList();

        var error = await Assert.ThrowsAsync<DomainException>(() => service.SubmitBatchAsync(
            new MobileLocationPointsUploadRequest(points),
            CancellationToken.None));

        Assert.Equal(6203, error.ErrorCode);
        Assert.Empty(await db.Set<MobileLocationPointEntity>().ToListAsync());
    }

    [Fact]
    public async Task LocationPointNaturalKey_HasAUniqueIndexInTheRelationalModel()
    {
        // 迁移会先清理历史重复行再建这个唯一索引；这里用关系型库确认约束真的存在。
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        var service = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

        await service.SubmitAsync(Request(12), CancellationToken.None);
        db.ChangeTracker.Clear();
        db.Set<MobileLocationPointEntity>().Add(new MobileLocationPointEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            RecordedAtUtc = DateTimeOffset.Parse("2026-07-06T11:58:00Z"),
            Latitude = 31.230416m,
            Longitude = 121.473701m,
            HorizontalAccuracyMeters = 12m,
            Provider = "gps",
            Source = "manual",
            RawJson = "{}",
            Quality = "usable",
            CreatedAt = DateTimeOffset.Parse("2026-07-06T12:00:00Z")
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static MobileLocationService CreateService(PimDbContext db) => new(
        db,
        MobileTestHelpers.CurrentUser(),
        MobileTestHelpers.Time(DateTimeOffset.Parse("2026-07-06T12:00:00Z")));

    private static MobileLocationPointRequest Request(double accuracy) => new(
        "android-main",
        DateTimeOffset.Parse("2026-07-06T11:58:00Z"),
        31.230416,
        121.473701,
        accuracy,
        "gps",
        "manual",
        4.2,
        6.0,
        1.1,
        0.5,
        90,
        1.5,
        false,
        "{\"provider\":\"gps\"}");
}
