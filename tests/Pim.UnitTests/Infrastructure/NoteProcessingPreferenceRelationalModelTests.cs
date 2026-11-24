using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// 只应用模块级配置的 SQLite 测试上下文会跳过 <see cref="PimDbContext.OnModelCreating"/>，
/// 因此基础实体不能只依赖基类内联映射来声明主键。
/// </summary>
public sealed class NoteProcessingPreferenceRelationalModelTests
{
    [Fact]
    public async Task OwnerId_IsDiscoverableAsThePrimaryKeyWithoutBaseInlineMappings()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new CoreEntitiesSqliteDbContext(options);

        await db.Database.EnsureCreatedAsync();

        var entityType = db.Model.FindEntityType(typeof(NoteProcessingPreferenceEntity));
        Assert.NotNull(entityType);
        var primaryKey = entityType.FindPrimaryKey();
        Assert.NotNull(primaryKey);
        var key = Assert.Single(primaryKey.Properties);
        Assert.Equal(nameof(NoteProcessingPreferenceEntity.OwnerId), key.Name);
    }

    private sealed class CoreEntitiesSqliteDbContext(DbContextOptions<PimDbContext> options)
        : PimDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // 刻意不调用 base：复现只映射单一模块的 SQLite 测试上下文。
        }
    }
}
