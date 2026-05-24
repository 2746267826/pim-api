using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Xunit;

namespace Pim.UnitTests.Operations;

public class PimPcTrackerModelTests
{
    public static TheoryData<Type> LegacyUuidDefaultEntities => new()
    {
        typeof(AppCategoryEntity),
        typeof(ActivityCategoryRuleEntity),
        typeof(ActivityClassificationSuggestionEntity)
    };

    [Theory]
    [MemberData(nameof(LegacyUuidDefaultEntities))]
    public void LegacyPcTrackerUuidIds_UseDatabaseGeneratedDefaults(Type entityType)
    {
        using var db = CreateDbContext();

        var idProperty = db.Model.FindEntityType(entityType)?.FindProperty("Id");

        Assert.NotNull(idProperty);
        Assert.Equal("gen_random_uuid()", idProperty.GetDefaultValueSql());
    }

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AppCategoryEntity).Assembly);

        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=localhost;Database=pim_model_tests")
            .Options;

        return new PimDbContext(options);
    }
}
