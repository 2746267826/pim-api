using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// 第一轮独立 review（Codex）发现项的**回归测试**。
///
/// 每条用例对应一个具体发现：修好之前必须红、修好之后绿。
/// 这些不是「补几张断言」——它们锁定的是会丢数据 / 绕过安全闸门 / 永久漏项的行为。
/// </summary>
public class FilesReviewRegressionTests
{
    private static readonly Guid UserId = Guid.Parse("ffffffff-1111-2222-3333-444444444401");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");

    private sealed class TestSecretProtector : Pim.Infrastructure.Secrets.ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"files-review-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(
        PimDbContext db,
        string status = "connected",
        string? accountId = "acc-1",
        string provider = "onedrive",
        string? deltaLink = null,
        string clientId = "cid-1")
    {
        var row = new FileProviderEntity
        {
            UserId = UserId,
            Provider = provider,
            AccountId = accountId,
            ClientId = clientId,
            Status = status,
            DeltaLink = deltaLink,
            IsDefault = true,
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes("protected::refresh-1"),
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(row);
        db.SaveChanges();
        return row;
    }

    // ===================== Critical：过期账号不能被「重新授权」删掉 =====================

    /// <summary>
    /// 授权失效（expired）的**正式账号**点「重新授权」时，绝不能被当成待授权脏行清理掉：
    /// 那会级联删除它全部 file_items，并让已发出的闪念附件（storage_account_id 指向该行）
    /// 永远读不回来。
    /// </summary>
    [Fact]
    public async Task StartAsync_MustNotPurgeExpiredConnectedAccount_OrItsItems()
    {
        await using var db = CreateDb();
        var expired = SeedProvider(db, status: "expired");
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = expired.Id,
            ExternalFileId = "keep-me",
            Path = "/合同/keep.pdf",
            Name = "keep.pdf",
            ItemType = "file",
        });
        await db.SaveChangesAsync();

        var graph = new FakeOneDriveGraphClient();
        using var harness = new FilesProviderHarness(db, graph, new TestSecretProtector(), new HarnessClock(Now));
        var service = CreateAuthorizationService(db, harness, graph);

        var start = await service.StartAsync(UserId, "onedrive", "cid-1");

        // 旧账号行与它的索引必须原样保留
        Assert.True(await db.Set<FileProviderEntity>().AnyAsync(p => p.Id == expired.Id));
        Assert.True(await db.Set<FileItemEntity>().AnyAsync(i => i.ExternalFileId == "keep-me"));
        // 并且这次重绑确实新建了一个待授权尝试
        Assert.NotEqual(expired.Id, start.ProviderId);

        // 用新授权重连同一账号 → 仍应回到**原来那一行**（唯一键不允许同账号两行）
        var status = await service.GetStatusAsync(UserId, start.ProviderId);
        Assert.Equal("connected", status.Status);
        var rows = await db.Set<FileProviderEntity>().Where(p => p.UserId == UserId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(expired.Id, row.Id);
        Assert.Equal(expired.Id, status.ProviderId);
        Assert.Equal("connected", row.Status);
    }

    /// <summary>
    /// 换 clientId 重绑同一账号：数据库里的 clientId 必须跟着换，否则会用 A 的客户端
    /// 去刷新 B 的 refresh token（invalid_client）。
    /// </summary>
    [Fact]
    public async Task StartAsync_SameAccountWithNewClientId_UpdatesClientId()
    {
        await using var db = CreateDb();
        var existing = SeedProvider(db, clientId: "client-A");

        var graph = new FakeOneDriveGraphClient();
        using var harness = new FilesProviderHarness(db, graph, new TestSecretProtector(), new HarnessClock(Now));
        var service = CreateAuthorizationService(db, harness, graph);

        var start = await service.StartAsync(UserId, "onedrive", "client-B");
        var status = await service.GetStatusAsync(UserId, start.ProviderId);

        Assert.Equal("connected", status.Status);
        var row = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == existing.Id);
        Assert.Equal("client-B", row.ClientId);
    }

    /// <summary>从未成功连接过的待授权脏行仍然要清理，否则每次点绑定都会堆一行。</summary>
    [Fact]
    public async Task StartAsync_PurgesOnlyNeverConnectedAttempts()
    {
        await using var db = CreateDb();
        SeedProvider(db, status: "expired", accountId: null, clientId: "cid-1");
        SeedProvider(db, status: "pending", accountId: null, clientId: "cid-1");
        await db.SaveChangesAsync();

        var graph = new FakeOneDriveGraphClient();
        using var harness = new FilesProviderHarness(db, graph, new TestSecretProtector(), new HarnessClock(Now));
        var service = CreateAuthorizationService(db, harness, graph);

        await service.StartAsync(UserId, "onedrive", "cid-1");

        // 两条从未连接的尝试被清掉，只剩本次新建的那一条
        Assert.Equal(1, await db.Set<FileProviderEntity>().CountAsync());
    }

    private static FileProviderAuthorizationService CreateAuthorizationService(
        PimDbContext db,
        FilesProviderHarness harness,
        FakeOneDriveGraphClient graph)
        => new(
            db,
            harness.Providers,
            new IFileProviderAuthorizer[] { new OneDriveAuthorizer(graph) },
            new TestSecretProtector(),
            logger: null,
            clock: new HarnessClock(Now),
            credentials: harness.Credentials,
            tokenCache: harness.Get<FileProviderTokenCache>());

    // ===================== Important：同步引擎的漏项与路径 =====================

    private static FileTreeSyncEngine CreateEngine(PimDbContext db, FakeOneDriveGraphClient graph)
    {
        var harness = new FilesProviderHarness(db, graph, new TestSecretProtector(), new HarnessClock(Now));
        return new FileTreeSyncEngine(db, harness.Sessions, harness.Tokens, harness.Providers, null, new HarnessClock(Now));
    }

    /// <summary>
    /// 有条目解析不出父路径时**不得推进游标**：否则这些变化再也不会被投递，成为永久漏项。
    /// </summary>
    [Fact]
    public async Task Sync_WithUnresolvedItems_KeepsCursorAndReportsError()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, deltaLink: "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=old");
        var graph = new FakeOneDriveGraphClient();
        // 子项声称的父目录既不在索引里，远端也回读不到（ItemsById 为空）。
        // 注意不能用 OneDriveDeltaPageFactory.File 的默认 parentPath：那会补成 "/drive/root:"，
        // 路径当场就能算出来，反而测不到「解析不出」这条分支。
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            new OneDriveDeltaItem(
                "orphan-1", "missing-parent", null, "orphan.txt",
                IsFolder: false, IsRemoved: false, Size: 1, MimeType: "text/plain",
                Ctag: "ctag", ModifiedAt: Now)));
        var engine = CreateEngine(db, graph);

        var result = await engine.SyncAsync(provider.Id);

        Assert.Equal(1, result.ItemsDeferred);
        var updated = await db.Set<FileProviderEntity>().AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=old", updated.DeltaLink);
        // 不能静默：状态与原因都要暴露出来
        Assert.Equal("error", updated.SyncStatus);
        Assert.Contains("未落地", updated.LastError!);
        // 没有臆造一条路径错误的行
        Assert.False(await db.Set<FileItemEntity>().AnyAsync(i => i.ExternalFileId == "orphan-1"));
    }

    /// <summary>
    /// 后到的删除必须压过先到的 deferred upsert：否则会把已经删掉的文件在补解析阶段复活。
    /// </summary>
    [Fact]
    public async Task Sync_RemovalInLaterPage_BeatsEarlierDeferredUpsert()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, deltaLink: "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=old");
        var existing = new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = "f-1",
            Path = "/old.txt",
            Name = "old.txt",
            ItemType = "file",
        };
        db.Set<FileItemEntity>().Add(existing);
        await db.SaveChangesAsync();

        var graph = new FakeOneDriveGraphClient();
        // 第一页：改名（父目录解析不出 → 进 deferred）
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.NextPage(
            new OneDriveDeltaItem(
                "f-1", "missing-parent", null, "renamed.txt",
                IsFolder: false, IsRemoved: false, Size: 1, MimeType: "text/plain",
                Ctag: "ctag", ModifiedAt: Now)));
        // 第二页：同一条被删除
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(OneDriveDeltaPageFactory.Removed("f-1")));
        var engine = CreateEngine(db, graph);

        await engine.SyncAsync(provider.Id);

        var row = await db.Set<FileItemEntity>().AsNoTracking().SingleAsync(i => i.ExternalFileId == "f-1");
        Assert.True(row.IsDeleted);
        Assert.Equal("/old.txt", row.Path);
    }

    /// <summary>
    /// 目录改名/移动时，远端**不会**为每个后代各发一条变更（Drive 尤其如此）。
    /// 只改目录自身会让子孙留着旧路径 —— 列表/搜索显示错位置，敏感路径闸门还会按旧路径放行。
    /// </summary>
    [Fact]
    public async Task Sync_FolderRename_RewritesDescendantPaths()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, deltaLink: "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=old");
        db.Set<FileItemEntity>().AddRange(
            new FileItemEntity
            {
                ProviderId = provider.Id, ExternalFileId = "folder-secret", Path = "/Secrets",
                Name = "Secrets", ItemType = "folder",
            },
            new FileItemEntity
            {
                ProviderId = provider.Id, ExternalFileId = "file-inside", Path = "/Secrets/keys.txt",
                Name = "keys.txt", ItemType = "file",
            });
        await db.SaveChangesAsync();

        var graph = new FakeOneDriveGraphClient();
        // 只有父目录那一条变更：/Secrets → /Public（父是盘根，因此父路径是 "/drive/root:"）
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.Folder("folder-secret", "Public", parentId: "root-1", parentPath: "/drive/root:")));
        var engine = CreateEngine(db, graph);

        await engine.SyncAsync(provider.Id);

        var folder = await db.Set<FileItemEntity>().AsNoTracking().SingleAsync(i => i.ExternalFileId == "folder-secret");
        var child = await db.Set<FileItemEntity>().AsNoTracking().SingleAsync(i => i.ExternalFileId == "file-inside");
        Assert.Equal("/Public", folder.Path);
        // 子孙必须跟着搬走，否则 /Secrets 的保护会被绕过
        Assert.Equal("/Public/keys.txt", child.Path);
    }

    /// <summary>
    /// 重绑会清空游标 → 下一次同步是「无游标的全量」。
    /// 这种全量**必须**清理缺失项，否则授权离线期间远端删掉的文件会永远留在索引里。
    /// </summary>
    [Fact]
    public async Task FullSync_WithoutCursor_SoftDeletesItemsMissingFromRemote()
    {
        await using var db = CreateDb();
        // 无游标 + 已有旧索引 = 重绑后的首扫
        var provider = SeedProvider(db, deltaLink: null);
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = "ghost",
            Path = "/ghost.txt",
            Name = "ghost.txt",
            ItemType = "file",
            LastSeenAt = Now.AddDays(-3),
        });
        await db.SaveChangesAsync();

        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("survivor", "ok.txt")));
        var engine = CreateEngine(db, graph);

        var result = await engine.SyncAsync(provider.Id);

        Assert.True(result.FullRecrawl);
        Assert.Equal(1, result.ItemsDeleted);
        Assert.True((await db.Set<FileItemEntity>().AsNoTracking().SingleAsync(i => i.ExternalFileId == "ghost")).IsDeleted);
        Assert.False((await db.Set<FileItemEntity>().AsNoTracking().SingleAsync(i => i.ExternalFileId == "survivor")).IsDeleted);
    }

    // ===================== Important：上传登记不得被客户端伪造 =====================

    /// <summary>
    /// 上传会话完成登记时，客户端给的条目 id 必须与服务端回读一致，
    /// 且条目必须真的落在声明的目标目录 —— 否则可以拿一个敏感文件的 id 谎称上传到了 /Public，
    /// 之后所有敏感路径闸门都会按伪造路径放行。
    /// </summary>
    [Fact]
    public async Task RegisterUploadedFile_RejectsMismatchedItemIdAndForeignParent()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = "root-ext",
            Path = "/",
            Name = "root",
            ItemType = "folder",
        });
        db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = "public-folder",
            Path = "/Public",
            Name = "Public",
            ItemType = "folder",
        });
        await db.SaveChangesAsync();

        var graph = new FakeOneDriveGraphClient();
        // 敏感文件：真实落在 /Secrets 下
        graph.ItemsById["secret-item"] = new OneDrivePathItem(
            "secret-item", "keys.txt", 10, "text/plain", "/drive/root:/Secrets", IsFolder: false, ParentId: "secrets-folder");
        // 回读「claimed-id」时服务端实际返回的是另一个条目 → 必须被 id 核对拦下
        graph.ItemsById["claimed-id"] = new OneDrivePathItem(
            "actual-other-id", "keys.txt", 10, "text/plain", "/drive/root:/Public", IsFolder: false, ParentId: "public-folder");
        using var harness = new FilesProviderHarness(db, graph, new TestSecretProtector(), new HarnessClock(Now));
        var audit = new StubAuditLog();
        var service = new FileWriteService(
            db, harness.Sessions, harness.Providers, new StubCurrentUser(UserId), audit,
            logger: null, clock: new HarnessClock(Now));

        // 1) 客户端声明的 id 与服务端回读到的条目 id 不一致
        var wrongId = await Assert.ThrowsAsync<DomainException>(
            () => service.RegisterUploadedFileAsync(provider.Id, "/Public", "keys.txt", "claimed-id"));
        Assert.Equal(5300, wrongId.ErrorCode);

        // 2) id 对得上、但条目根本不在声明的目录里
        var foreignParent = await Assert.ThrowsAsync<DomainException>(
            () => service.RegisterUploadedFileAsync(provider.Id, "/Public", "keys.txt", "secret-item"));
        Assert.Equal(5300, foreignParent.ErrorCode);

        // 敏感文件不得被登记成 /Public
        Assert.False(await db.Set<FileItemEntity>().AnyAsync(i => i.ExternalFileId == "secret-item"));
    }

    private sealed class StubCurrentUser(Guid userId) : Pim.Infrastructure.Auth.ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
