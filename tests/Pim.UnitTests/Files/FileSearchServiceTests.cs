using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// FileSearchService：文件模块 v2 的**仅元数据**搜索（设计 §9 / 决策 D3）。
/// v2 不做内容索引与向量检索（那条线随 P4 退役），内容级查找交给 Hermes 的 read_file_text。
/// </summary>
public class FileSearchServiceTests
{
    private static readonly Guid UserId = Guid.Parse("cccccccc-1111-2222-3333-444444444471");
    private static readonly Guid OtherUserId = Guid.Parse("cccccccc-9999-8888-7777-666666666671");

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-search-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(PimDbContext db, Guid userId, string provider = "onedrive")
    {
        var entity = new FileProviderEntity
        {
            UserId = userId,
            Provider = provider,
            Status = "connected",
        };
        db.Set<FileProviderEntity>().Add(entity);
        db.SaveChanges();
        return entity;
    }

    private static void SeedItem(PimDbContext db, FileProviderEntity provider, string path, string name, string mime, bool deleted = false)
    {
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = provider.Id,
            Provider = provider,
            ExternalFileId = Guid.NewGuid().ToString("N"),
            Path = path,
            Name = name,
            ItemType = "file",
            MimeType = mime,
            IsDeleted = deleted,
        });
    }

    private static FileSearchService CreateService(PimDbContext db, Guid? userId, SensitivePathPolicy? policy = null)
        => new(db, new StubCurrentUser(userId), policy ?? new SensitivePathPolicy(null));

    [Fact]
    public async Task SearchAsync_MatchesOnName()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/合同/房屋租赁合同.pdf", "房屋租赁合同.pdf", "application/pdf");
        SeedItem(db, provider, "/合同/报价单.xlsx", "报价单.xlsx", "application/vnd.ms-excel");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("房屋", null));

        var item = Assert.Single(result.Items);
        Assert.Equal("房屋租赁合同.pdf", item.Name);
    }

    [Fact]
    public async Task SearchAsync_MatchesOnPathAndMimeType()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/归档/报告.txt", "报告.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var byPath = await service.SearchAsync(new FileSearchQuery("归档", null));
        Assert.Single(byPath.Items);

        var byMime = await service.SearchAsync(new FileSearchQuery("text/plain", null));
        Assert.Single(byMime.Items);
    }

    /// <summary>敏感路径文件绝不进搜索结果（§13：与直链/文本出口一致）。</summary>
    [Fact]
    public async Task SearchAsync_ExcludesSensitivePaths()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/Secrets/密码.txt", "密码.txt", "text/plain");
        SeedItem(db, provider, "/文档/说明.txt", "说明.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("txt", null));

        var item = Assert.Single(result.Items);
        Assert.Equal("说明.txt", item.Name);
    }

    [Fact]
    public async Task SearchAsync_ExcludesDeletedAndOtherUsersItems()
    {
        await using var db = CreateDb();
        var mine = SeedProvider(db, UserId);
        var theirs = SeedProvider(db, OtherUserId, "onedrive");
        SeedItem(db, mine, "/文档/保留.txt", "保留.txt", "text/plain");
        SeedItem(db, mine, "/文档/已删.txt", "已删.txt", "text/plain", deleted: true);
        SeedItem(db, theirs, "/文档/别人的.txt", "别人的.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery(".txt", null));

        var item = Assert.Single(result.Items);
        Assert.Equal("保留.txt", item.Name);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/a.txt", "a.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        Assert.Empty((await service.SearchAsync(new FileSearchQuery(null, null))).Items);
        Assert.Empty((await service.SearchAsync(new FileSearchQuery("   ", null))).Items);
    }

    /// <summary>
    /// 结果必须封顶：先多取候选再过滤敏感项，避免敏感项吃掉预算（复审 I-3 的既有约束）。
    /// </summary>
    [Fact]
    public async Task SearchAsync_CapsResultsAtTwenty()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 40; i++)
        {
            SeedItem(db, provider, $"/文档/报告{i:D2}.txt", $"报告{i:D2}.txt", "text/plain");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("报告", null));

        Assert.Equal(20, result.Items.Count);
    }

    /// <summary>
    /// MCP 合约声明了 search_files 的 page/pageSize，因此必须真的翻页：
    /// 第 2 页应返回不同的项，而不是把第一页再发一遍。
    /// </summary>
    [Fact]
    public async Task SearchAsync_PaginatesThroughResults()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 25; i++)
        {
            SeedItem(db, provider, $"/文档/报告{i:D2}.txt", $"报告{i:D2}.txt", "text/plain");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page1 = await service.SearchAsync(new FileSearchQuery("报告", null), page: 1, pageSize: 10);
        var page2 = await service.SearchAsync(new FileSearchQuery("报告", null), page: 2, pageSize: 10);

        Assert.Equal(10, page1.Items.Count);
        Assert.Equal(10, page2.Items.Count);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id)));
    }

    /// <summary>
    /// 敏感项必须在**分页之前**被过滤：否则它会占掉页名额，
    /// 使返回条数少于 pageSize 且后续页错位。
    /// </summary>
    [Fact]
    public async Task SearchAsync_FiltersSensitiveBeforePaging()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        // 敏感项排在最前（按名称排序），若先分页就会被它占掉名额
        SeedItem(db, provider, "/Secrets/报告A.txt", "报告A.txt", "text/plain");
        SeedItem(db, provider, "/文档/报告B.txt", "报告B.txt", "text/plain");
        SeedItem(db, provider, "/文档/报告C.txt", "报告C.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page1 = await service.SearchAsync(new FileSearchQuery("报告", null), page: 1, pageSize: 2);

        Assert.Equal(2, page1.Items.Count);
        Assert.DoesNotContain(page1.Items, i => i.Path.StartsWith("/Secrets", StringComparison.Ordinal));
    }

    /// <summary>默认单页签名仍返回最多 20 条（旧行为不变）。</summary>
    [Fact]
    public async Task SearchAsync_DefaultOverload_KeepsTwentyLimit()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 30; i++)
        {
            SeedItem(db, provider, $"/文档/报告{i:D2}.txt", $"报告{i:D2}.txt", "text/plain");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("报告", null));

        Assert.Equal(20, result.Items.Count);
    }

    /// <summary>
    /// REQ-8 / P7（PR-1）：全盘搜索结果按 100/页翻页，且总数是**全部命中数**，
    /// 不是「当前取回的候选数」。旧实现先 `Take(60)` 再分页，第 2 页起会凭空丢结果。
    /// </summary>
    [Fact]
    public async Task SearchAsync_PagesThroughMoreCandidatesThanTheOldSixtyItemWindow()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 150; i++)
        {
            SeedItem(db, provider, $"/文档/报告{i:D3}.txt", $"报告{i:D3}.txt", "text/plain");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page1 = await service.SearchAsync(new FileSearchQuery("报告", null), page: 1, pageSize: 100);
        var page2 = await service.SearchAsync(new FileSearchQuery("报告", null), page: 2, pageSize: 100);

        Assert.Equal(150, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(100, page1.Items.Count);
        Assert.Equal(50, page2.Items.Count);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id)));
    }

    /// <summary>
    /// 反面（AC-8.3）：敏感路径既不进结果，也不进总数——否则「共 N 项」会泄漏被保护文件的数量，
    /// 而且会让分页出现空页。
    /// </summary>
    [Fact]
    public async Task SearchAsync_SensitivePathsAreExcludedFromTotalsToo()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 5; i++)
        {
            SeedItem(db, provider, $"/Secrets/报告S{i}.txt", $"报告S{i}.txt", "text/plain");
        }

        for (var i = 0; i < 7; i++)
        {
            SeedItem(db, provider, $"/文档/报告{i}.txt", $"报告{i}.txt", "text/plain");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("报告", null), page: 1, pageSize: 100);

        Assert.Equal(7, result.TotalCount);
        Assert.All(result.Items, i => Assert.DoesNotContain("/Secrets", i.Path));
    }

    /// <summary>反面：翻到超出末页时返回空列表而不是把最后一页再发一遍。</summary>
    [Fact]
    public async Task SearchAsync_PageBeyondTheEndReturnsEmpty()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 5; i++)
        {
            SeedItem(db, provider, $"/文档/报告{i}.txt", $"报告{i}.txt", "text/plain");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("报告", null), page: 9, pageSize: 100);

        Assert.Empty(result.Items);
        Assert.Equal(5, result.TotalCount);
    }

    /// <summary>
    /// 复审 Critical：<c>IsProtected</c> 用 OrdinalIgnoreCase，而 SQL 的 <c>=</c>/<c>LIKE</c>
    /// 在非 C 排序规则下大小写敏感。受保护**目录本身**的大小写变体必须同样被排除，
    /// 否则会把它连同总数一起泄漏出去。
    /// </summary>
    [Theory]
    [InlineData("/secrets/密码.txt")]
    [InlineData("/SECRETS/密码.txt")]
    [InlineData("/Secrets/密码.txt")]
    [InlineData("/passwords/密码.txt")]
    public async Task SearchAsync_ExcludesProtectedDirectoriesRegardlessOfPathCase(string protectedPath)
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, protectedPath, "密码.txt", "text/plain");
        SeedItem(db, provider, "/文档/说明.txt", "说明.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("txt", null), page: 1, pageSize: 100);

        Assert.Equal(1, result.TotalCount);
        Assert.DoesNotContain(result.Items, i => i.Path.Contains("ecrets", StringComparison.OrdinalIgnoreCase)
            || i.Path.Contains("asswords", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>复审 Critical：受保护目录**自身**那一行也不得出现在结果与总数里。</summary>
    [Theory]
    [InlineData("/secrets")]
    [InlineData("/Secrets")]
    [InlineData("/SECRETS")]
    public async Task SearchAsync_ExcludesTheProtectedDirectoryRowItselfRegardlessOfCase(string protectedRoot)
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        // 目录名里带关键词，命中搜索词
        SeedItem(db, provider, protectedRoot, "secrets", "text/plain");
        SeedItem(db, provider, "/文档/说明.txt", "说明.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("secrets", null), page: 1, pageSize: 100);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    /// <summary>
    /// 段边界：<c>/Secrets0</c>、<c>/SecretsArchive</c> 不是受保护目录，不得被误伤
    /// （与 <c>IsProtected</c> 的「段边界匹配」一致）。
    /// </summary>
    [Fact]
    public async Task SearchAsync_DoesNotExcludePathsThatMerelyShareTheProtectedPrefix()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/Secrets0/note.txt", "note.txt", "text/plain");
        SeedItem(db, provider, "/SecretsArchive/note2.txt", "note2.txt", "text/plain");
        SeedItem(db, provider, "/Secrets/secret.txt", "secret.txt", "text/plain");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var result = await service.SearchAsync(new FileSearchQuery("note", null), page: 1, pageSize: 100);

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, i => i.Name == "note.txt");
        Assert.Contains(result.Items, i => i.Name == "note2.txt");
    }

    /// <summary>
    /// SQL 谓词与 <c>SensitivePathPolicy.IsProtected</c> 必须同口径：
    /// 对一组路径逐一比对「策略判定」与「搜索结果是否保留」。
    /// </summary>
    [Theory]
    [InlineData("/Secrets/a.txt", true)]
    [InlineData("/secrets/a.txt", true)]
    [InlineData("/SEcrets/a.txt", true)]
    [InlineData("/Secrets", true)]
    [InlineData("/secrets", true)]
    [InlineData("/Passwords/x.txt", true)]
    [InlineData("/Secrets0/a.txt", false)]
    [InlineData("/Documents/a.txt", false)]
    [InlineData("/a.txt", false)]
    public async Task SearchAsync_SqlPredicateMatchesSensitivePathPolicy(string path, bool protectedByPolicy)
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        var name = "probe-target.txt";
        SeedItem(db, provider, path, name, "text/plain");
        await db.SaveChangesAsync();

        var policy = new SensitivePathPolicy(null);
        Assert.Equal(protectedByPolicy, policy.IsProtected(path));

        var service = CreateService(db, UserId, policy);
        var result = await service.SearchAsync(new FileSearchQuery("probe-target", null), page: 1, pageSize: 100);

        // 策略判定 true -> 结果与总数都不出现；false -> 必须能找到
        Assert.Equal(protectedByPolicy ? 0 : 1, result.TotalCount);
        Assert.Equal(protectedByPolicy ? 0 : 1, result.Items.Count);
    }

    [Fact]
    public async Task SearchAsync_WhenNotLoggedIn_Throws1002()
    {
        await using var db = CreateDb();
        var service = CreateService(db, null);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.SearchAsync(new FileSearchQuery("x", null)));

        Assert.Equal(1002, error.ErrorCode);
    }

    /// <summary>
    /// 未注入 SensitivePathPolicy 实例时，必须按**配置**构造，而不是退回硬编码默认值——
    /// 否则自定义 `Files:SensitivePathPatterns` 会被静默忽略，用户以为加了保护其实没有。
    /// </summary>
    [Fact]
    public async Task SearchAsync_HonoursConfiguredSensitivePatterns()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/机密/方案.txt", "方案.txt", "text/plain");
        SeedItem(db, provider, "/文档/说明.txt", "说明.txt", "text/plain");
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:SensitivePathPatterns:0"] = "/机密/*",
            })
            .Build();
        var service = new FileSearchService(
            db, new StubCurrentUser(UserId), sensitivePolicy: null, configuration: configuration);

        var result = await service.SearchAsync(new FileSearchQuery(".txt", null));

        var item = Assert.Single(result.Items);
        Assert.Equal("说明.txt", item.Name);
    }
}
