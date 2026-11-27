using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Some SQLite test contexts deliberately skip <see cref="PimDbContext.OnModelCreating"/>.
/// The ledger entity must therefore declare its composite key via data annotations, not only
/// through the base context's inline fluent mapping.
/// </summary>
public sealed class NoteMcpOperationEntityModelTests
{
    [Fact]
    public void CompositeKeyIsDiscoveredWithoutBaseOnModelCreating()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"mcp-ledger-model-{Guid.NewGuid():N}")
            .Options;
        using var db = new CoreOnlyDbContext(options);

        var entityType = db.Model.FindEntityType(typeof(NoteMcpOperationEntity));
        Assert.NotNull(entityType);
        var key = entityType.FindPrimaryKey();
        Assert.NotNull(key);
        Assert.Equal(
            new[] { nameof(NoteMcpOperationEntity.OwnerId), nameof(NoteMcpOperationEntity.RequestId) },
            key.Properties.Select(property => property.Name).ToArray());
    }

    private sealed class CoreOnlyDbContext(DbContextOptions<PimDbContext> options)
        : PimDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Intentionally empty: mirrors module-only SQLite test contexts in this repository.
        }
    }
}
