using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Pim.Core.Caching;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileLocationCorrectionServiceTests
{
    private const string DeviceId = "pixel-8";
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-11-25T12:00:00Z");

    [Fact]
    public async Task CorrectAsync_PersistsAppliedCoordinateAndKeepsCapturedFactsAndAudit()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(
            db,
            Id(1),
            Now.AddHours(-1),
            31.230416,
            121.473701);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var result = await service.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(
                Latitude: 31.2401234,
                Longitude: 121.4805678,
                Revision: 0,
                Excluded: false,
                Reason: "GPS 漂移"),
            CancellationToken.None);

        Assert.Equal(1, result.Point.Revision);
        Assert.Equal(31.2401234, result.Point.Latitude, 7);
        Assert.Equal(121.4805678, result.Point.Longitude, 7);
        Assert.Equal(31.230416, result.Point.OriginalLatitude!.Value, 7);
        Assert.Equal(121.473701, result.Point.OriginalLongitude!.Value, 7);
        Assert.True(result.Point.IsCorrected);

        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().SingleAsync();
        Assert.Equal(31.2401234m, stored.Latitude);
        Assert.Equal(121.4805678m, stored.Longitude);
        Assert.Equal(31.230416m, stored.OriginalLatitude!.Value);
        Assert.Equal(121.473701m, stored.OriginalLongitude!.Value);
        Assert.Equal("gps", stored.Provider);
        Assert.Equal("auto", stored.Source);
        Assert.Equal(12m, stored.HorizontalAccuracyMeters);
        Assert.Equal(Now.AddHours(-1), stored.RecordedAtUtc);

        var revision = Assert.Single(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Equal("correct", revision.Operation);
        Assert.Equal(0, revision.RevisionBefore);
        Assert.Equal(1, revision.RevisionAfter);
        Assert.Equal("GPS 漂移", revision.Reason);
        Assert.Equal(MobileTestHelpers.UserId, revision.ActorUserId);

        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal("mobile.location.correct", audit.Action);
        Assert.Equal("mobile-location-point", audit.ResourceType);
        Assert.Contains("revisionAfter", audit.MetadataJson);
    }

    [Fact]
    public async Task CorrectAsync_InvalidCoordinatesDoesNotMutateOrAudit()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(2), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(Latitude: 91, Longitude: 121.473701, Revision: 0),
            CancellationToken.None));

        Assert.Equal(6201, error.ErrorCode);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().SingleAsync();
        Assert.Equal(31.230416m, stored.Latitude);
        Assert.Equal(121.473701m, stored.Longitude);
        Assert.Equal(0, stored.Revision);
        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task CorrectAsync_OldRevisionIsRejectedButSameTargetRetryIsIdempotent()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(3), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;
        var request = new MobileLocationPointCorrectionRequest(
            Latitude: 31.2401234,
            Longitude: 121.4805678,
            Revision: 0,
            Excluded: false,
            Reason: "首次保存");

        var first = await service.CorrectAsync(point.Id, request, CancellationToken.None);
        var retried = await service.CorrectAsync(point.Id, request, CancellationToken.None);

        Assert.Equal(first.Point.Id, retried.Point.Id);
        Assert.Equal(1, retried.Point.Revision);
        Assert.Single(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Single(await db.AuditLogs.ToListAsync());

        var conflict = await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(
            point.Id,
            request with { Latitude = 31.25, Longitude = 121.49 },
            CancellationToken.None));
        Assert.Equal(6204, conflict.ErrorCode);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().SingleAsync();
        Assert.Equal(31.2401234m, stored.Latitude);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task RestoreAsync_RestoresOriginalCoordinateAndRecordsRevision()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(4), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;
        await service.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(31.25, 121.49, 0),
            CancellationToken.None);

        var restored = await service.RestoreAsync(
            point.Id,
            new MobileLocationPointRestoreRequest(1, "恢复采集值"),
            CancellationToken.None);

        Assert.Equal(31.230416, restored.Point.Latitude, 7);
        Assert.Equal(121.473701, restored.Point.Longitude, 7);
        Assert.False(restored.Point.IsCorrected);
        Assert.Equal(2, restored.Point.Revision);
        var revisions = await db.Set<MobileLocationPointRevisionEntity>()
            .OrderBy(revision => revision.CreatedAt)
            .ToListAsync();
        Assert.Contains(revisions, revision => revision.Operation == "correct");
        Assert.Contains(revisions, revision => revision.Operation == "restore");
        Assert.Contains(await db.AuditLogs.ToListAsync(), audit => audit.Action == "mobile.location.restore");
    }

    [Fact]
    public async Task SetExclusionAsync_IsAtomicWhenOneRevisionIsStale()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var first = SeedPoint(db, Id(5), Now, 31.230416, 121.473701);
        var second = SeedPoint(db, Id(6), Now.AddMinutes(1), 31.230516, 121.473801);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var error = await Assert.ThrowsAsync<DomainException>(() => service.SetExclusionAsync(
            new MobileLocationPointExclusionRequest(
                [
                    new MobileLocationPointExclusionItem(first.Id, 0),
                    new MobileLocationPointExclusionItem(second.Id, 99)
                ],
                Excluded: true),
            CancellationToken.None));

        Assert.Equal(6204, error.ErrorCode);
        db.ChangeTracker.Clear();
        var points = await db.Set<MobileLocationPointEntity>().OrderBy(point => point.Id).ToListAsync();
        Assert.All(points, point => Assert.False(point.IsExcluded));
        Assert.All(points, point => Assert.Equal(0, point.Revision));
        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SetExclusionAsync_ExcludesAndRestoresPointsWithRevisionAndAudit()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(7), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var excluded = await service.SetExclusionAsync(
            new MobileLocationPointExclusionRequest(
                [new MobileLocationPointExclusionItem(point.Id, 0)],
                Excluded: true,
                Reason: "误报"),
            CancellationToken.None);

        Assert.Equal(1, excluded.UpdatedCount);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().SingleAsync();
        Assert.True(stored.IsExcluded);
        Assert.Equal(1, stored.Revision);
        Assert.NotNull(stored.ExcludedAtUtc);
        Assert.Equal(MobileTestHelpers.UserId, stored.ExcludedByUserId);

        var restored = await service.SetExclusionAsync(
            new MobileLocationPointExclusionRequest(
                [new MobileLocationPointExclusionItem(point.Id, 1)],
                Excluded: false),
            CancellationToken.None);

        Assert.Equal(1, restored.UpdatedCount);
        db.ChangeTracker.Clear();
        stored = await db.Set<MobileLocationPointEntity>().SingleAsync();
        Assert.False(stored.IsExcluded);
        Assert.Equal(2, stored.Revision);
        Assert.Null(stored.ExcludedAtUtc);
        Assert.Equal(2, await db.Set<MobileLocationPointRevisionEntity>().CountAsync());
        var actions = await db.AuditLogs.Select(audit => audit.Action).ToListAsync();
        Assert.Contains("mobile.location.exclude", actions);
        Assert.Contains("mobile.location.include", actions);
    }

    [Fact]
    public async Task GetPointsAsync_ExcludesExcludedPointsUnlessExplicitlyRequested()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var included = SeedPoint(db, Id(8), Now, 31.230416, 121.473701);
        var excluded = SeedPoint(db, Id(9), Now.AddMinutes(1), 31.230516, 121.473801);
        excluded.IsExcluded = true;
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;
        var request = new MobileManagedLocationQueryRequest(
            RangeStartUtc: Now.AddHours(-1),
            RangeEndUtc: Now.AddHours(1),
            Timezone: "Asia/Shanghai",
            DeviceId: DeviceId,
            SegmentId: null,
            IncludeExcluded: false,
            Cursor: null,
            PageSize: 50);

        var visible = await service.GetPointsAsync(request, CancellationToken.None);
        var withExcluded = await service.GetPointsAsync(
            request with { IncludeExcluded = true },
            CancellationToken.None);

        Assert.Equal(new[] { included.Id }, visible.Items.Select(point => point.Id));
        Assert.Equal(
            new[] { included.Id, excluded.Id }.OrderBy(id => id),
            withExcluded.Items.Select(point => point.Id).OrderBy(id => id));
        Assert.True(withExcluded.Items.Single(point => point.Id == excluded.Id).IsExcluded);
    }

    [Fact]
    public async Task ExclusionAndRestoreRecomputeTracksAndOverview()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var first = SeedPoint(db, Id(10), Now, 31.230416, 121.473701);
        var second = SeedPoint(db, Id(11), Now.AddMinutes(5), 31.234016, 121.473701);
        await db.SaveChangesAsync();
        var services = CreateServices(db);
        var query = new MobileLocationQueryRequest(
            RangeStartUtc: Now.AddHours(-1),
            RangeEndUtc: Now.AddHours(1),
            Timezone: "Asia/Shanghai");

        var before = Assert.Single(await services.Aggregation.GetTracksAsync(query, CancellationToken.None));
        Assert.Equal(2, before.Segments.SelectMany(segment => segment.Path).Count());
        await services.Correction.SetExclusionAsync(
            new MobileLocationPointExclusionRequest(
                [new MobileLocationPointExclusionItem(second.Id, 0)],
                Excluded: true),
            CancellationToken.None);

        var after = Assert.Single(await services.Aggregation.GetTracksAsync(query, CancellationToken.None));
        Assert.Single(after.Segments.SelectMany(segment => segment.Path));
        Assert.Equal(first.Id.ToString(), after.Segments.SelectMany(segment => segment.Path).Single().Id);
        var overview = await services.Aggregation.GetOverviewAsync(query, CancellationToken.None);
        Assert.Equal(1, overview.PointCount);
        Assert.Equal(1, overview.UsablePointCount);
    }

    [Fact]
    public async Task DeleteAsync_HidesPointPurgesRevisionsAndPreventsLateUploadFromResurrectingIt()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(12), Now, 31.230416, 121.473701);
        db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = MobileTestHelpers.UserId,
            PointId = point.Id,
            DeviceId = DeviceId,
            Operation = "correct",
            RevisionBefore = 0,
            RevisionAfter = 0,
            OriginalLatitude = 31.230416m,
            OriginalLongitude = 121.473701m,
            NewLatitude = 31.24m,
            NewLongitude = 121.48m,
            Reason = "删除前的原因",
            ActorUserId = MobileTestHelpers.UserId,
            CreatedAt = Now.AddMinutes(-5)
        });
        db.Set<MobileLocationSegmentMergeEntity>().Add(new MobileLocationSegmentMergeEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BeforePointId = point.Id,
            AfterPointId = point.Id,
            Kind = "move",
            ActorUserId = MobileTestHelpers.UserId,
            CreatedAt = Now.AddMinutes(-4)
        });
        await db.SaveChangesAsync();
        var correction = CreateServices(db).Correction;
        var location = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var deleted = await correction.DeleteAsync(point.Id, 0, CancellationToken.None);

        Assert.Equal(point.Id, deleted.PointId);
        Assert.Empty(await location.GetHistoryAsync(
            DeviceId,
            Now.AddHours(-1),
            Now.AddHours(1),
            50,
            CancellationToken.None));
        var duplicate = await location.SubmitAsync(LocationRequest(), CancellationToken.None);
        Assert.Equal(point.Id, duplicate.Id);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(stored.DeletedAtUtc);
        Assert.Equal(1, stored.Revision);

        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Empty(await db.Set<MobileLocationSegmentMergeEntity>().ToListAsync());
        var revisionsError = await Assert.ThrowsAsync<DomainException>(
            () => correction.GetRevisionsAsync(point.Id, CancellationToken.None));
        Assert.Equal(40401, revisionsError.ErrorCode);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(
            () => correction.CorrectAsync(
                point.Id,
                new MobileLocationPointCorrectionRequest(31.25, 121.49, 1),
                CancellationToken.None))).ErrorCode);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(
            () => correction.RestoreAsync(
                point.Id,
                new MobileLocationPointRestoreRequest(1),
                CancellationToken.None))).ErrorCode);

        var managed = await correction.GetPointsAsync(
            new MobileManagedLocationQueryRequest(
                Now.AddHours(-1),
                Now.AddHours(1),
                "Asia/Shanghai",
                DeviceId,
                null,
                true,
                null,
                50),
            CancellationToken.None);
        Assert.Empty(managed.Items);
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.DoesNotContain("latitude", audit.MetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", audit.MetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recordedAtUtc", audit.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorrectedPoint_IsNotReinsertedWhenTheCaptureQueueRetriesOriginalCoordinates()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(17), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var correction = CreateServices(db).Correction;
        await correction.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(31.25, 121.49, 0),
            CancellationToken.None);
        var location = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var duplicate = await location.SubmitAsync(LocationRequest(), CancellationToken.None);

        Assert.Equal(point.Id, duplicate.Id);
        db.ChangeTracker.Clear();
        var stored = Assert.Single(await db.Set<MobileLocationPointEntity>().ToListAsync());
        Assert.Equal(31.25m, stored.Latitude);
        Assert.Equal(121.49m, stored.Longitude);
        Assert.Equal(31.230416m, stored.OriginalLatitude!.Value);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task CorrectedPoint_IsNotReinsertedWhenABatchRetriesOriginalCoordinates()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(18), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var correction = CreateServices(db).Correction;
        await correction.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(31.25, 121.49, 0),
            CancellationToken.None);
        var location = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        var retried = await location.SubmitBatchAsync(
            new MobileLocationPointsUploadRequest([LocationRequest()]),
            CancellationToken.None);

        Assert.Equal(0, retried.AcceptedCount);
        Assert.Equal(1, retried.SkippedCount);
        Assert.Equal("duplicate", Assert.Single(retried.ItemResults).Code);
        db.ChangeTracker.Clear();
        var stored = Assert.Single(await db.Set<MobileLocationPointEntity>().ToListAsync());
        Assert.Equal(point.Id, stored.Id);
        Assert.Equal(31.25m, stored.Latitude);
        Assert.Equal(121.49m, stored.Longitude);
    }

    [Fact]
    public async Task ExcludedPoint_IsHiddenFromTheLegacyReadOnlyHistoryPath()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(19), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var correction = CreateServices(db).Correction;
        var location = new MobileLocationService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        await correction.SetExclusionAsync(
            new MobileLocationPointExclusionRequest(
                [new MobileLocationPointExclusionItem(point.Id, 0)],
                Excluded: true),
            CancellationToken.None);

        Assert.Empty(await location.GetHistoryAsync(
            DeviceId,
            Now.AddHours(-1),
            Now.AddHours(1),
            50,
            CancellationToken.None));
    }

    [Fact]
    public async Task FillAsync_AddsManualFactAndRejectsDuplicateNaturalKeyOnRelationalStore()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, DeviceId);
        await db.SaveChangesAsync();
        // 本用例只验证自然键冲突与回滚；SQLite 不支持 DateTimeOffset 比较，
        // 因此把同步重算替换成记录器，重算本身由 InMemory/PostgreSQL 翻译用例覆盖。
        var service = CreateServices(
            db,
            audit: new RecordingAuditLog(),
            recalculation: new RecordingRecalculationService()).Correction;
        var request = new MobileLocationFillRequest(
            DeviceId,
            Now.AddMinutes(-2),
            31.230416,
            121.473701,
            HorizontalAccuracyMeters: 12,
            Reason: "定位缺失补点");

        var filled = await service.FillAsync(request, CancellationToken.None);

        Assert.Equal("manual", filled.Point.Provider);
        Assert.Equal("manual-fill", filled.Point.SourceKind);
        Assert.Equal("usable", filled.Point.Quality);
        var duplicate = await Assert.ThrowsAsync<DomainException>(
            () => service.FillAsync(request, CancellationToken.None));
        Assert.Equal(6205, duplicate.ErrorCode);
    }

    [Fact]
    public async Task CorrectAsync_RejectsDuplicateNaturalKeyOnRelationalStore()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedPoint(db, Id(20), Now, 31.230416, 121.473701);
        var other = SeedPoint(db, Id(21), Now, 31.230416, 121.481000);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var duplicate = await Assert.ThrowsAsync<DomainException>(
            () => service.CorrectAsync(
                other.Id,
                new MobileLocationPointCorrectionRequest(
                    Latitude: 31.230416,
                    Longitude: 121.473701,
                    Revision: 0),
                CancellationToken.None));

        Assert.Equal(6205, duplicate.ErrorCode);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().SingleAsync(point => point.Id == other.Id);
        Assert.Equal(121.481000m, stored.Longitude);
        Assert.Equal(0, stored.Revision);
        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
    }

    [Fact]
    public async Task MergeAdjacentSegmentsAsync_MergesAndRecomputesOverview()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, Id(13), Now, 31.230416, 121.473701);
        SeedPoint(db, Id(14), Now.AddMinutes(12), 31.230800, 121.473701);
        SeedPoint(db, Id(15), Now.AddMinutes(25), 31.260000, 121.510000);
        SeedPoint(db, Id(16), Now.AddMinutes(40), 31.260500, 121.510500);
        await db.SaveChangesAsync();
        var services = CreateServices(db);
        var query = new MobileLocationQueryRequest(
            RangeStartUtc: Now.AddHours(-1),
            RangeEndUtc: Now.AddHours(1),
            Timezone: "Asia/Shanghai");
        var before = Assert.Single(await services.Aggregation.GetTracksAsync(query, CancellationToken.None));
        Assert.Equal(3, before.SegmentCount);
        var between = before.Segments[0].Id;
        var next = before.Segments[1].Id;

        var merged = await services.Correction.MergeAdjacentSegmentsAsync(
            new MobileLocationSegmentMergeRequest(
                [between, next],
                "move",
                query.RangeStartUtc!.Value,
                query.RangeEndUtc!.Value,
                Timezone: query.Timezone,
                DeviceId: DeviceId),
            CancellationToken.None);

        Assert.Equal("move", merged.Kind);
        var after = Assert.Single(await services.Aggregation.GetTracksAsync(query, CancellationToken.None));
        Assert.Equal(2, after.SegmentCount);
        Assert.Equal("move", after.Segments[0].Kind);
        Assert.Equal(3, after.Segments[0].PointCount);
        Assert.Equal(1, (await services.Aggregation.GetOverviewAsync(query, CancellationToken.None)).StayCount);
    }

    [Fact]
    public async Task SavedPlaceName_ReusesAcrossDateAndDeviceFilters()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedCluster(db, 100, Now.AddHours(2), DeviceId);
        SeedCluster(db, 200, Now.AddDays(1).AddHours(2), "pixel-9");
        await db.SaveChangesAsync();
        var services = CreateServices(db);
        var savedPlace = services.SavedPlace;
        var created = await savedPlace.CreateAsync(
            new MobileSavedPlaceCreateRequest(
                Name: "家",
                Type: "home",
                CenterLatitude: 31.230416,
                CenterLongitude: 121.473701,
                RadiusMeters: 120),
            CancellationToken.None);

        var dayOne = await services.FrequentPlace.GetFrequentPlacesAsync(
            Query(Now.AddHours(1), Now.AddHours(4)),
            CancellationToken.None);
        var dayTwo = await services.FrequentPlace.GetFrequentPlacesAsync(
            Query(Now.AddDays(1).AddHours(1), Now.AddDays(1).AddHours(4), "pixel-9"),
            CancellationToken.None);

        Assert.Equal("家", Assert.Single(dayOne.Places).Name);
        Assert.Equal(created.Id, dayTwo.Places.Single().SavedPlaceId);
        Assert.Equal("家", dayTwo.Places.Single().Name);
    }

    [Fact]
    public async Task SavedPlaceRetry_WithSameValuesAndOldRevisionIsIdempotent()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = CreateServices(db).SavedPlace;
        var created = await service.CreateAsync(
            new MobileSavedPlaceCreateRequest("家", "home", 31.230416, 121.473701, 120),
            CancellationToken.None);
        var updated = await service.UpdateAsync(
            created.Id,
            new MobileSavedPlaceUpdateRequest("住所", "home", 1),
            CancellationToken.None);

        var retried = await service.UpdateAsync(
            created.Id,
            new MobileSavedPlaceUpdateRequest("住所", "home", 1),
            CancellationToken.None);

        Assert.Equal(2, updated.Revision);
        Assert.Equal(2, retried.Revision);
        Assert.Equal("住所", retried.Name);
    }

    [Fact]
    public async Task UserIsolation_RejectsReadsAndWritesForAnotherOwnersPoint()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var other = SeedPoint(db, Id(300), Now, 31.230416, 121.473701, userId: Guid.NewGuid());
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var points = await service.GetPointsAsync(
            new MobileManagedLocationQueryRequest(
                Now.AddHours(-1),
                Now.AddHours(1),
                "Asia/Shanghai",
                DeviceId,
                null,
                true,
                null,
                50),
            CancellationToken.None);
        Assert.Empty(points.Items);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(
            other.Id,
            new MobileLocationPointCorrectionRequest(31.24, 121.48, 0),
            CancellationToken.None))).ErrorCode);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(() => service.GetRevisionsAsync(
            other.Id,
            CancellationToken.None))).ErrorCode);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(() => service.DeleteAsync(
            other.Id,
            0,
            CancellationToken.None))).ErrorCode);
    }

    [Fact]
    public async Task UserIsolation_HidesAndRejectsAnotherOwnersSavedPlace()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var other = new MobileSavedPlaceEntity
        {
            UserId = Guid.NewGuid(),
            Name = "别人的地点",
            PlaceType = "home",
            CenterLatitude = 31.230416m,
            CenterLongitude = 121.473701m,
            RadiusMeters = 100,
            Revision = 1,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.Set<MobileSavedPlaceEntity>().Add(other);
        await db.SaveChangesAsync();
        var service = CreateServices(db).SavedPlace;

        Assert.Empty(await service.ListAsync(CancellationToken.None));
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(
            other.Id,
            new MobileSavedPlaceUpdateRequest("改名", "home", 1),
            CancellationToken.None))).ErrorCode);
    }

    [Fact]
    public async Task DeviceMerge_MovesCorrectionRowsToTheTargetDevice()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, "source-device");
        SeedDevice(db, "target-device");
        db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = MobileTestHelpers.UserId,
            PointId = Id(400),
            DeviceId = "source-device",
            Operation = "correct",
            RevisionBefore = 0,
            RevisionAfter = 1,
            ActorUserId = MobileTestHelpers.UserId,
            CreatedAt = Now
        });
        db.Set<MobileLocationSegmentMergeEntity>().Add(new MobileLocationSegmentMergeEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "source-device",
            BeforePointId = Id(401),
            AfterPointId = Id(401),
            Kind = "move",
            ActorUserId = MobileTestHelpers.UserId,
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        var service = new DeviceManagementService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        await service.MergeAsync(["source-device"], "target-device", CancellationToken.None);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>()
            .Where(row => row.DeviceId == "source-device")
            .ToListAsync());
        Assert.Empty(await db.Set<MobileLocationSegmentMergeEntity>()
            .Where(row => row.DeviceId == "source-device")
            .ToListAsync());
        Assert.Single(await db.Set<MobileLocationPointRevisionEntity>()
            .Where(row => row.DeviceId == "target-device")
            .ToListAsync());
        Assert.Single(await db.Set<MobileLocationSegmentMergeEntity>()
            .Where(row => row.DeviceId == "target-device")
            .ToListAsync());
    }

    [Fact]
    public async Task DeviceDelete_RemovesCorrectionRowsSoNoOrphansSurvive()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        SeedDevice(db, DeviceId);
        db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = MobileTestHelpers.UserId,
            PointId = Id(410),
            DeviceId = DeviceId,
            Operation = "exclude",
            RevisionBefore = 0,
            RevisionAfter = 1,
            ActorUserId = MobileTestHelpers.UserId,
            CreatedAt = Now
        });
        db.Set<MobileLocationSegmentMergeEntity>().Add(new MobileLocationSegmentMergeEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = DeviceId,
            BeforePointId = Id(411),
            AfterPointId = Id(411),
            Kind = "stay",
            ActorUserId = MobileTestHelpers.UserId,
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        var service = new DeviceManagementService(
            db,
            MobileTestHelpers.CurrentUser(),
            MobileTestHelpers.Time(Now));

        await service.DeleteAsync(DeviceId, CancellationToken.None);

        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Empty(await db.Set<MobileLocationSegmentMergeEntity>().ToListAsync());
    }

    [Fact]
    public async Task GetPointsAsync_CursorPaginatesWithStableKeysetOrdering()
    {
        await using var db = MobileTestHelpers.CreateDb();
        for (var index = 0; index < 5; index++)
        {
            SeedPoint(
                db,
                Id(500 + index),
                Now,
                31.230416 + index * 0.0001,
                121.473701);
        }

        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;
        var request = new MobileManagedLocationQueryRequest(
            Now.AddHours(-1),
            Now.AddHours(1),
            "Asia/Shanghai",
            DeviceId,
            null,
            true,
            null,
            2);

        var collected = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await service.GetPointsAsync(request with { Cursor = cursor }, CancellationToken.None);
            pages++;
            collected.AddRange(page.Items.Select(point => point.Id));
            Assert.True(page.Items.Count <= 2);
            cursor = page.NextCursor;
            if (!page.HasMore)
                Assert.Null(cursor);
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(5, collected.Distinct().Count());
        Assert.Equal(
            Enumerable.Range(0, 5).Select(index => Id(500 + index)).OrderBy(id => id),
            collected.OrderBy(id => id));

        var firstPage = await service.GetPointsAsync(request, CancellationToken.None);
        Assert.True(firstPage.HasMore);
        var legacyCursor = firstPage.Items[^1].Id.ToString();
        var legacyPage = await service.GetPointsAsync(
            request with { Cursor = legacyCursor },
            CancellationToken.None);
        var encodedPage = firstPage.NextCursor is null
            ? Enumerable.Empty<Guid>()
            : (await service.GetPointsAsync(
                request with { Cursor = firstPage.NextCursor },
                CancellationToken.None)).Items.Select(point => point.Id);
        Assert.Equal(
            encodedPage.ToArray(),
            legacyPage.Items.Select(point => point.Id).ToArray());
    }

    [Fact]
    public async Task GetPointsAsync_InvalidCursorIsRejectedInsteadOfSilentlyRestarting()
    {
        await using var db = MobileTestHelpers.CreateDb();
        SeedPoint(db, Id(520), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetPointsAsync(
            new MobileManagedLocationQueryRequest(
                Now.AddHours(-1),
                Now.AddHours(1),
                "Asia/Shanghai",
                DeviceId,
                null,
                true,
                "not-a-cursor",
                50),
            CancellationToken.None));

        Assert.Equal(6208, error.ErrorCode);
    }

    [Fact]
    public async Task CorrectAsync_RecalculatesBeforeReportingSuccess()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(530), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var recalculation = new RecordingRecalculationService();
        var service = CreateServices(db, recalculation: recalculation).Correction;

        var result = await service.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(31.24, 121.48, 0),
            CancellationToken.None);

        var scope = Assert.Single(recalculation.Scopes);
        Assert.Equal(DeviceId, scope.DeviceId);
        Assert.InRange(Now, scope.RangeStartUtc, scope.RangeEndUtc.AddTicks(-1));
        Assert.Equal(Now, result.RecomputedAtUtc);
    }

    [Fact]
    public async Task CorrectAsync_RecalculationFailureRollsBackMutationAndAudit()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        var point = SeedPoint(db, Id(531), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var recalculation = new RecordingRecalculationService(
            new InvalidOperationException("aggregation failed"));
        var service = CreateServices(db, recalculation: recalculation).Correction;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(31.24, 121.48, 0),
            CancellationToken.None));

        Assert.Equal("aggregation failed", error.Message);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().SingleAsync();
        Assert.Equal(31.230416m, stored.Latitude);
        Assert.Equal(121.473701m, stored.Longitude);
        Assert.Equal(0, stored.Revision);
        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task CorrectAsync_ConcurrentRevisionTokenRejectsStaleWriteAndRollsBackAudit()
    {
        var interceptor = new BumpRevisionBeforeSaveInterceptor();
        await using var ctx = await DeviceManagementTestDb.CreateAsync(interceptor);
        var db = ctx.Db;
        var point = SeedPoint(db, Id(532), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        interceptor.Arm();
        var service = CreateServices(db).Correction;

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(31.24, 121.48, 0),
            CancellationToken.None));

        Assert.Equal(6204, error.ErrorCode);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().SingleAsync();
        Assert.Equal(31.230416m, stored.Latitude);
        Assert.Equal(121.473701m, stored.Longitude);
        Assert.Equal(0, stored.Revision);
        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_RecalculatesAffectedDayBeforeReportingSuccess()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(533), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var recalculation = new RecordingRecalculationService();
        var service = CreateServices(db, recalculation: recalculation).Correction;

        var result = await service.DeleteAsync(point.Id, 0, CancellationToken.None);

        var scope = Assert.Single(recalculation.Scopes);
        Assert.Equal(DeviceId, scope.DeviceId);
        Assert.InRange(Now, scope.RangeStartUtc, scope.RangeEndUtc.AddTicks(-1));
        Assert.Equal(Now, result.RecomputedAtUtc);
        Assert.Empty(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_RecalculationFailureRollsBackDeletionPurgeAndAudit()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        var point = SeedPoint(db, Id(534), Now, 31.230416, 121.473701);
        db.Set<MobileLocationPointRevisionEntity>().Add(new MobileLocationPointRevisionEntity
        {
            UserId = MobileTestHelpers.UserId,
            PointId = point.Id,
            DeviceId = DeviceId,
            Operation = "correct",
            RevisionBefore = 0,
            RevisionAfter = 1,
            ActorUserId = MobileTestHelpers.UserId,
            CreatedAt = Now.AddMinutes(-1)
        });
        await db.SaveChangesAsync();
        var service = CreateServices(
            db,
            recalculation: new RecordingRecalculationService(
                new InvalidOperationException("aggregation failed"))).Correction;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(point.Id, 0, CancellationToken.None));

        Assert.Equal("aggregation failed", error.Message);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.Null(stored.DeletedAtUtc);
        Assert.Equal(0, stored.Revision);
        Assert.Single(await db.Set<MobileLocationPointRevisionEntity>().ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_RetryAfterSuccessIsIdempotentAndDoesNotAdvanceRevision()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var point = SeedPoint(db, Id(535), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        var service = CreateServices(db).Correction;

        var first = await service.DeleteAsync(point.Id, 0, CancellationToken.None);
        var retried = await service.DeleteAsync(point.Id, 0, CancellationToken.None);

        Assert.Equal(first.PointId, retried.PointId);
        Assert.Equal(first.DeletedAtUtc, retried.DeletedAtUtc);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileLocationPointEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(stored.DeletedAtUtc);
        Assert.Equal(1, stored.Revision);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SavePointMutation_NonUniqueDbUpdateExceptionKeepsServerErrorSemantics()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"mobile-throwing-{Guid.NewGuid()}")
            .Options;
        await using var db = new ThrowingDbContext(options);
        var point = SeedPoint(db, Id(540), Now, 31.230416, 121.473701);
        await db.SaveChangesAsync();
        db.FailNextSave = true;
        var service = CreateServices(db).Correction;

        var error = await Record.ExceptionAsync(() => service.CorrectAsync(
            point.Id,
            new MobileLocationPointCorrectionRequest(31.24, 121.48, 0),
            CancellationToken.None));

        Assert.IsType<DbUpdateException>(error);
        Assert.IsNotType<DomainException>(error);
    }

    [Fact]
    public async Task CreateSavedPlace_RepeatedRequestReturnsTheSameRow()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        var service = CreateServices(db).SavedPlace;
        var request = new MobileSavedPlaceCreateRequest(
            "家",
            "home",
            31.230416,
            121.473701,
            120);

        var first = await service.CreateAsync(request, CancellationToken.None);
        var retried = await service.CreateAsync(request, CancellationToken.None);

        Assert.Equal(first.Id, retried.Id);
        Assert.Single(await db.Set<MobileSavedPlaceEntity>().ToListAsync());
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task UpdateSavedPlace_DuplicateContentIsRejectedAsNaturalKeyConflict()
    {
        await using var ctx = await DeviceManagementTestDb.CreateAsync();
        var db = ctx.Db;
        var service = CreateServices(db).SavedPlace;
        var first = await service.CreateAsync(
            new MobileSavedPlaceCreateRequest("家", "home", 31.230416, 121.473701, 120),
            CancellationToken.None);
        var second = await service.CreateAsync(
            new MobileSavedPlaceCreateRequest("公司", "other", 31.230416, 121.473701, 120),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.UpdateAsync(
            second.Id,
            new MobileSavedPlaceUpdateRequest("家", "home", second.Revision),
            CancellationToken.None));

        Assert.Equal(6205, error.ErrorCode);
        db.ChangeTracker.Clear();
        var stored = await db.Set<MobileSavedPlaceEntity>().SingleAsync(place => place.Id == second.Id);
        Assert.Equal("公司", stored.Name);
        Assert.Equal(second.Revision, stored.Revision);
        Assert.Equal(first.Id, (await db.Set<MobileSavedPlaceEntity>().SingleAsync(place => place.Name == "家")).Id);
    }

    private static Services CreateServices(
        PimDbContext db,
        Guid? userId = null,
        IAuditLogService? audit = null,
        IMobileLocationRecalculationService? recalculation = null)
    {
        var user = MobileTestHelpers.CurrentUser(userId);
        var time = MobileTestHelpers.Time(Now);
        var query = new MobileLocationQueryService(time);
        var aggregation = new MobileLocationAggregationService(db, user, query, time);
        var frequentPlace = new MobileFrequentPlaceService(db, user, query);
        var recalculationService = recalculation
            ?? new MobileLocationRecalculationService(aggregation, frequentPlace, time);
        var auditService = audit ?? new AuditLogService(db);
        var cache = new MemoryAggregateResultCache(new MemoryCache(new MemoryCacheOptions()), time);
        return new Services(
            new MobileLocationCorrectionService(
                db,
                user,
                query,
                aggregation,
                recalculationService,
                auditService,
                cache,
                time),
            new MobileSavedPlaceService(db, user, auditService, cache, time),
            aggregation,
            frequentPlace);
    }

    private static MobileLocationPointEntity SeedPoint(
        PimDbContext db,
        Guid id,
        DateTimeOffset recordedAt,
        double latitude,
        double longitude,
        Guid? userId = null,
        string deviceId = DeviceId,
        string quality = "usable",
        decimal accuracy = 12)
    {
        var point = new MobileLocationPointEntity
        {
            Id = id,
            UserId = userId ?? MobileTestHelpers.UserId,
            DeviceId = deviceId,
            RecordedAtUtc = recordedAt,
            Latitude = Convert.ToDecimal(latitude),
            Longitude = Convert.ToDecimal(longitude),
            HorizontalAccuracyMeters = accuracy,
            Provider = "gps",
            Source = "auto",
            Quality = quality,
            RawJson = "{}",
            CreatedAt = recordedAt
        };
        db.Set<MobileLocationPointEntity>().Add(point);
        return point;
    }

    private static void SeedCluster(PimDbContext db, int firstId, DateTimeOffset recordedAt, string deviceId)
    {
        var offsets = new[]
        {
            (0d, 0d), (0.0002, 0.0002), (-0.0002, 0.0002), (0.0002, -0.0002),
            (-0.0002, -0.0002), (0.0001, 0d), (-0.0001, 0d), (0d, 0.0001),
            (0d, -0.0001), (0.00015, 0.00015), (-0.00015, -0.00015), (0.0001, -0.0001)
        };
        for (var index = 0; index < offsets.Length; index++)
        {
            SeedPoint(
                db,
                Id(firstId + index),
                recordedAt.AddMinutes(index),
                31.230416 + offsets[index].Item1,
                121.473701 + offsets[index].Item2,
                deviceId: deviceId);
        }
    }

    private static void SeedDevice(PimDbContext db, string deviceId)
        => db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            DeviceHash = "hash",
            DisplayName = "Pixel",
            Manufacturer = "Google",
            Brand = "Google",
            Model = "Pixel",
            OsVersion = "15",
            ApiLevel = 35,
            AppVersion = "1.0",
            MetadataJson = "{}",
            RegisteredAtUtc = Now.AddDays(-1),
            LastSeenAtUtc = Now,
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now
        });

    private static MobileLocationQueryRequest Query(
        DateTimeOffset start,
        DateTimeOffset end,
        string? deviceId = null)
        => new(start, end, "Asia/Shanghai", deviceId);

    private static MobileLocationPointRequest LocationRequest()
        => new(
            DeviceId,
            Now,
            31.230416,
            121.473701,
            12,
            "gps",
            "auto",
            4.2,
            6.0,
            1.1,
            0.5,
            90,
            1.5,
            false,
            "{}");

    private static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");

    private sealed record Services(
        MobileLocationCorrectionService Correction,
        MobileSavedPlaceService SavedPlace,
        MobileLocationAggregationService Aggregation,
        MobileFrequentPlaceService FrequentPlace);

    private sealed class RecordingAuditLog : IAuditLogService
    {
        public List<CreateAuditLogRequest> Requests { get; } = [];

        public AuditLogDto Enqueue(CreateAuditLogRequest request)
        {
            Requests.Add(request);
            return new AuditLogDto(
                Guid.NewGuid(),
                request.UserId,
                request.ActorType,
                request.Action,
                request.ResourceType,
                request.ResourceId,
                request.Source,
                request.Result,
                request.CorrelationId,
                Now);
        }

        public Task<AuditLogDto> RecordAsync(
            CreateAuditLogRequest request,
            CancellationToken ct = default)
            => Task.FromResult(Enqueue(request));
    }

    private sealed class RecordingRecalculationService : IMobileLocationRecalculationService
    {
        private readonly Exception? _failure;

        public RecordingRecalculationService(Exception? failure = null)
        {
            _failure = failure;
        }

        public List<MobileLocationRecalculationScope> Scopes { get; } = [];

        public Task<DateTimeOffset> RecalculateAsync(
            IReadOnlyCollection<MobileLocationRecalculationScope> scopes,
            CancellationToken ct = default)
        {
            Scopes.AddRange(scopes);
            return _failure is null
                ? Task.FromResult(Now)
                : Task.FromException<DateTimeOffset>(_failure);
        }
    }

    private sealed class ThrowingDbContext(DbContextOptions<PimDbContext> options) : PimDbContext(options)
    {
        public bool FailNextSave { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => FailNextSave
                ? Task.FromException<int>(new DbUpdateException(
                    "write failed",
                    new InvalidOperationException("disk full")))
                : base.SaveChangesAsync(cancellationToken);

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
            => FailNextSave
                ? Task.FromException<int>(new DbUpdateException(
                    "write failed",
                    new InvalidOperationException("disk full")))
                : base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// 在业务 SaveChanges 发起前，用同一事务把 revision 从 0 改成 1。
    /// 服务随后携带旧 revision=0 更新时只能影响 0 行，触发真实的数据库 CAS 冲突。
    /// </summary>
    private sealed class BumpRevisionBeforeSaveInterceptor : SaveChangesInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1 && eventData.Context is not null)
            {
                await eventData.Context.Database.ExecuteSqlRawAsync(
                    "UPDATE mobile_location_points SET revision = revision + 1",
                    cancellationToken);
            }

            return result;
        }
    }
}
