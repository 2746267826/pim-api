using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassificationRuleEvaluatorTests
{
    [Fact]
    public void ActivityClassificationResult_HasFallbackDefaults()
    {
        var result = ActivityClassificationResult.Fallback();

        Assert.Equal("其他", result.CategoryName);
        Assert.Equal("#64748b", result.CategoryColor);
        Assert.Null(result.ProjectTag);
        Assert.Equal("fallback", result.Source);
        Assert.True(result.Confidence < 0.5);
    }

    [Fact]
    public void SchemaSql_MigratesLegacyRulesAboveBuiltinPriority()
    {
        Assert.Contains(
            "priority + 1000",
            PcTrackerSchemaInitializer.SchemaSql);
    }

    [Fact]
    public void SchemaSql_EnforcesUniquePendingSuggestionClusters()
    {
        Assert.Contains(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_pc_activity_classification_suggestions_pending_cluster",
            PcTrackerSchemaInitializer.SchemaSql);
        Assert.Contains(
            "WHERE status = 'pending'",
            PcTrackerSchemaInitializer.SchemaSql);
    }

    [Fact]
    public void SchemaSql_RemovesExeSuffixCaseInsensitivelyWhenMigratingLegacyRules()
    {
        Assert.Contains(
            "regexp_replace(app_pattern, '\\.exe$', '', 'i')",
            PcTrackerSchemaInitializer.SchemaSql);
    }
}
