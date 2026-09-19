using Xunit;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveSyncService delta 爬取语义测试：分页、幂等、软删、410 重扫、429 退避、进度落库。
/// </summary>
public class OneDriveSyncServiceTests
{
    private static readonly Guid UserId = Guid.Parse("eeeeeeee-1111-2222-3333-444444444444");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"onedrive-sync-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(PimDbContext db, string? deltaLink = null)
    {
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "onedrive",
            ClientId = "cid",
            Status = "connected",
            DriveId = "drive-1",
            AccountId = "acc-1",
            AccountName = "Test User",
            DeltaLink = deltaLink,
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes("protected::refresh-token"),
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    private static FileItemEntity SeedItem(PimDbContext db, Guid providerId, string externalId, string path, DateTimeOffset seenAt)
    {
        var item = new FileItemEntity
        {
            ProviderId = providerId,
            ExternalFileId = externalId,
            Path = path,
            Name = path.Split('/').LastOrDefault() ?? externalId,
            ItemType = "file",
            LastSeenAt = seenAt,
            SyncedAt = seenAt,
        };
        db.Set<FileItemEntity>().Add(item);
        db.SaveChanges();
        return item;
    }

    private static OneDriveSyncService CreateService(
        PimDbContext db,
        FakeOneDriveGraphClient graph,
        ISecretProtector? protector = null)
    {
        return new OneDriveSyncService(
            db,
            graph,
            new OneDriveTokenService(db, graph, protector ?? new TestSecretProtector(), clock: new FixedClock(Now)),
            NullLogger<OneDriveSyncService>.Instance,
            new FixedClock(Now));
    }

    [Fact]
    public async Task FirstSync_FollowsPages_CreatesTree_WithPaths_AndStoresDeltaLink()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        // 子项先于父文件夹到达（delta 不保证顺序）
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.NextPage(
            OneDriveDeltaPageFactory.File("file-1", "房屋租赁合同.pdf", parentId: "folder-1", parentPath: "/drive/root:/合同")));
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.Folder("folder-1", "合同", parentId: "root-1"),
            OneDriveDeltaPageFactory.Folder("root-1", "root", parentPath: "/drive")));
        var service = CreateService(db, graph);

        var result = await service.SyncAsync(provider.Id);

        Assert.Equal(2, result.PagesProcessed);
        Assert.Equal(3, result.ItemsApplied);
        Assert.False(result.FullRecrawl);
        var items = await db.Set<FileItemEntity>()
            .Where(item => item.ProviderId == provider.Id)
            .ToListAsync();
        Assert.Equal(3, items.Count);
        var root = items.Single(item => item.ExternalFileId == "root-1");
        Assert.Equal("folder", root.ItemType);
        Assert.Equal("/", root.Path);
        var folder = items.Single(item => item.ExternalFileId == "folder-1");
        Assert.Equal("/合同", folder.Path);
        Assert.Equal("root-1", folder.ParentExternalFileId);
        var file = items.Single(item => item.ExternalFileId == "file-1");
        Assert.Equal("/合同/房屋租赁合同.pdf", file.Path);
        Assert.Equal("folder-1", file.ParentExternalFileId);
        Assert.Equal(1024, file.Size);
        var updatedProvider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        Assert.Contains("$deltatoken=done-", updatedProvider.DeltaLink);
        Assert.Equal(3, updatedProvider.SyncedItemCount);
        Assert.NotNull(updatedProvider.LastSyncAt);
        Assert.Null(updatedProvider.LastError);
        Assert.Equal("idle", updatedProvider.SyncStatus);
    }

    [Fact]
    public async Task Resync_SamePages_IsIdempotent()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.Folder("folder-1", "合同")));
        var service = CreateService(db, graph);

        await service.SyncAsync(provider.Id);

        // 第二轮用全新 service + 相同页（模拟重复推送）
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.Folder("folder-1", "合同")));
        await service.SyncAsync(provider.Id);

        var items = await db.Set<FileItemEntity>()
            .Where(item => item.ProviderId == provider.Id)
            .ToListAsync();
        Assert.Single(items);
        Assert.Equal("合同", items[0].Name);
    }

    [Fact]
    public async Task Resync_WithChangedCtag_UpdatesExistingRow()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-1", "a.txt", size: 100, ctag: "ctag-v1")));
        var service = CreateService(db, graph);
        await service.SyncAsync(provider.Id);

        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-1", "a.txt", size: 250, ctag: "ctag-v2")));
        await service.SyncAsync(provider.Id);

        var items = await db.Set<FileItemEntity>()
            .Where(item => item.ProviderId == provider.Id)
            .ToListAsync();
        Assert.Single(items);
        Assert.Equal(250, items[0].Size);
        Assert.Equal("ctag-v2", items[0].Etag);
    }

    [Fact]
    public async Task RemovedEntry_SoftDeletes_Item()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-1", "a.txt")));
        var service = CreateService(db, graph);
        await service.SyncAsync(provider.Id);

        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.Removed("file-1")));
        var result = await service.SyncAsync(provider.Id);

        Assert.Equal(1, result.ItemsDeleted);
        var item = await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "file-1");
        Assert.True(item.IsDeleted);
        Assert.NotNull(item.DeletedAt);
    }

    [Fact]
    public async Task DeltaReset_410_FullRecrawl_SoftDeletesStaleItems_AndRecordsReset()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, deltaLink: "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=stale");
        SeedItem(db, provider.Id, "file-stale", "/旧文件.txt", seenAt: Now.AddDays(-7));
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(new OneDriveGraphException(410, null, "resyncRequired"));
        // 重扫后只剩 folder-keep；旧的 file-stale 不再出现
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.Folder("folder-keep", "保留")));
        var service = CreateService(db, graph);

        var result = await service.SyncAsync(provider.Id);

        Assert.True(result.FullRecrawl);
        var stale = await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "file-stale");
        Assert.True(stale.IsDeleted);
        var keep = await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "folder-keep");
        Assert.False(keep.IsDeleted);
        var updatedProvider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        Assert.Contains("$deltatoken=done-", updatedProvider.DeltaLink!);
        Assert.NotNull(updatedProvider.DeltaResetAt);
    }

    [Fact]
    public async Task Throttled_429_RetriesSameUrl_ThenContinues()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(new OneDriveGraphException(429, 0, "activityLimitReached"));
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-1", "a.txt")));
        var service = CreateService(db, graph);

        var result = await service.SyncAsync(provider.Id);

        Assert.Equal(1, result.ItemsApplied);
        Assert.Equal(2, graph.DeltaRequests.Count);
        // 重试必须用同一个 URL（不是下一页）
        Assert.Equal(graph.DeltaRequests[0].Url, graph.DeltaRequests[1].Url);
    }

    [Fact]
    public async Task Throttled_429_ExceedsRetryBudget_ThrowsAndMarksError()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(new OneDriveGraphException(429, 0, "activityLimitReached"));
        graph.DeltaScript.Enqueue(new OneDriveGraphException(429, 0, "activityLimitReached"));
        graph.DeltaScript.Enqueue(new OneDriveGraphException(429, 0, "activityLimitReached"));
        var service = CreateService(db, graph);

        await Assert.ThrowsAsync<OneDriveGraphException>(() => service.SyncAsync(provider.Id));

        var updatedProvider = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("error", updatedProvider.SyncStatus);
        Assert.NotNull(updatedProvider.LastError);
    }

    [Fact]
    public async Task Sync_NonOneDriveProvider_Throws()
    {
        await using var db = CreateDb();
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "nextcloud",
            BaseUrl = "https://cloud.example.com",
            Username = "me",
            Status = "connected",
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<DomainException>(() => service.SyncAsync(provider.Id));
    }

    [Fact]
    public async Task Sync_NotConnectedProvider_Throws()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        provider.Status = "pending";
        db.SaveChanges();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<DomainException>(() => service.SyncAsync(provider.Id));
    }

    [Fact]
    public async Task Sync_UnknownProvider_Throws()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<DomainException>(() => service.SyncAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Sync_UsesStoredDeltaLink_AsFirstRequest()
    {
        await using var db = CreateDb();
        SeedProvider(db, deltaLink: "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=cursor");
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page());
        var service = CreateService(db, graph);

        await service.SyncAsync(Guid.Parse(db.Set<FileProviderEntity>().First().Id.ToString()));

        var first = graph.DeltaRequests[0];
        Assert.Contains("$deltatoken=cursor", first.Url);
    }

    [Fact]
    public async Task Gone410_OnDefaultStartUrl_FailsInsteadOfInfiniteLoop()
    {
        await using var db = CreateDb();
        SeedProvider(db, deltaLink: null); // 首次全量，起点即默认 URL
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(new OneDriveGraphException(410, null, "resyncRequired"));
        var service = CreateService(db, graph);

        await Assert.ThrowsAsync<OneDriveGraphException>(() => service.SyncAsync(
            db.Set<FileProviderEntity>().Single().Id));
        // 只请求了一次，没有循环
        Assert.Single(graph.DeltaRequests);
    }

    [Fact]
    public async Task Unauthorized401_MidCrawl_RefreshesTokenAndRetriesSameUrl()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, deltaLink: "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=cursor");
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(new OneDriveGraphException(401, null, "token expired"));
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-1", "a.txt")));
        var service = CreateService(db, graph);

        var result = await service.SyncAsync(provider.Id);

        Assert.Equal(1, result.ItemsApplied);
        // 401 后用同一 URL 重试
        Assert.Equal(2, graph.DeltaRequests.Count);
        Assert.Equal(graph.DeltaRequests[0].Url, graph.DeltaRequests[1].Url);
        var updated = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("idle", updated.SyncStatus);
    }

    [Fact]
    public async Task Sync_MarksSyncing_WhileRunning_AndIdleAfter()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        graph.DeltaScript.Enqueue(OneDriveDeltaPageFactory.Page(
            OneDriveDeltaPageFactory.File("file-1", "a.txt")));
        var service = CreateService(db, graph);

        await service.SyncAsync(provider.Id);

        var updated = await db.Set<FileProviderEntity>().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("idle", updated.SyncStatus);
    }
}
