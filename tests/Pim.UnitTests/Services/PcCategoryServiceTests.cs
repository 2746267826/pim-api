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
        Assert.Contains("\u7f16\u7a0b/\u6298\u817e", names);
        Assert.Contains("\u5b66\u4e60", names);
        Assert.Contains("\u89c6\u9891", names);
        Assert.Contains("\u804a\u5929", names);
        Assert.Contains("\u6587\u6863", names);
        Assert.Contains("\u6e38\u620f", names);
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
            Name = "\u5b66\u4e60",
            Color = "#111827",
            IsBuiltin = false
        });
        await db.SaveChangesAsync();
        var service = new PcCategoryService(db);

        await service.SeedDefaultsAsync(CancellationToken.None);

        Assert.Equal(1, await db.Set<PcCategoryEntity>().CountAsync(category => category.Name == "\u5b66\u4e60"));
    }

    [Fact]
    public async Task SeedDefaultsAsync_UsesExistingSameNameCategoryForMissingSeed()
    {
        await using var db = CreateDb();
        var existingId = Guid.NewGuid();
        db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
        {
            Id = existingId,
            Name = "\u6587\u6863",
            Color = "#111827",
            IsBuiltin = false
        });
        await db.SaveChangesAsync();
        var service = new PcCategoryService(db);

        await service.SeedDefaultsAsync(CancellationToken.None);

        var document = await db.Set<PcCategoryEntity>().SingleAsync(category => category.Name == "\u6587\u6863");
        Assert.Equal(existingId, document.Id);
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
