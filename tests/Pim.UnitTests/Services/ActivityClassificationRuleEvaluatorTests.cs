using System.Globalization;
using System.Text.Json;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassificationRuleEvaluatorTests
{
    [Fact]
    public void Matches_ReturnsTrueForWebPageDomainSuffixAndTitleContainsAny()
    {
        var context = new ActivityClassificationContext(
            RecordType: "web-page",
            AppName: "Firefox",
            AppNameNormalized: "firefox",
            Domain: "docs.activitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch",
            WindowTitle: "REST API - ActivityWatch - Firefox",
            FilePath: null,
            BucketType: "web.tab.current");
        const string conditionsJson = """
            {
              "all": [
                { "field": "domain", "op": "domainSuffix", "value": "activitywatch.net" },
                { "field": "title", "op": "containsAny", "value": [ "REST API", "ActivityWatch" ] }
              ]
            }
            """;

        var matches = ActivityClassificationRuleEvaluator.Matches(conditionsJson, context);

        Assert.True(matches);
    }

    [Fact]
    public void Matches_ReturnsFalseWhenOneAllConditionFails()
    {
        var context = new ActivityClassificationContext(
            RecordType: "web-page",
            AppName: "Firefox",
            AppNameNormalized: "firefox",
            Domain: "docs.activitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch",
            WindowTitle: "REST API - ActivityWatch - Firefox",
            FilePath: null,
            BucketType: "web.tab.current");
        const string conditionsJson = """
            {
              "all": [
                { "field": "domain", "op": "domainSuffix", "value": "activitywatch.net" },
                { "field": "title", "op": "containsAny", "value": [ "GitHub", "calendar" ] }
              ]
            }
            """;

        var matches = ActivityClassificationRuleEvaluator.Matches(conditionsJson, context);

        Assert.False(matches);
    }

    [Theory]
    [InlineData("recordType", "equals", "WEB-PAGE")]
    [InlineData("appName", "contains", "fox")]
    [InlineData("appNameNormalized", "startsWith", "fire")]
    [InlineData("domain", "endsWith", "WATCH.NET")]
    [InlineData("urlPath", "pathPrefix", "en/latest/api")]
    [InlineData("title", "regex", "rest api\\s+-\\s+activitywatch")]
    [InlineData("windowTitle", "contains", "Firefox")]
    [InlineData("filePath", "endsWith", "notes.txt")]
    [InlineData("bucketType", "equals", "WEB.TAB.CURRENT")]
    public void Matches_SupportsStringOperators(string field, string op, string value)
    {
        var context = new ActivityClassificationContext(
            RecordType: "web-page",
            AppName: "Firefox",
            AppNameNormalized: "firefox",
            Domain: "docs.activitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch",
            WindowTitle: "REST API - ActivityWatch - Firefox",
            FilePath: @"C:\Users\tester\notes.txt",
            BucketType: "web.tab.current");
        var conditionsJson = JsonSerializer.Serialize(new
        {
            all = new[]
            {
                new { field, op, value }
            }
        });

        var matches = ActivityClassificationRuleEvaluator.Matches(conditionsJson, context);

        Assert.True(matches);
    }

    [Fact]
    public void Matches_DomainSuffixRequiresDomainBoundary()
    {
        var context = new ActivityClassificationContext(
            RecordType: "web-page",
            AppName: "Firefox",
            AppNameNormalized: "firefox",
            Domain: "notactivitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch",
            WindowTitle: "REST API - ActivityWatch - Firefox",
            FilePath: null,
            BucketType: "web.tab.current");
        const string conditionsJson = """
            {
              "all": [
                { "field": "domain", "op": "domainSuffix", "value": "activitywatch.net" }
              ]
            }
            """;

        var matches = ActivityClassificationRuleEvaluator.Matches(conditionsJson, context);

        Assert.False(matches);
    }

    [Theory]
    [InlineData("/docs?tab=api")]
    [InlineData("/docs#section")]
    public void Matches_PathPrefixIgnoresQueryAndFragmentBoundaries(string urlPath)
    {
        var context = new ActivityClassificationContext(
            RecordType: "web-page",
            AppName: "Firefox",
            AppNameNormalized: "firefox",
            Domain: "docs.activitywatch.net",
            UrlPath: urlPath,
            Title: "REST API - ActivityWatch",
            WindowTitle: "REST API - ActivityWatch - Firefox",
            FilePath: null,
            BucketType: "web.tab.current");
        const string conditionsJson = """
            {
              "all": [
                { "field": "urlPath", "op": "pathPrefix", "value": "/docs" }
              ]
            }
            """;

        var matches = ActivityClassificationRuleEvaluator.Matches(conditionsJson, context);

        Assert.True(matches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{ "all": [] }""")]
    [InlineData("""{ "all": "invalid" }""")]
    [InlineData("""{ "all": [{ "field": "title", "op": "unsupported", "value": "ActivityWatch" }] }""")]
    [InlineData("""{ "all": [{ "field": "unknown", "op": "equals", "value": "ActivityWatch" }] }""")]
    [InlineData("""{ "all": [{ "field": "title", "op": "regex", "value": "[" }] }""")]
    public void Matches_ReturnsFalseForMalformedOrUnsupportedConditions(string? conditionsJson)
    {
        var context = new ActivityClassificationContext(
            RecordType: "web-page",
            AppName: "Firefox",
            AppNameNormalized: "firefox",
            Domain: "docs.activitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch",
            WindowTitle: "REST API - ActivityWatch - Firefox",
            FilePath: null,
            BucketType: "web.tab.current");

        var matches = ActivityClassificationRuleEvaluator.Matches(conditionsJson, context);

        Assert.False(matches);
    }

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
    public void SchemaSql_SetsActivityRuleTimestampDefaultsBeforeSeedingEfMigratedTables()
    {
        var normalizedSql = PcTrackerSchemaInitializer.SchemaSql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var createdAtDefaultIndex = normalizedSql.IndexOf(
            "ALTER TABLE pc_activity_category_rules ALTER COLUMN created_at SET DEFAULT NOW();",
            StringComparison.Ordinal);
        var updatedAtDefaultIndex = normalizedSql.IndexOf(
            "ALTER TABLE pc_activity_category_rules ALTER COLUMN updated_at SET DEFAULT NOW();",
            StringComparison.Ordinal);
        var seedIndex = normalizedSql.IndexOf(
            "INSERT INTO pc_activity_category_rules",
            StringComparison.Ordinal);

        Assert.True(createdAtDefaultIndex >= 0);
        Assert.True(updatedAtDefaultIndex >= 0);
        Assert.True(seedIndex > createdAtDefaultIndex);
        Assert.True(seedIndex > updatedAtDefaultIndex);
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

    [Fact]
    public void SchemaSql_FormatsJsonbLiteralsForExecuteSqlRaw()
    {
        var formattedSql = string.Format(
            CultureInfo.InvariantCulture,
            PcTrackerSchemaInitializer.SchemaSql,
            Array.Empty<object>());

        Assert.Contains("DEFAULT '{}'::jsonb", formattedSql);
        Assert.Contains("DEFAULT '[]'::jsonb", formattedSql);
        Assert.Contains("'{\"all\":[{\"field\":\"appNameNormalized\"", formattedSql);
        Assert.DoesNotContain("{{\"all\"", formattedSql);
        Assert.DoesNotContain("{{}}", formattedSql);
    }
}
