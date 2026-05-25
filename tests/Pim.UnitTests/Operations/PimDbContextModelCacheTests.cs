using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Operations;

public class PimDbContextModelCacheTests
{
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
