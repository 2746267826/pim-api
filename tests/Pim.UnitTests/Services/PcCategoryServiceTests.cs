using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class PcCategoryServiceTests
{
    [Fact]
    public async Task SeedDefaultsAsync_AddsMissingBuiltinsWhenCategoriesAlreadyExist()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "Custom",
            Color = "#111827",
            IsBuiltin = false
        });
        await db.SaveChangesAsync();
        var service = new PcCategoryService(db);

        await service.SeedDefaultsAsync(CancellationToken.None);

        var names = await db.Set<PcCategoryEntity>()
            .Select(category => category.Name)
            .ToListAsync();
        Assert.Contains("Custom", names);
        Assert.Contains("\u7f16\u7a0b", names);
        Assert.Contains("\u7ec8\u7aef", names);
        Assert.Contains("\u6c9f\u901a", names);
        Assert.Contains("\u529e\u516c", names);
        Assert.Contains("\u6587\u4ef6", names);
        Assert.Contains("\u6d4f\u89c8", names);
        Assert.Contains("\u5b66\u4e60", names);
        Assert.Contains("\u5a31\u4e50", names);
        Assert.Contains("\u5176\u4ed6", names);

        var countAfterFirstSeed = await db.Set<PcCategoryEntity>().CountAsync();
        await service.SeedDefaultsAsync(CancellationToken.None);

        Assert.Equal(countAfterFirstSeed, await db.Set<PcCategoryEntity>().CountAsync());
    }

    [Fact]
    public async Task SeedDefaultsAsync_DoesNotDuplicateExistingSameNameCategory()
    {
        await using var db = CreateDb();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
        {
            Id = Guid.NewGuid(),
            Name = "\u7ec8\u7aef",
            Color = "#111827",
            IsBuiltin = false
        });
        await db.SaveChangesAsync();
        var service = new PcCategoryService(db);

        await service.SeedDefaultsAsync(CancellationToken.None);

        Assert.Equal(1, await db.Set<PcCategoryEntity>().CountAsync(category => category.Name == "\u7ec8\u7aef"));
    }

    [Fact]
    public async Task SeedDefaultsAsync_UsesExistingSameNameParentForMissingChildren()
    {
        await using var db = CreateDb();
        var existingWorkId = Guid.NewGuid();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
        {
            Id = existingWorkId,
            Name = "\u5de5\u4f5c",
            Color = "#111827",
            IsBuiltin = false
        });
        await db.SaveChangesAsync();
        var service = new PcCategoryService(db);

        await service.SeedDefaultsAsync(CancellationToken.None);

        var programming = await db.Set<PcCategoryEntity>().SingleAsync(category => category.Name == "\u7f16\u7a0b");
        Assert.Equal(existingWorkId, programming.ParentId);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(PcCategoryEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
