using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Xunit;

namespace Pim.UnitTests.Operations;

public class PimDbContextModelCacheTests
{
    [Fact]
    public void MicrosoftCalendarSyncMigration_HasStableIdentifier()
    {
        var migration = typeof(Pim.Infrastructure.Data.Migrations.MicrosoftCalendarSync);
        var attribute = migration.GetCustomAttributes(typeof(MigrationAttribute), false)
            .Cast<MigrationAttribute>()
            .Single();

        Assert.Equal("20260710000000_MicrosoftCalendarSync", attribute.Id);
    }

    [Fact]
    public void MicrosoftCalendarSyncMigration_TargetModelContainsMicrosoftSyncEntities()
    {
        var migration = new Pim.Infrastructure.Data.Migrations.MicrosoftCalendarSync();

        var targetModel = migration.TargetModel;

        Assert.NotNull(targetModel);
        Assert.NotNull(targetModel.FindEntityType(typeof(OutlookAuthorizationSessionEntity)));
        Assert.NotNull(targetModel.FindEntityType(typeof(OutlookCalendarBindingEntity)));
        Assert.NotNull(targetModel.FindEntityType(typeof(OutlookOperationExecutionEntity)));
    }

    [Fact]
    public void ModelCache_UsesModuleAssembliesRegisteredAfterCoreModelIsBuilt()
    {
        var coreOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using (var coreDb = new PimDbContext(coreOptions))
        {
            Assert.Null(coreDb.Model.FindEntityType(typeof(ModelCacheCanaryEntity)));
        }

        PimDbContext.RegisterModuleAssembly(typeof(ModelCacheCanaryEntity).Assembly);

        var moduleOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var moduleDb = new PimDbContext(moduleOptions);

        Assert.NotNull(moduleDb.Model.FindEntityType(typeof(ModelCacheCanaryEntity)));
    }

    private sealed class ModelCacheCanaryEntity
    {
        public Guid Id { get; set; }
    }

    private sealed class ModelCacheCanaryEntityConfiguration : IEntityTypeConfiguration<ModelCacheCanaryEntity>
    {
        public void Configure(EntityTypeBuilder<ModelCacheCanaryEntity> builder)
        {
            builder.ToTable("model_cache_canaries");
            builder.HasKey(e => e.Id);
        }
    }
}
