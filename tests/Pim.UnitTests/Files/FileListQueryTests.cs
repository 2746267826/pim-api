using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// REQ-2 / REQ-3 / REQ-8 / REQ-9（工单 WO-FILES-20260923，PR-1）：
/// <c>GET /files/items</c> 的直属子项语义、服务端排序、分页完整性与当前文件夹过滤。
///
/// 这些用例锁定的是**行为契约**：无论底层是内存还是 Postgres，直属子项集合、排序键与分页
/// 都不许随整棵子树的规模变化。数据库侧的翻译与索引使用由
/// <see cref="FileListQueryRealDbTests"/> 用真 Postgres 单独验证。
/// </summary>
public class FileListQueryTests
{
    private static readonly Guid UserId = Guid.Parse("abababab-1111-2222-3333-444444444481");
    private static readonly Guid OtherUserId = Guid.Parse("abababab-9999-8888-7777-666666666681");

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-list-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(PimDbContext db, Guid userId)
    {
        var provider = new FileProviderEntity { UserId = userId, Provider = "onedrive", Status = "connected" };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    private static FileItemEntity Seed(
        PimDbContext db,
        FileProviderEntity provider,
        string path,
        string name,
        string type = "file",
        long? size = null,
        DateTimeOffset? modifiedAt = null,
        bool deleted = false)
    {
        var item = new FileItemEntity
        {
            ProviderId = provider.Id,
            Provider = provider,
            ExternalFileId = Guid.NewGuid().ToString("N"),
            Path = path,
            Name = name,
            ItemType = type,
            Size = size,
            IsDeleted = deleted,
            DeletedAt = deleted ? DateTimeOffset.UnixEpoch : null,
            ModifiedAt = modifiedAt ?? DateTimeOffset.UnixEpoch,
        };
        db.Set<FileItemEntity>().Add(item);
        return item;
    }

    private static FileOperationService CreateService(PimDbContext db, Guid? userId)
        => new(db, new StubCurrentUser(userId), new StubAuditLog());

    /// <summary>
    /// 反面（AC-2.3）：同一 provider 下前缀相近的兄弟目录不许混入。
    /// <c>/main</c> 的直属子项只应来自 <c>/main/</c>，不能把 <c>/main0.txt</c>（前缀区间内的同前缀串）
    /// 或 <c>/maintenance/…</c> 算进来——区间预筛是超集，必须由「是否仍含分隔符」把它收回来。
    /// </summary>
    [Fact]
    public async Task ListItemsAsync_ExcludesSiblingsThatOnlyShareThePathPrefix()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/main", "main", "folder");
        Seed(db, provider, "/main/report.txt", "report.txt");
        Seed(db, provider, "/main/sub", "sub", "folder");
        Seed(db, provider, "/main/sub/deep.txt", "deep.txt");
        Seed(db, provider, "/main0.txt", "main0.txt");          // shares "/main" but not "/main/"
        Seed(db, provider, "/maintenance", "maintenance", "folder");
        Seed(db, provider, "/maintenance/other.txt", "other.txt");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/main"));

        Assert.Equal(2, page.TotalCount);
        Assert.DoesNotContain(page.Items, i => i.Name == "main0.txt");
        Assert.DoesNotContain(page.Items, i => i.Name == "other.txt");
        Assert.DoesNotContain(page.Items, i => i.Name == "deep.txt");
        Assert.Contains(page.Items, i => i.Name == "report.txt");
        Assert.Contains(page.Items, i => i.Name == "sub");
    }

    /// <summary>
    /// 根目录自身（path <c>/</c>）是容器，不是自己的子项：根列表不得把它列出来。
    /// （区间是 <c>["/", "0")</c>，恰好包含 <c>/</c>，因此必须显式排除。）
    /// </summary>
    [Fact]
    public async Task ListItemsAsync_RootDoesNotListTheRootItemItself()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/", "OneDrive", "folder");
        Seed(db, provider, "/a.txt", "a.txt");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/"), page: 1, pageSize: 100);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("a.txt", Assert.Single(page.Items).Name);
    }

    /// <summary>REQ-8：当前文件夹模式的关键词过滤在服务端完成，且过滤发生在计数与分页之前。</summary>
    [Fact]
    public async Task ListItemsAsync_FiltersByNameBeforePagingAndCounting()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 25; i++)
        {
            Seed(db, provider, $"/文档/报告{i:D2}.txt", $"报告{i:D2}.txt");
            Seed(db, provider, $"/文档/其他{i:D2}.txt", $"其他{i:D2}.txt");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page1 = await service.ListItemsAsync(new FileListQuery("/文档", Q: "报告"), page: 1, pageSize: 10);
        var page3 = await service.ListItemsAsync(new FileListQuery("/文档", Q: "报告"), page: 3, pageSize: 10);

        Assert.Equal(25, page1.TotalCount);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal(10, page1.Items.Count);
        Assert.Equal(5, page3.Items.Count);
        Assert.All(page1.Items.Concat(page3.Items), i => Assert.Contains("报告", i.Name));
    }

    /// <summary>REQ-8 反面（AC-8.2）：关键词大小写不敏感，中文/英文一致。</summary>
    [Fact]
    public async Task ListItemsAsync_NameFilterIsCaseInsensitive()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/docs/Report-Q3.pdf", "Report-Q3.pdf");
        Seed(db, provider, "/docs/other.pdf", "other.pdf");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/docs", Q: "report"));

        Assert.Equal("Report-Q3.pdf", Assert.Single(page.Items).Name);
    }

    /// <summary>REQ-4：树只需目录，服务端按类型过滤，避免把上千个文件拉进树的数据源。</summary>
    [Fact]
    public async Task ListItemsAsync_TypeFolderReturnsFoldersOnly()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/图片", "图片", "folder");
        Seed(db, provider, "/图片/子相册", "子相册", "folder");
        Seed(db, provider, "/图片/a.jpg", "a.jpg");
        Seed(db, provider, "/图片/b.jpg", "b.jpg");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var folderPage = await service.ListItemsAsync(new FileListQuery("/图片", Type: "folder"));
        var filePage = await service.ListItemsAsync(new FileListQuery("/图片", Type: "file"));
        var allPage = await service.ListItemsAsync(new FileListQuery("/图片"));

        Assert.Equal("子相册", Assert.Single(folderPage.Items).Name);
        Assert.Equal(1, folderPage.TotalCount);
        Assert.Equal(2, filePage.Items.Count);
        Assert.All(filePage.Items, i => Assert.Equal("file", i.ItemType));
        Assert.Equal(3, allPage.TotalCount);
    }

    /// <summary>AC-2.2：默认排序为文件夹在前、名称升序；名称排序必须与大小写无关。</summary>
    [Fact]
    public async Task ListItemsAsync_DefaultSortIsFoldersThenNameAscending()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/zeta.txt", "zeta.txt");
        Seed(db, provider, "/Alpha.txt", "Alpha.txt");
        Seed(db, provider, "/middle", "middle", "folder");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/"));

        Assert.Equal(new[] { "middle", "Alpha.txt", "zeta.txt" }, page.Items.Select(i => i.Name).ToArray());
    }

    /// <summary>REQ-9.1：按修改时间排序（新→旧），文件夹仍在前。</summary>
    [Fact]
    public async Task ListItemsAsync_SortsByModifiedDescending()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/old.txt", "old.txt", modifiedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Seed(db, provider, "/new.txt", "new.txt", modifiedAt: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        Seed(db, provider, "/mid.txt", "mid.txt", modifiedAt: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        Seed(db, provider, "/folder", "folder", "folder", modifiedAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/", Sort: "modified", Order: "desc"));

        Assert.Equal(new[] { "folder", "new.txt", "mid.txt", "old.txt" }, page.Items.Select(i => i.Name).ToArray());
    }

    /// <summary>REQ-9.1：按大小排序；文件夹在前，空大小按 0 计，顺序稳定。</summary>
    [Fact]
    public async Task ListItemsAsync_SortsBySizeAscending()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/big.bin", "big.bin", size: 900);
        Seed(db, provider, "/small.bin", "small.bin", size: 10);
        Seed(db, provider, "/unknown.bin", "unknown.bin", size: null);
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/", Sort: "size", Order: "asc"));

        Assert.Equal(new[] { "unknown.bin", "small.bin", "big.bin" }, page.Items.Select(i => i.Name).ToArray());
    }

    /// <summary>
    /// 反面（AC-9.2）：分页顺序必须一致——翻完所有页得到的条目集合与单页全量完全一致，
    /// 不重不漏。排序键不稳定（例如只按名称、忽略 id）时这条会红。
    /// </summary>
    [Fact]
    public async Task ListItemsAsync_PagesDoNotOverlapOrDropItemsUnderNameSort()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        // 大量重名：排序必须靠 id 兜底做稳定键，否则跨页会重复/丢项
        for (var i = 0; i < 30; i++)
        {
            Seed(db, provider, $"/dup/report{i:D2}.txt", "同一份报告.txt");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var all = await service.ListItemsAsync(new FileListQuery("/dup"), page: 1, pageSize: 100);
        var pages = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var chunk = await service.ListItemsAsync(new FileListQuery("/dup"), page: page, pageSize: 10);
            pages.AddRange(chunk.Items.Select(i => i.Id));
        }

        Assert.Equal(30, all.TotalCount);
        Assert.Equal(30, pages.Count);
        Assert.Equal(30, pages.Distinct().Count());
        Assert.Equal(all.Items.Select(i => i.Id).OrderBy(x => x), pages.OrderBy(x => x));
    }

    /// <summary>反面（AC-3.2）：分页大小按 P3 口径封顶 100，不允许静默返回超过一页的数据。</summary>
    [Fact]
    public async Task ListItemsAsync_ClampsPageSizeToOneHundred()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 150; i++)
        {
            Seed(db, provider, $"/many/f{i:D3}.txt", $"f{i:D3}.txt");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/many"), page: 1, pageSize: 5000);

        Assert.Equal(100, page.PageSize);
        Assert.Equal(100, page.Items.Count);
        Assert.Equal(150, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
    }

    /// <summary>反面：未知排序键不得抛错，回落到默认排序（前端缓存可能带着旧值或脏值）。</summary>
    [Fact]
    public async Task ListItemsAsync_UnknownSortKeyFallsBackToName()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/b.txt", "b.txt");
        Seed(db, provider, "/a.txt", "a.txt");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/", Sort: "bogus", Order: "sideways"));

        Assert.Equal(new[] { "a.txt", "b.txt" }, page.Items.Select(i => i.Name).ToArray());
    }

    /// <summary>反面：空目录返回 0 项、0 页，而不是 1 页的空列表（避免「第 1/1 页」的误导）。</summary>
    [Fact]
    public async Task ListItemsAsync_EmptyFolderReportsZeroPages()
    {
        await using var db = CreateDb();
        SeedProvider(db, UserId);
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/nothing-here"));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
    }

    /// <summary>多用户隔离：他人目录里的同名直属子项不得出现（REQ-30 回归）。</summary>
    [Fact]
    public async Task ListItemsAsync_IsScopedToTheCurrentUser()
    {
        await using var db = CreateDb();
        var mine = SeedProvider(db, UserId);
        var theirs = SeedProvider(db, OtherUserId);
        Seed(db, mine, "/doc/mine.txt", "mine.txt");
        Seed(db, theirs, "/doc/theirs.txt", "theirs.txt");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/doc"));

        Assert.Equal("mine.txt", Assert.Single(page.Items).Name);
    }

    /// <summary>反面：软删项不出现在列表，也不计入总数。</summary>
    [Fact]
    public async Task ListItemsAsync_ExcludesDeletedItemsFromCountAndPage()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        Seed(db, provider, "/doc/live.txt", "live.txt");
        Seed(db, provider, "/doc/gone.txt", "gone.txt", deleted: true);
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/doc"));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("live.txt", Assert.Single(page.Items).Name);
    }
}
