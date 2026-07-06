using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pim.Module.Mobile.Entities;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileModelTests
{
    [Fact]
    public void MobileModule_RegistersExpectedEntities()
    {
        using var db = MobileTestHelpers.CreateDb();

        var entityTypes = db.Model.GetEntityTypes().Select(entity => entity.ClrType).ToHashSet();

        Assert.Contains(typeof(MobileDeviceEntity), entityTypes);
        Assert.Contains(typeof(MobileAppCatalogEntity), entityTypes);
        Assert.Contains(typeof(MobileUsageEventEntity), entityTypes);
        Assert.Contains(typeof(MobileUsageSummaryEntity), entityTypes);
        Assert.Contains(typeof(MobileUsageSessionEntity), entityTypes);
        Assert.Contains(typeof(MobileLocationPointEntity), entityTypes);
        Assert.Contains(typeof(MobileSyncBatchEntity), entityTypes);
    }

    [Fact]
    public void MobileLocation_UsesPreciseCoordinateAndAccuracyMappings()
    {
        using var db = MobileTestHelpers.CreateDb();
        var entity = Entity<MobileLocationPointEntity>(db);

        var latitude = entity.FindProperty(nameof(MobileLocationPointEntity.Latitude));
        var longitude = entity.FindProperty(nameof(MobileLocationPointEntity.Longitude));
        var accuracy = entity.FindProperty(nameof(MobileLocationPointEntity.HorizontalAccuracyMeters));

        Assert.NotNull(latitude);
        Assert.NotNull(longitude);
        Assert.NotNull(accuracy);
        Assert.Equal(10, latitude.GetPrecision());
        Assert.Equal(7, latitude.GetScale());
        Assert.Equal(10, longitude.GetPrecision());
        Assert.Equal(7, longitude.GetScale());
        Assert.Equal(9, accuracy.GetPrecision());
        Assert.Equal(2, accuracy.GetScale());
    }

    [Fact]
    public void MobileModel_DefinesRequiredUniqueIndexes()
    {
        using var db = MobileTestHelpers.CreateDb();

        Assert.True(Index<MobileDeviceEntity>(
            db,
            nameof(MobileDeviceEntity.UserId),
            nameof(MobileDeviceEntity.DeviceId)).IsUnique);
        Assert.True(Index<MobileAppCatalogEntity>(
            db,
            nameof(MobileAppCatalogEntity.UserId),
            nameof(MobileAppCatalogEntity.DeviceId),
            nameof(MobileAppCatalogEntity.PackageName)).IsUnique);
        Assert.True(Index<MobileUsageEventEntity>(
            db,
            nameof(MobileUsageEventEntity.UserId),
            nameof(MobileUsageEventEntity.DeviceId),
            nameof(MobileUsageEventEntity.PackageName),
            nameof(MobileUsageEventEntity.EventType),
            nameof(MobileUsageEventEntity.EventTimestampUtc),
            nameof(MobileUsageEventEntity.ClassName)).IsUnique);
        Assert.True(Index<MobileUsageSummaryEntity>(
            db,
            nameof(MobileUsageSummaryEntity.UserId),
            nameof(MobileUsageSummaryEntity.DeviceId),
            nameof(MobileUsageSummaryEntity.PackageName),
            nameof(MobileUsageSummaryEntity.WindowStartUtc),
            nameof(MobileUsageSummaryEntity.WindowEndUtc),
            nameof(MobileUsageSummaryEntity.SourceKind)).IsUnique);
    }

    private static IEntityType Entity<TEntity>(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        return entity;
    }

    private static IIndex Index<TEntity>(DbContext db, params string[] propertyNames)
    {
        var index = Entity<TEntity>(db)
            .GetIndexes()
            .SingleOrDefault(candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
        Assert.NotNull(index);
        return index;
    }
}
