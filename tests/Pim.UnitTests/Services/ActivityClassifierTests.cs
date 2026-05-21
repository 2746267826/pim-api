using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassifierTests
{
    [Fact]
    public void Classify_UserRuleBeatsHeuristic()
    {
        var ruleId = Guid.NewGuid();
        var context = CreateContext(
            Domain: "docs.activitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch");
        var rules = new[]
        {
            new ActivityCategoryRuleEntity
            {
                Id = ruleId,
                RuleName = "ActivityWatch docs project",
                Status = "active",
                CategoryName = "Project",
                ProjectTag = "ActivityWatch",
                Color = "#123456",
                Priority = 100,
                ConditionsJson = """
                    {
                      "all": [
                        { "field": "domain", "op": "domainSuffix", "value": "activitywatch.net" }
                      ]
                    }
                    """,
                Confidence = 0.97,
                Explanation = "Matched user ActivityWatch rule."
            }
        };

        var result = ActivityClassifier.Classify(context, rules);

        Assert.Equal("rule", result.Source);
        Assert.Equal("Project", result.CategoryName);
        Assert.Equal("#123456", result.CategoryColor);
        Assert.Equal("ActivityWatch", result.ProjectTag);
        Assert.Equal(0.97, result.Confidence);
        Assert.Equal("Matched user ActivityWatch rule.", result.Explanation);
        Assert.Equal(ruleId, result.SourceRuleId);
    }

    [Fact]
    public void Classify_GithubRepoBecomesProgrammingWithProjectTag()
    {
        var context = CreateContext(
            Domain: "github.com",
            UrlPath: "/owner/projectGPT/pull/1",
            Title: "Pull request");

        var result = ActivityClassifier.Classify(context, []);

        Assert.Equal("编程", result.CategoryName);
        Assert.Equal("#6B5EE4", result.CategoryColor);
        Assert.Equal("projectGPT", result.ProjectTag);
        Assert.Equal("heuristic", result.Source);
        Assert.Null(result.SourceRuleId);
    }

    [Fact]
    public void Classify_DocsPageBecomesLearning()
    {
        var context = CreateContext(
            Domain: "docs.activitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch");

        var result = ActivityClassifier.Classify(context, []);

        Assert.Equal("学习", result.CategoryName);
        Assert.Equal("#14b8a6", result.CategoryColor);
        Assert.Equal("ActivityWatch", result.ProjectTag);
        Assert.Equal("heuristic", result.Source);
        Assert.Null(result.SourceRuleId);
    }

    [Fact]
    public void Classify_UnknownReturnsFallback()
    {
        var context = CreateContext(
            AppName: "MysteryApp",
            AppNameNormalized: "mysteryapp",
            Domain: "example.invalid",
            UrlPath: "/nothing-to-see",
            Title: "Untitled");

        var result = ActivityClassifier.Classify(context, []);
        var fallback = ActivityClassificationResult.Fallback();

        Assert.Equal(fallback, result);
    }

    [Fact]
    public void Classify_InactiveRuleIsIgnored()
    {
        var ruleId = Guid.NewGuid();
        var context = CreateContext(
            Domain: "docs.activitywatch.net",
            UrlPath: "/en/latest/api/rest.html",
            Title: "REST API - ActivityWatch");
        var rules = new[]
        {
            new ActivityCategoryRuleEntity
            {
                Id = ruleId,
                RuleName = "Inactive docs override",
                Status = "inactive",
                CategoryName = "Ignored",
                ProjectTag = "IgnoredProject",
                Color = "#000000",
                Priority = 1000,
                ConditionsJson = """
                    {
                      "all": [
                        { "field": "domain", "op": "domainSuffix", "value": "activitywatch.net" }
                      ]
                    }
                    """,
                Confidence = 0.99,
                Explanation = "Should not be returned."
            }
        };

        var result = ActivityClassifier.Classify(context, rules);

        Assert.Equal("heuristic", result.Source);
        Assert.Equal("学习", result.CategoryName);
        Assert.Equal("ActivityWatch", result.ProjectTag);
        Assert.Null(result.SourceRuleId);
    }

    [Fact]
    public void Classify_TerminalAppBecomesTerminal()
    {
        var context = CreateContext(
            RecordType: "window",
            AppName: "WindowsTerminal.exe",
            AppNameNormalized: "windowsterminal",
            BucketType: "aw-watcher-window");

        var result = ActivityClassifier.Classify(context, []);

        Assert.Equal("终端", result.CategoryName);
        Assert.Equal("#E05A7A", result.CategoryColor);
        Assert.Equal("heuristic", result.Source);
    }

    [Fact]
    public void Classify_OfficeAppBecomesOffice()
    {
        var context = CreateContext(
            RecordType: "window",
            AppName: "EXCEL.EXE",
            AppNameNormalized: "excel",
            BucketType: "aw-watcher-window");

        var result = ActivityClassifier.Classify(context, []);

        Assert.Equal("办公", result.CategoryName);
        Assert.Equal("#F59E0B", result.CategoryColor);
        Assert.Equal("heuristic", result.Source);
    }

    [Fact]
    public void Classify_FileAppBecomesFiles()
    {
        var context = CreateContext(
            RecordType: "window",
            AppName: "explorer.exe",
            AppNameNormalized: "explorer",
            BucketType: "aw-watcher-window");

        var result = ActivityClassifier.Classify(context, []);

        Assert.Equal("文件", result.CategoryName);
        Assert.Equal("#3B82F6", result.CategoryColor);
        Assert.Equal("heuristic", result.Source);
    }

    [Fact]
    public void Classify_RuleResultUsesMatchedRuleValuesAsIs()
    {
        var ruleId = Guid.NewGuid();
        var context = CreateContext(
            Domain: "example.com",
            UrlPath: "/matched",
            Title: "Matched");
        var rules = new[]
        {
            new ActivityCategoryRuleEntity
            {
                Id = ruleId,
                RuleName = "Null-valued rule",
                Status = "active",
                CategoryName = null,
                ProjectTag = null,
                Color = "#abcdef",
                Priority = 100,
                ConditionsJson = """
                    {
                      "all": [
                        { "field": "domain", "op": "equals", "value": "example.com" }
                      ]
                    }
                    """,
                Confidence = 0.61,
                Explanation = null
            }
        };

        var result = ActivityClassifier.Classify(context, rules);

        Assert.Equal("rule", result.Source);
        Assert.Null(result.CategoryName);
        Assert.Equal("#abcdef", result.CategoryColor);
        Assert.Null(result.ProjectTag);
        Assert.Equal(0.61, result.Confidence);
        Assert.Null(result.Explanation);
        Assert.Equal(ruleId, result.SourceRuleId);
    }

    private static ActivityClassificationContext CreateContext(
        string? RecordType = "web-page",
        string? AppName = "Firefox",
        string? AppNameNormalized = "firefox",
        string? Domain = null,
        string? UrlPath = null,
        string? Title = null,
        string? WindowTitle = null,
        string? FilePath = null,
        string? BucketType = "web.tab.current") =>
        new(
            RecordType,
            AppName,
            AppNameNormalized,
            Domain,
            UrlPath,
            Title,
            WindowTitle ?? Title,
            FilePath,
            BucketType);
}
