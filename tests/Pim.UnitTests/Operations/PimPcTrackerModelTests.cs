using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Xunit;

namespace Pim.UnitTests.Operations;

public class PimPcTrackerModelTests
{
    public static TheoryData<Type> PcTrackerUuidDefaultEntities => new()
    {
        typeof(AppCategoryEntity),
        typeof(ActivityCategoryRuleEntity),
        typeof(ActivityClassificationSuggestionEntity),
        typeof(ActivityClassificationEntity),
        typeof(ActivityClassificationSettingsEntity)
    };

    [Theory]
    [MemberData(nameof(PcTrackerUuidDefaultEntities))]
    public void PcTrackerUuidIds_UseDatabaseGeneratedDefaults(Type entityType)
    {
        using var db = CreateDbContext();

        var idProperty = db.Model.FindEntityType(entityType)?.FindProperty("Id");

        Assert.NotNull(idProperty);
        Assert.Equal("gen_random_uuid()", idProperty.GetDefaultValueSql());
    }

    [Fact]
    public void PimDbContext_ConfiguresActivityClassificationSnapshotModel()
    {
        using var db = CreateDbContext();

        var entity = db.Model.FindEntityType(typeof(ActivityClassificationEntity));

        Assert.NotNull(entity);
        Assert.Equal("pc_activity_classifications", entity!.GetTableName());
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ux_pc_activity_classifications_record_key" &&
            index.IsUnique);
        Assert.Equal("'[]'::jsonb", entity.FindProperty(nameof(ActivityClassificationEntity.SourceEventIdsJson))!.GetDefaultValueSql());
        Assert.Equal("其他", entity.FindProperty(nameof(ActivityClassificationEntity.CategoryName))!.GetDefaultValue());
        Assert.Equal("#64748b", entity.FindProperty(nameof(ActivityClassificationEntity.CategoryColor))!.GetDefaultValue());
        Assert.Equal(0.2, entity.FindProperty(nameof(ActivityClassificationEntity.Confidence))!.GetDefaultValue());
        Assert.Equal("fallback", entity.FindProperty(nameof(ActivityClassificationEntity.Source))!.GetDefaultValue());
        Assert.Equal("No rule or heuristic matched.", entity.FindProperty(nameof(ActivityClassificationEntity.Explanation))!.GetDefaultValue());
        Assert.Equal("local-v1", entity.FindProperty(nameof(ActivityClassificationEntity.ClassifierVersion))!.GetDefaultValue());
        Assert.Equal("NOW()", entity.FindProperty(nameof(ActivityClassificationEntity.ClassifiedAt))!.GetDefaultValueSql());
    }

    [Fact]
    public void PimDbContext_ConfiguresActivityClassificationSettingsModel()
    {
        using var db = CreateDbContext();

        var entity = db.Model.FindEntityType(typeof(ActivityClassificationSettingsEntity));

        Assert.NotNull(entity);
        Assert.Equal("pc_activity_classification_settings", entity!.GetTableName());
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ux_pc_activity_classification_settings_key" &&
            index.IsUnique);
        Assert.Equal("default", entity.FindProperty(nameof(ActivityClassificationSettingsEntity.SettingsKey))!.GetDefaultValue());
        Assert.Equal(5, entity.FindProperty(nameof(ActivityClassificationSettingsEntity.RecommendedMinimumClassificationDurationMinutes))!.GetDefaultValue());
        Assert.Equal("NOW()", entity.FindProperty(nameof(ActivityClassificationSettingsEntity.CreatedAt))!.GetDefaultValueSql());
        Assert.Equal("NOW()", entity.FindProperty(nameof(ActivityClassificationSettingsEntity.UpdatedAt))!.GetDefaultValueSql());
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
