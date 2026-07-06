using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
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
        typeof(ActivityClassificationAuditEntity),
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
        Assert.Equal("没有匹配到规则或启发式分类。", entity.FindProperty(nameof(ActivityClassificationEntity.Explanation))!.GetDefaultValue());
        Assert.Equal("local-v1", entity.FindProperty(nameof(ActivityClassificationEntity.ClassifierVersion))!.GetDefaultValue());
        Assert.Equal("NOW()", entity.FindProperty(nameof(ActivityClassificationEntity.ClassifiedAt))!.GetDefaultValueSql());
        Assert.Equal("pc-fallback-v1", entity.FindProperty(nameof(ActivityClassificationEntity.RecordKeyVersion))!.GetDefaultValue());
        Assert.Equal("low", entity.FindProperty(nameof(ActivityClassificationEntity.RecordKeyStability))!.GetDefaultValue());
        Assert.Equal("fallback", entity.FindProperty(nameof(ActivityClassificationEntity.SourceType))!.GetDefaultValue());
        Assert.Equal("'[]'::jsonb", entity.FindProperty(nameof(ActivityClassificationEntity.SourceBucketIdsJson))!.GetDefaultValueSql());
        Assert.Equal("interpreted-aw-v1", entity.FindProperty(nameof(ActivityClassificationEntity.InterpretationVersion))!.GetDefaultValue());
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_activity_classifications_record_key_version");
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_activity_classifications_source_type");
    }

    [Fact]
    public void PimDbContext_ConfiguresActivityClassificationAuditModel()
    {
        using var db = CreateDbContext();

        var entity = db.Model.FindEntityType(typeof(ActivityClassificationAuditEntity));

        Assert.NotNull(entity);
        Assert.Equal("pc_activity_classification_audits", entity!.GetTableName());
        Assert.Equal("gen_random_uuid()", entity.FindProperty(nameof(ActivityClassificationAuditEntity.Id))!.GetDefaultValueSql());
        Assert.Equal("'[]'::jsonb", entity.FindProperty(nameof(ActivityClassificationAuditEntity.AffectedRecordKeysJson))!.GetDefaultValueSql());
        Assert.Equal("NOW()", entity.FindProperty(nameof(ActivityClassificationAuditEntity.CreatedAt))!.GetDefaultValueSql());
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_activity_classification_audits_rule_id");
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_activity_classification_audits_suggestion_id");
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_activity_classification_audits_created_at");
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

    [Fact]
    public void AppKnowledgeContextEntity_HasExpectedTableAndIndexes()
    {
        using var db = CreateDbContext();
        var entity = db.Model.FindEntityType(typeof(AppKnowledgeContextEntity));

        Assert.NotNull(entity);
        Assert.Equal("pc_app_knowledge_contexts", entity!.GetTableName());
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_app_knowledge_contexts_app_pattern" &&
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(AppKnowledgeContextEntity.ProcessName),
                nameof(AppKnowledgeContextEntity.PatternType),
                nameof(AppKnowledgeContextEntity.PatternValue)
            ]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_app_knowledge_contexts_category");
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_app_knowledge_contexts_source_suggestion" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(AppKnowledgeContextEntity.SourceSuggestionId)
            ]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "ix_pc_app_knowledge_contexts_app_signature_id" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(AppKnowledgeContextEntity.AppSignatureId)
            ]));

        var appSignatureId = entity.FindProperty(nameof(AppKnowledgeContextEntity.AppSignatureId));
        Assert.NotNull(appSignatureId);
        Assert.True(appSignatureId!.IsNullable);

        var appSignatureForeignKey = Assert.Single(entity.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(AppSignatureEntity) &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([
                nameof(AppKnowledgeContextEntity.AppSignatureId)
            ]));
        Assert.False(appSignatureForeignKey.IsRequired);
        Assert.Equal(DeleteBehavior.SetNull, appSignatureForeignKey.DeleteBehavior);
    }

    [Fact]
    public void PcTrackerSchemaInitializer_AlignsInitializerOwnedTablesWithEfModel()
    {
        Assert.Contains("source VARCHAR(32) NOT NULL DEFAULT 'builtin'", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("confidence DOUBLE PRECISION NOT NULL DEFAULT 1.0", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("name VARCHAR(64) NOT NULL", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("color VARCHAR(7) NOT NULL DEFAULT '#64748b'", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("icon VARCHAR(32)", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains("productivity VARCHAR(16) NOT NULL DEFAULT 'neutral'", PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains(
            "CREATE INDEX IF NOT EXISTS ix_pc_app_knowledge_contexts_app_signature_id",
            PcTrackerSchemaInitializer.SchemaSql);
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
