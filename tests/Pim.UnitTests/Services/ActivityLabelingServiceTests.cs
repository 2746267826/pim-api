using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityLabelingServiceTests
{
    private static (PimDbContext db, ActivityLabelingService svc) Create()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ActivityCategoryRuleEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new PimDbContext(options);
        foreach (var (name, i) in CategoryLegacyMapper.UnifiedCategoryNames.Select((n, i) => (n, i)))
            db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
            {
                Id = Guid.Parse($"20000000-0000-0000-0000-{i + 1:D12}"),
                Name = name, Color = "#64748b", IsBuiltin = true
            });
        db.SaveChanges();
        return (db, new ActivityLabelingService(db));
    }

    [Fact]
    public async Task LabelApp_AllScope_WritesAppMapping()
    {
        var (db, svc) = Create();
        var req = new ActivityLabelingRequest("app", "mobaxterm", null, "编程/折腾", "all", null);
        var res = await svc.LabelAsync(req, CancellationToken.None);
        Assert.True(res.Ok);
        var mapping = Assert.Single(db.Set<AppCategoryEntity>());
        Assert.Equal("mobaxterm", mapping.AppPattern);
        Assert.Equal("编程/折腾", mapping.CategoryName);
    }

    [Fact]
    public async Task LabelDomain_KeywordScope_CreatesContextRule()
    {
        var (db, svc) = Create();
        var req = new ActivityLabelingRequest("domain", "bilibili.com", null, "学习", "keyword", "教程");
        await svc.LabelAsync(req, CancellationToken.None);
        var rule = Assert.Single(db.Set<ActivityCategoryRuleEntity>());
        Assert.Contains("\"field\":\"domain\"", rule.ConditionsJson);
        Assert.Contains("\"field\":\"urlPath\"", rule.ConditionsJson);
        Assert.Equal("学习", rule.CategoryName);
    }

    [Fact]
    public async Task LabelWithNewCustomCategory_CreatesCategoryRow()
    {
        var (db, svc) = Create();
        var req = new ActivityLabelingRequest("app", "obsidian", null, "写日记", "all", null);
        var res = await svc.LabelAsync(req, CancellationToken.None);
        Assert.True(res.Ok);
        var cat = db.Set<PcCategoryEntity>().Single(c => c.Name == "写日记");
        Assert.False(cat.IsBuiltin);
        Assert.Equal(cat.Id, res.CategoryId);
    }

    [Fact]
    public async Task LabelDomain_AllScope_IsIdempotent()
    {
        var (db, svc) = Create();
        var req = new ActivityLabelingRequest("domain", "csdn.net", null, "学习", "all", null);
        await svc.LabelAsync(req, CancellationToken.None);
        await svc.LabelAsync(req, CancellationToken.None);
        var rules = db.Set<ActivityCategoryRuleEntity>()
            .Where(r => r.RuleName == "Label: csdn.net [all]").ToList();
        Assert.Single(rules);
    }

    [Fact]
    public async Task BuildQueue_AppCandidatesExcludesMappedAndShortApps()
    {
        var (db, svc) = Create();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        db.Set<AwEventEntity>().AddRange(
            new AwEventEntity { AppNameNormalized = "mobaxterm", Duration = 42 * 60, Timestamp = now, WindowTitle = "ssh to 192.168.1.1", EventType = "window" },
            new AwEventEntity { AppNameNormalized = "mobaxterm", Duration = 3 * 60, Timestamp = now.AddSeconds(1), WindowTitle = "ssh to 192.168.1.2", EventType = "window" },
            new AwEventEntity { AppNameNormalized = "msedge", Duration = 12 * 60, Timestamp = now.AddSeconds(2), EventType = "window" },
            new AwEventEntity { AppNameNormalized = "notepad", Duration = 2 * 60, Timestamp = now.AddSeconds(3), EventType = "window" });
        db.Set<AppCategoryEntity>().Add(new AppCategoryEntity
        {
            Id = Guid.NewGuid(),
            AppPattern = "msedge",
            CategoryName = "其他",
            Color = "#64748b",
            Priority = 100
        });
        await db.SaveChangesAsync();

        var queue = await svc.BuildQueueAsync(20, CancellationToken.None);

        var app = Assert.Single(queue.Items.Where(i => i.TargetType == "app"));
        Assert.Equal("mobaxterm", app.Target);
        Assert.Equal(45, app.Minutes);
        Assert.Contains("ssh to 192.168.1.1", app.SampleTitles);
        Assert.DoesNotContain(queue.Items, i => i.TargetType == "app" && i.Target == "msedge");
        Assert.DoesNotContain(queue.Items, i => i.TargetType == "app" && i.Target == "notepad");
    }

    [Fact]
    public async Task BuildQueue_DomainCandidatesAggregateWebEvents()
    {
        var (db, svc) = Create();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        db.Set<AwEventEntity>().AddRange(
            new AwEventEntity
            {
                AppNameNormalized = "msedge",
                EventType = "web",
                Duration = 20 * 60,
                Timestamp = now,
                WindowTitle = "CSDN - 教程文章",
                DataJson = """{"url":"https://blog.csdn.net/a/b"}"""
            },
            new AwEventEntity
            {
                AppNameNormalized = "msedge",
                EventType = "web",
                Duration = 15 * 60,
                Timestamp = now.AddSeconds(1),
                WindowTitle = "CSDN - 另一篇",
                DataJson = """{"url":"https://blog.csdn.net/c"}"""
            },
            new AwEventEntity
            {
                AppNameNormalized = "msedge",
                EventType = "web",
                Duration = 5 * 60,
                Timestamp = now.AddSeconds(2),
                DataJson = """{"url":"https://short-site.example/x"}"""
            });
        await db.SaveChangesAsync();

        var queue = await svc.BuildQueueAsync(20, CancellationToken.None);

        var domain = Assert.Single(queue.Items.Where(i => i.TargetType == "domain"));
        Assert.Equal("blog.csdn.net", domain.Target);
        Assert.Equal(35, domain.Minutes);
        Assert.Contains("CSDN - 教程文章", domain.SampleTitles);
    }

    [Fact]
    public async Task LabelApp_KeywordScope_CreatesWindowTitleRule()
    {
        var (db, svc) = Create();
        var req = new ActivityLabelingRequest("app", "code", null, "编程/折腾", "keyword", "rust");
        var res = await svc.LabelAsync(req, CancellationToken.None);
        Assert.True(res.Ok);
        Assert.Equal("app_context_rule", res.Created);
        var rule = Assert.Single(db.Set<ActivityCategoryRuleEntity>());
        Assert.Equal("Label: code [rust]", rule.RuleName);
        Assert.Contains("\"field\":\"windowTitle\"", rule.ConditionsJson);
        Assert.Equal("编程/折腾", rule.CategoryName);
    }

    [Fact]
    public async Task BuildQueue_DomainCombinedRule_DoesNotExcludeDomain()
    {
        var (db, svc) = Create();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            AppNameNormalized = "msedge",
            EventType = "web",
            Duration = 45 * 60,
            Timestamp = now,
            DataJson = """{"url":"https://blog.csdn.net/a"}"""
        });
        db.Set<ActivityCategoryRuleEntity>().Add(new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = "combined-domain-rule",
            Scope = "activity",
            Source = "user",
            Status = "active",
            Priority = 450,
            CategoryName = "学习",
            ConditionsJson = """{"all":[{"field":"domain","op":"equals","value":"blog.csdn.net"},{"field":"urlPath","op":"contains","value":"tutorial"}]}""",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var queue = await svc.BuildQueueAsync(20, CancellationToken.None);

        Assert.Contains(queue.Items, i => i.TargetType == "domain" && i.Target == "blog.csdn.net");
    }

    [Fact]
    public async Task BuildQueue_DomainSingleEqualsRule_ExcludesDomain()
    {
        var (db, svc) = Create();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            AppNameNormalized = "msedge",
            EventType = "web",
            Duration = 45 * 60,
            Timestamp = now,
            DataJson = """{"url":"https://blog.csdn.net/a"}"""
        });
        db.Set<ActivityCategoryRuleEntity>().Add(new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = "exact-domain-rule",
            Scope = "activity",
            Source = "user",
            Status = "active",
            Priority = 400,
            CategoryName = "学习",
            ConditionsJson = """{"all":[{"field":"domain","op":"equals","value":"blog.csdn.net"}]}""",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var queue = await svc.BuildQueueAsync(20, CancellationToken.None);

        Assert.DoesNotContain(queue.Items, i => i.TargetType == "domain" && i.Target == "blog.csdn.net");
    }

    [Fact]
    public async Task BuildQueue_DomainMultipleDomainConditions_DoesNotExclude()
    {
        var (db, svc) = Create();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            AppNameNormalized = "msedge",
            EventType = "web",
            Duration = 45 * 60,
            Timestamp = now,
            DataJson = """{"url":"https://blog.csdn.net/a"}"""
        });
        db.Set<ActivityCategoryRuleEntity>().Add(new ActivityCategoryRuleEntity
        {
            Id = Guid.NewGuid(),
            RuleName = "multi-domain-rule",
            Scope = "activity",
            Source = "user",
            Status = "active",
            Priority = 400,
            CategoryName = "学习",
            ConditionsJson = """{"all":[{"field":"domain","op":"equals","value":"blog.csdn.net"},{"field":"domain","op":"equals","value":"example.com"}]}""",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var queue = await svc.BuildQueueAsync(20, CancellationToken.None);

        // AND 语义：domain 不可能同时等于两个域名，该规则实际不命中任何域名 → 不排除。
        Assert.Contains(queue.Items, i => i.TargetType == "domain" && i.Target == "blog.csdn.net");
    }

    [Fact]
    public async Task BuildQueue_AppSingleEqualsRuleExcludes_CombinedRuleDoesNot()
    {
        var (db, svc) = Create();
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        db.Set<AwEventEntity>().AddRange(
            new AwEventEntity { AppNameNormalized = "code", Duration = 45 * 60, Timestamp = now, EventType = "window" },
            new AwEventEntity { AppNameNormalized = "idea", Duration = 45 * 60, Timestamp = now.AddSeconds(1), EventType = "window" });
        db.Set<ActivityCategoryRuleEntity>().AddRange(
            new ActivityCategoryRuleEntity
            {
                Id = Guid.NewGuid(),
                RuleName = "exact-app-rule",
                Scope = "activity",
                Source = "user",
                Status = "active",
                Priority = 400,
                CategoryName = "编程/折腾",
                ConditionsJson = """{"all":[{"field":"appNameNormalized","op":"equals","value":"code"}]}""",
                CreatedAt = now,
                UpdatedAt = now
            },
            new ActivityCategoryRuleEntity
            {
                Id = Guid.NewGuid(),
                RuleName = "context-app-rule",
                Scope = "activity",
                Source = "user",
                Status = "active",
                Priority = 500,
                CategoryName = "编程/折腾",
                ConditionsJson = """{"all":[{"field":"appNameNormalized","op":"equals","value":"idea"},{"field":"windowTitle","op":"contains","value":"rust"}]}""",
                CreatedAt = now,
                UpdatedAt = now
            });
        await db.SaveChangesAsync();

        var queue = await svc.BuildQueueAsync(20, CancellationToken.None);

        Assert.DoesNotContain(queue.Items, i => i.TargetType == "app" && i.Target == "code");
        Assert.Contains(queue.Items, i => i.TargetType == "app" && i.Target == "idea");
    }

    [Fact]
    public async Task LabelDomain_LongTarget_ProducesStableUniqueRuleNames()
    {
        var (db, svc) = Create();
        var target1 = new string('x', 60);
        var req1 = new ActivityLabelingRequest("domain", target1, null, "学习", "all", null);
        await svc.LabelAsync(req1, CancellationToken.None);
        await svc.LabelAsync(req1, CancellationToken.None);
        Assert.Single(db.Set<ActivityCategoryRuleEntity>());

        // 与 target1 前 48 字符相同、尾部不同 → 截断名相同，需靠稳定哈希区分。
        var target2 = new string('x', 59) + "y";
        await svc.LabelAsync(new ActivityLabelingRequest("domain", target2, null, "学习", "all", null), CancellationToken.None);
        var names = db.Set<ActivityCategoryRuleEntity>().Select(r => r.RuleName).ToList();
        Assert.Equal(2, names.Count);
        Assert.NotEqual(names[0], names[1]);
    }
}
