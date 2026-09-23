using System;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Services;
using Pim.UnitTests.Files;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// REQ-2 / REQ-3 的真库验证（工单 WO-FILES-20260923，PR-1）。
///
/// <para>
/// 内存 provider 会把 LINQ 当对象查询执行，证明不了「过滤与分页真的下推到了 SQL」——
/// 那正是本次要修的根因。这里在<b>一次性临时库</b>（真实迁移链 + 真实 Postgres）上铺一棵
/// 与工单实证同量级的合成树，然后用生产同款 EF 查询核对：
/// 直属子项精确、总数精确、耗时不随子树规模失控。
/// </para>
///
/// <para>
/// 需要 <c>PIM_TEST_CONN</c>（指向隔离实例，不可是 <c>pim_prod</c>）；不可用时显式 Skip。
/// 每次运行自建 <c>wo_files_list_{guid}</c> 库并在结束时删掉，绝不触碰镜像/生产库。
/// </para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class FileListQueryRealDbTests
{
    private static readonly Guid UserId = Guid.Parse("abababab-2222-3333-4444-555555555581");
    private static readonly Guid ProviderId = Guid.Parse("abababab-2222-3333-4444-555555555582");

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    /// <summary>
    /// 合成数据：与工单 references/02 的实证形状一致但对不上任何真实文件——
    /// 根目录 4 个直接子项；<c>/main</c> 子树 3 万个文件（200 个子目录 × 150）；
    /// <c>/main/deep</c> 单目录 4000 个直属文件（模拟「截图」类大目录）。
    /// </summary>
    private const string SeedSql =
        """
        INSERT INTO users (id, username, email, password_hash, role, is_active, created_at, updated_at)
        VALUES ('abababab-2222-3333-4444-555555555581', 'wo-realdb', 'wo-realdb@local.test',
                'x', 'user', true, now(), now());

        INSERT INTO file_providers (id, user_id, provider, base_url, username, app_password_secret,
                                    status, sync_status, synced_item_count, created_at, updated_at)
        VALUES ('abababab-2222-3333-4444-555555555582', 'abababab-2222-3333-4444-555555555581',
                'onedrive', 'https://graph.microsoft.com', 'wo-realdb', 'x',
                'connected', 'idle', 0, now(), now());

        -- 根容器本身（path = '/'）不是自己的子项
        INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type,
                                is_deleted, created_at, modified_at, synced_at)
        VALUES (gen_random_uuid(), 'abababab-2222-3333-4444-555555555582', 'ext:/', '/', 'OneDrive',
                'folder', false, now(), now(), now());

        INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type,
                                is_deleted, created_at, modified_at, synced_at)
        SELECT gen_random_uuid(), 'abababab-2222-3333-4444-555555555582',
               'ext:' || p, p, split_part(p, '/', 2), 'folder', false, now(), now(), now()
        FROM (VALUES ('/main'), ('/图片'), ('/Documents')) AS v(p);

        INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type,
                                is_deleted, created_at, modified_at, synced_at)
        VALUES (gen_random_uuid(), 'abababab-2222-3333-4444-555555555582', 'ext:/readme.md',
                '/readme.md', 'readme.md', 'file', false, now(), now(), now());

        -- /main 下 200 个子目录
        INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type,
                                is_deleted, created_at, modified_at, synced_at)
        SELECT gen_random_uuid(), 'abababab-2222-3333-4444-555555555582',
               'ext:/main/f' || g, '/main/f' || g, 'f' || g, 'folder', false, now(), now(), now()
        FROM generate_series(1, 200) AS g;

        -- 每个子目录 150 个文件 => /main 子树 3 万项
        INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type, mime_type,
                                size, is_deleted, created_at, modified_at, synced_at)
        SELECT gen_random_uuid(), 'abababab-2222-3333-4444-555555555582',
               'ext:/main/f' || f || '/doc' || g, '/main/f' || f || '/doc' || g || '.pdf',
               'doc' || g || '.pdf', 'file', 'application/pdf', (g % 900) * 2048,
               false, now(), now() - (g || ' minutes')::interval, now()
        FROM generate_series(1, 200) AS f, generate_series(1, 150) AS g;

        -- /main/deep：单目录 4000 个直属文件
        INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type,
                                is_deleted, created_at, modified_at, synced_at)
        VALUES (gen_random_uuid(), 'abababab-2222-3333-4444-555555555582', 'ext:/main/deep',
                '/main/deep', 'deep', 'folder', false, now(), now(), now());

        INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type, mime_type,
                                size, is_deleted, created_at, modified_at, synced_at)
        SELECT gen_random_uuid(), 'abababab-2222-3333-4444-555555555582',
               'ext:/main/deep/shot' || g, '/main/deep/shot' || g || '.jpg',
               'shot' || g || '.jpg', 'file', 'image/jpeg', 1024,
               false, now(), now(), now()
        FROM generate_series(1, 4000) AS g;

        ANALYZE file_items;
        """;

    private static FileOperationService CreateService(PimDbContext db)
        => new(db, new StubCurrentUser(UserId), new StubAuditLog());


    /// <summary>
    /// 建临时库前先把文件模块的 EF 配置登记进 <see cref="PimDbContext"/>：
    /// <see cref="TempMigrationDatabase"/> 自己不知道要用到哪些模块，不登记的话
    /// 拿到的 context 里没有 <c>FileItemEntity</c>（Cannot create a DbSet …）。
    /// </summary>
    private static async Task<TempMigrationDatabase> CreateMigratedTempDatabaseAsync(string prefix)
    {
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.Files.Entities.FileItemEntity).Assembly);
        var temp = await TempMigrationDatabase.CreateAsync(prefix);
        await temp.MigrateAsync();
        return temp;
    }


    /// <summary>
    /// AC-2.1 / AC-2.3：3 万项的子树存在时，根目录与 /main 的直属子项查询仍然精确、
    /// 并且在 P1 口径（≤5 秒）内返回。旧的「整子树 ToListAsync 再内存筛选」在同样数据上
    /// 会把子树全部物化成实体。
    /// </summary>
    [SkippableFact]
    public async Task ListItemsAsync_OnAThirtyThousandItemTree_IsExactAndWithinTheConfirmedBudget()
    {
        await using var temp = await CreateMigratedTempDatabaseAsync("wo_files_list");
        await temp.ExecuteAsync(SeedSql);

        // 迁移必须真的把前缀检索索引建出来——它是 REQ-2 性能目标的前提
        Assert.True(
            await temp.IndexExistsAsync("ix_file_items_provider_id_path_pattern"),
            "迁移未建立 ix_file_items_provider_id_path_pattern，前缀查询会退化成整表扫描");

        var service = CreateService(temp.Context);

        var stopwatch = Stopwatch.StartNew();
        var root = await service.ListItemsAsync(new FileListQuery("/"), page: 1, pageSize: 100);
        var rootMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        var main = await service.ListItemsAsync(new FileListQuery("/main"), page: 1, pageSize: 100);
        var mainMs = stopwatch.ElapsedMilliseconds;

        // 直属子项精确：根 = /main, /图片, /Documents, /readme.md（不含根容器自身，也不含任何更深层项）
        Assert.Equal(4, root.TotalCount);
        Assert.Equal(
            new[] { "图片", "Documents", "main", "readme.md" }.OrderBy(x => x),
            root.Items.Select(i => i.Name).OrderBy(x => x));
        Assert.DoesNotContain(root.Items, i => i.Path == "/");

        // /main 的直接子项 = 200 个 f* 目录 + deep 目录；30000 个深层文件一个都不许混进来
        Assert.Equal(201, main.TotalCount);
        Assert.Equal(100, main.Items.Count);
        Assert.All(main.Items, i => Assert.Equal("folder", i.ItemType));
        Assert.DoesNotContain(main.Items, i => i.Path.Contains(".pdf", StringComparison.Ordinal));

        Assert.True(rootMs <= 5000, $"根目录列表 {rootMs}ms 超出 P1 口径（≤5 秒）");
        Assert.True(mainMs <= 5000, $"/main 列表 {mainMs}ms 超出 P1 口径（≤5 秒）");

        // 记录真实耗时，便于人工复核（测试输出随 CI 日志留档）
        Console.WriteLine($"[REQ-2] 3 万项子树：根目录 {rootMs}ms，/main {mainMs}ms");
    }

    /// <summary>
    /// AC-3.1 / AC-3.2：4000 项的大目录显示真实总数、可翻到末页看到全部条目，
    /// 不存在 2000 项封顶或静默缺项。
    /// </summary>
    [SkippableFact]
    public async Task ListItemsAsync_OnAFourThousandItemFolder_PagesThroughEveryItemWithATrueTotal()
    {
        await using var temp = await CreateMigratedTempDatabaseAsync("wo_files_list");
        await temp.ExecuteAsync(SeedSql);

        var service = CreateService(temp.Context);

        var first = await service.ListItemsAsync(new FileListQuery("/main/deep"), page: 1, pageSize: 100);
        Assert.Equal(4000, first.TotalCount);
        Assert.Equal(40, first.TotalPages);
        Assert.Equal(100, first.Items.Count);

        var last = await service.ListItemsAsync(new FileListQuery("/main/deep"), page: 40, pageSize: 100);
        Assert.Equal(100, last.Items.Count);

        var beyond = await service.ListItemsAsync(new FileListQuery("/main/deep"), page: 41, pageSize: 100);
        Assert.Empty(beyond.Items);

        // 逐页取回必须恰好是 4000 个不同条目，不重不漏（AC-9.2 的稳定全序）
        var seen = new System.Collections.Generic.HashSet<Guid>();
        for (var page = 1; page <= 40; page++)
        {
            var chunk = await service.ListItemsAsync(new FileListQuery("/main/deep"), page: page, pageSize: 100);
            foreach (var item in chunk.Items)
            {
                Assert.True(seen.Add(item.Id), $"第 {page} 页出现重复条目 {item.Id}");
            }
        }

        Assert.Equal(4000, seen.Count);
    }

    /// <summary>
    /// REQ-2 / AC-2.3 的性能契约：前缀判据必须**直接作用在 path 列上**，
    /// 否则迁移里那条 <c>text_pattern_ops</c> 索引失效、退化成整表扫描。
    ///
    /// 守的是一个已经踩过的坑：为补回「尾斜杠规范化」曾写成 <c>rtrim(path,'/') LIKE ...</c>，
    /// 语义对了但包住被索引列后计划从 Bitmap Index Scan 变成 Parallel Seq Scan，
    /// 把 REQ-2 要消除的全表扫描请了回来。
    ///
    /// 做法：断言 EF 生成的**真实 SQL**（<c>ToQueryString</c>）形状——<c>LIKE</c> 的左侧
    /// 必须是裸列 <c>path</c>；一旦有人写成 <c>rtrim(path,'/') LIKE ...</c>，这条立刻转红。
    /// （计划形状的人工证据见 REQ-2 的 EXPLAIN 留档；这里守的是可自动化的那一部分。）
    /// </summary>
    [SkippableFact]
    public async Task ListItemsAsync_PrefixPredicateKeepsTheIndexedColumnBare()
    {
        await using var temp = await CreateMigratedTempDatabaseAsync("wo_files_index");
        await temp.ExecuteAsync(SeedSql);

        var query = FileOperationService.DirectChildren(temp.Context, UserId, "/main/deep");
        var sql = query.OrderBy(item => item.Name).Take(100).ToQueryString();

        // StartsWith 必须翻成作用在裸列上的 LIKE，而不是 rtrim(path) LIKE ...
        var withoutQuotes = sql.Replace("\"", string.Empty);
        Assert.Contains("path LIKE", withoutQuotes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rtrim(path", withoutQuotes, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// REQ-30 / AC-30.1（PR-1 触及的出口）：在**真库**上核对多用户隔离与软删过滤——
    /// 别人 provider 下的同级同名条目不得出现，软删条目既不进结果也不进总数。
    /// 内存 provider 不经过 SQL，这条补的是「下推到 SQL 之后归属过滤仍然生效」。
    /// </summary>
    [SkippableFact]
    public async Task ListItemsAsync_OnRealPostgres_KeepsUserIsolationAndSoftDeleteFilters()
    {
        await using var temp = await CreateMigratedTempDatabaseAsync("wo_files_isolation");
        await temp.ExecuteAsync(SeedSql);

        // 第二个用户 + 自己的 provider/条目（同名同目录，专门用来试探归属过滤）
        await temp.ExecuteAsync(
            """
            INSERT INTO users (id, username, email, password_hash, role, is_active, created_at, updated_at)
            VALUES ('abababab-2222-3333-4444-555555555583', 'wo-realdb-other', 'other@local.test',
                    'x', 'user', true, now(), now());

            INSERT INTO file_providers (id, user_id, provider, base_url, username, app_password_secret,
                                        status, sync_status, synced_item_count, created_at, updated_at)
            VALUES ('abababab-2222-3333-4444-555555555584', 'abababab-2222-3333-4444-555555555583',
                    'onedrive', 'https://graph.microsoft.com', 'other', 'x',
                    'connected', 'idle', 0, now(), now());

            -- 别人的同级同名目录与文件：当前用户绝不能看到
            INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type,
                                    is_deleted, created_at, modified_at, synced_at)
            VALUES (gen_random_uuid(), 'abababab-2222-3333-4444-555555555584', 'ext:/main',
                    '/main', 'main', 'folder', false, now(), now(), now()),
                   (gen_random_uuid(), 'abababab-2222-3333-4444-555555555584', 'ext:/readme.md',
                    '/readme.md', 'readme.md', 'file', false, now(), now(), now());

            -- 自己的一个软删条目：不得出现、也不得计数
            INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type,
                                    is_deleted, deleted_at, created_at, modified_at, synced_at)
            VALUES (gen_random_uuid(), 'abababab-2222-3333-4444-555555555582', 'ext:/gone.txt',
                    '/gone.txt', 'gone.txt', 'file', true, now(), now(), now(), now());

            ANALYZE file_items;
            """);

        var service = CreateService(temp.Context);
        var root = await service.ListItemsAsync(new FileListQuery("/"), page: 1, pageSize: 100);

        // 根的 4 个直属子项不变：没有别人的条目，也没有自己的软删条目
        Assert.Equal(4, root.TotalCount);
        Assert.DoesNotContain(root.Items, i => i.Name == "gone.txt");
        Assert.Equal(root.Items.Select(i => i.Id).Distinct().Count(), root.Items.Count);

        var folders = await service.ListItemsAsync(new FileListQuery("/", Type: "folder"), page: 1, pageSize: 100);
        Assert.Equal(3, folders.TotalCount);

        // 搜索同样不得跨用户：只应有自己的内容
        var search = new FileSearchService(temp.Context, new StubCurrentUser(UserId), new SensitivePathPolicy(null));
        var hits = await search.SearchAsync(new FileSearchQuery("readme", null), page: 1, pageSize: 100);
        Assert.Equal(1, hits.TotalCount);
        Assert.Equal("readme.md", Assert.Single(hits.Items).Name);
    }

    /// <summary>
    /// REQ-8 / AC-8.3：全盘搜索在真库上分页，敏感目录既不进结果也不进总数。
    /// </summary>
    [SkippableFact]
    public async Task SearchAsync_OnRealPostgres_ExcludesSensitivePathsFromResultsAndTotals()
    {
        await using var temp = await CreateMigratedTempDatabaseAsync("wo_files_search");
        await temp.ExecuteAsync(SeedSql);
        await temp.ExecuteAsync(
            """
            INSERT INTO file_items (id, provider_id, external_file_id, path, name, item_type, mime_type,
                                    is_deleted, created_at, modified_at, synced_at)
            SELECT gen_random_uuid(), 'abababab-2222-3333-4444-555555555582',
                   'ext:/Secrets/shot' || g, '/Secrets/shot' || g || '.jpg',
                   'shot' || g || '.jpg', 'file', 'image/jpeg', false, now(), now(), now()
            FROM generate_series(1, 50) AS g;
            """);

        var policy = new SensitivePathPolicy(null);
        var service = new FileSearchService(temp.Context, new StubCurrentUser(UserId), policy);

        var page1 = await service.SearchAsync(new FileSearchQuery("shot", null), page: 1, pageSize: 100);
        var page2 = await service.SearchAsync(new FileSearchQuery("shot", null), page: 2, pageSize: 100);

        // 只有 /main/deep 下的 4000 个命中；/Secrets 的 50 个既不进结果也不进总数
        Assert.Equal(4000, page1.TotalCount);
        Assert.Equal(40, page1.TotalPages);
        Assert.Equal(100, page1.Items.Count);
        Assert.Equal(100, page2.Items.Count);
        Assert.DoesNotContain(page1.Items, i => i.Path.StartsWith("/Secrets", StringComparison.Ordinal));
        Assert.DoesNotContain(page2.Items, i => i.Path.StartsWith("/Secrets", StringComparison.Ordinal));
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id)));
    }
}
