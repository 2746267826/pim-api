using Xunit;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveContentService 测试：稳定直链解析、敏感路径拦截、文本读写与编辑前快照、快照恢复。
/// </summary>
public class OneDriveContentServiceTests
{
    private static readonly Guid UserId = Guid.Parse("eeeeeeee-1111-2222-3333-444444444481");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    private sealed class StubCurrentUser(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role { get; } = "user";
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"onedrive-content-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static (FileProviderEntity Provider, FileItemEntity Item) SeedFile(
        PimDbContext db, string path = "/工作/a.txt", string mime = "text/plain")
    {
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "onedrive",
            ClientId = "cid",
            Status = "connected",
            RefreshTokenEncrypted = Encoding.UTF8.GetBytes("protected::refresh-token"),
            TokenExpiresAt = Now.AddHours(1),
        };
        var item = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "item-1",
            Path = path,
            Name = path.Split('/').LastOrDefault() ?? "a.txt",
            ItemType = "file",
            MimeType = mime,
            Size = 100,
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().Add(item);
        db.SaveChanges();
        return (provider, item);
    }

    private static OneDriveContentService CreateService(
        PimDbContext db,
        FakeOneDriveGraphClient graph,
        string[]? sensitivePatterns = null)
    {
        Microsoft.Extensions.Configuration.IConfiguration? config = null;
        if (sensitivePatterns is not null)
        {
            config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(sensitivePatterns.Select((p, i) => new KeyValuePair<string, string?>($"Files:SensitivePathPatterns:{i}", p)))
                .Build();
        }
        return new OneDriveContentService(
            db,
            graph,
            new OneDriveTokenService(db, graph, new TestSecretProtector(), clock: new FixedClock(Now)),
            new StubCurrentUser(UserId),
            new SensitivePathPolicy(config),
            NullLogger<OneDriveContentService>.Instance,
            new FixedClock(Now));
    }

    [Fact]
    public async Task GetContentLink_ReturnsFreshLink()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var link = await service.GetContentLinkAsync(item.Id);

        Assert.Equal("https://my.microsoftpersonalcontent.com/dl?tempauth=xyz", link);
        Assert.Equal("item-1", graph.DownloadUrlCalls.Single().ItemId);
        // token 获取发生过一次（刷新或缓存）
        Assert.Equal(1, graph.RefreshCalls);
    }

    [Fact]
    public async Task GetContentLink_UnknownItem_Throws404MappedCode()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetContentLinkAsync(Guid.NewGuid()));
        Assert.Equal(5104, error.ErrorCode);
    }

    [Fact]
    public async Task GetContentLink_FolderItem_ThrowsNotDownloadable()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        item.ItemType = "folder";
        db.SaveChanges();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetContentLinkAsync(item.Id));
        Assert.Equal(5332, error.ErrorCode);
    }

    [Fact]
    public async Task SensitivePath_ContentLink_TextAndPreview_AllBlocked()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db, path: "/Secrets/密钥.txt");
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        foreach (var attempt in new Func<Task>[]
                 {
                     () => service.GetContentLinkAsync(item.Id),
                     () => service.GetThumbnailLinkAsync(item.Id, "medium"),
                     () => service.GetPreviewLinkAsync(item.Id),
                     () => service.GetTextAsync(item.Id),
                     () => service.SaveTextAsync(item.Id, "new"),
                 })
        {
            var error = await Assert.ThrowsAsync<DomainException>(attempt);
            Assert.Equal(40303, error.ErrorCode);
        }

        // 全程没有任何 Graph 调用
        Assert.Empty(graph.DownloadUrlCalls);
        Assert.Empty(graph.ThumbnailCalls);
        Assert.Empty(graph.PreviewCalls);
        Assert.Empty(graph.DownloadSmallCalls);
        Assert.Empty(graph.PutCalls);
    }

    [Fact]
    public async Task Thumbnail_ReturnsLink()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db, mime: "image/png");
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var link = await service.GetThumbnailLinkAsync(item.Id, "medium");

        Assert.Equal("https://my.microsoftpersonalcontent.com/thumb?tempauth=t", link);
        Assert.Equal(("access-token-1", "item-1", "medium"), graph.ThumbnailCalls.Single());
    }

    [Fact]
    public async Task Preview_ReturnsEmbedUrl()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db, mime: "application/pdf");
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var link = await service.GetPreviewLinkAsync(item.Id);

        Assert.Equal("https://www.onedrive.com/preview?resid=x", link);
        Assert.Equal("item-1", graph.PreviewCalls.Single().ItemId);
    }

    [Fact]
    public async Task GetText_ReturnsContentWithMeta()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        var graph = new FakeOneDriveGraphClient { SmallContent = new OneDriveSmallContent("文件内容"u8.ToArray(), "text/plain") };
        var service = CreateService(db, graph);

        var text = await service.GetTextAsync(item.Id);

        Assert.Equal("文件内容", text.Content);
        Assert.False(text.Truncated);
        Assert.Equal("text/plain", text.MimeType);
        Assert.Equal(12L, text.Size);
        Assert.Equal(4L * 1024 * 1024, graph.DownloadSmallCalls.Single().MaxBytes);
    }

    [Fact]
    public async Task GetText_OversizedFile_Throws()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        var graph = new FakeOneDriveGraphClient
        {
            DownloadSmallException = new OneDriveContentTooLargeException(2 * 1024 * 1024 + 1),
        };
        var service = CreateService(db, graph);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetTextAsync(item.Id));
        Assert.Equal(5331, error.ErrorCode);
    }

    [Fact]
    public async Task SaveText_SnapshotsOldContent_ThenPushesNewContent()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        var graph = new FakeOneDriveGraphClient { SmallContent = new OneDriveSmallContent("旧内容"u8.ToArray(), "text/plain") };
        var service = CreateService(db, graph);

        await service.SaveTextAsync(item.Id, "新内容");

        // 快照先于回写
        Assert.True(graph.DownloadSmallCalls.Count >= 1);
        var put = Assert.Single(graph.PutCalls);
        Assert.Equal("item-1", put.ItemId);
        Assert.Equal("新内容", Encoding.UTF8.GetString(put.Bytes));
        Assert.Contains("text/plain", put.ContentType);

        var snapshot = await db.Set<FileTextSnapshotEntity>().SingleAsync();
        Assert.Equal("旧内容", snapshot.Content);
        Assert.Equal("item-1", snapshot.ExternalFileId);
        Assert.Equal("/工作/a.txt", snapshot.Path);
        Assert.Equal("pre-edit", snapshot.Reason);
        Assert.Equal(item.Id, snapshot.ItemId);
        Assert.Equal(UserId, snapshot.UserId);
    }

    [Fact]
    public async Task ListSnapshots_ReturnsNewestFirst()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        db.Set<FileTextSnapshotEntity>().AddRange(
            new FileTextSnapshotEntity
            {
                UserId = UserId, ItemId = item.Id, ExternalFileId = "item-1",
                Path = "/工作/a.txt", Name = "a.txt", Content = "v1",
                ByteSize = 2, Reason = "pre-edit", CreatedAt = Now.AddMinutes(-10),
            },
            new FileTextSnapshotEntity
            {
                UserId = UserId, ItemId = item.Id, ExternalFileId = "item-1",
                Path = "/工作/a.txt", Name = "a.txt", Content = "v2",
                ByteSize = 2, Reason = "pre-edit", CreatedAt = Now.AddMinutes(-1),
            });
        db.SaveChanges();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var snapshots = await service.ListSnapshotsAsync(item.Id);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal("v2", snapshots[0].Content);
        Assert.Equal("v1", snapshots[1].Content);
    }

    [Fact]
    public async Task RestoreSnapshot_PushesSnapshotContent_AndSnapshotsCurrentFirst()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        var snapshot = new FileTextSnapshotEntity
        {
            UserId = UserId, ItemId = item.Id, ExternalFileId = "item-1",
            Path = "/工作/a.txt", Name = "a.txt", Content = "历史版本",
            ByteSize = 12, Reason = "pre-edit", CreatedAt = Now.AddMinutes(-5),
        };
        db.Set<FileTextSnapshotEntity>().Add(snapshot);
        db.SaveChanges();
        var graph = new FakeOneDriveGraphClient { SmallContent = new OneDriveSmallContent("当前内容"u8.ToArray(), "text/plain") };
        var service = CreateService(db, graph);

        await service.RestoreSnapshotAsync(item.Id, snapshot.Id);

        // 一次下载（恢复前快照当前内容）+ 两次 PUT 无 —— 恢复是 PUT 历史内容
        var put = Assert.Single(graph.PutCalls);
        Assert.Equal("历史版本", Encoding.UTF8.GetString(put.Bytes));
        // 恢复前把当前内容也存了快照
        Assert.Equal(2, await db.Set<FileTextSnapshotEntity>().CountAsync());
    }

    [Fact]
    public async Task SaveText_PrunesSnapshots_ToRecentTenPerItem()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        for (var i = 0; i < 12; i++)
        {
            db.Set<FileTextSnapshotEntity>().Add(new FileTextSnapshotEntity
            {
                UserId = UserId, ItemId = item.Id, ExternalFileId = "item-1",
                Path = "/工作/a.txt", Name = "a.txt", Content = $"v{i}",
                ByteSize = 2, Reason = "pre-edit", CreatedAt = Now.AddMinutes(-100 + i),
            });
        }
        db.SaveChanges();
        var graph = new FakeOneDriveGraphClient { SmallContent = new OneDriveSmallContent("当前"u8.ToArray(), "text/plain") };
        var service = CreateService(db, graph);

        await service.SaveTextAsync(item.Id, "新内容");

        var remaining = await db.Set<FileTextSnapshotEntity>()
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .ToListAsync();
        Assert.Equal(10, remaining.Count);
        // 最新的是本次编辑前快照，最旧的 v0/v1 已被修剪
        Assert.Equal("当前", remaining[0].Content);
        Assert.DoesNotContain(remaining, row => row.Content == "v0");
        Assert.DoesNotContain(remaining, row => row.Content == "v1");
    }

    [Fact]
    public async Task ListSnapshots_OnSensitivePath_Blocked()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db, path: "/Secrets/密钥.txt");
        db.Set<FileTextSnapshotEntity>().Add(new FileTextSnapshotEntity
        {
            UserId = UserId, ItemId = item.Id, ExternalFileId = "item-1",
            Path = "/工作/a.txt", Name = "a.txt", Content = "旧全文",
            ByteSize = 9, Reason = "pre-edit", CreatedAt = Now,
        });
        db.SaveChanges();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        // 文件后来被移动到敏感路径：快照出口同样拦截（复审 C2）
        var error = await Assert.ThrowsAsync<DomainException>(() => service.ListSnapshotsAsync(item.Id));
        Assert.Equal(40303, error.ErrorCode);
    }

    [Fact]
    public async Task SaveText_EmptyContent_Allowed()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        var graph = new FakeOneDriveGraphClient { SmallContent = new OneDriveSmallContent("旧"u8.ToArray(), "text/plain") };
        var service = CreateService(db, graph);

        await service.SaveTextAsync(item.Id, string.Empty);

        var put = Assert.Single(graph.PutCalls);
        Assert.Empty(put.Bytes);
    }

    [Fact]
    public async Task GetText_NonTextFile_Blocked()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db, mime: "image/png", path: "/图片.png");
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetTextAsync(item.Id));
        Assert.Equal(5332, error.ErrorCode);
    }

    [Fact]
    public async Task Operations_OnOtherUsersItem_Throw()
    {
        await using var db = CreateDb();
        var (_, item) = SeedFile(db);
        var service = CreateService(db, new FakeOneDriveGraphClient());
        // 以另一个用户身份访问
        var other = new OneDriveContentService(
            db,
            new FakeOneDriveGraphClient(),
            new OneDriveTokenService(db, new FakeOneDriveGraphClient(), new TestSecretProtector(), clock: new FixedClock(Now)),
            new StubCurrentUser(Guid.Parse("eeeeeeee-1111-2222-3333-444444444499")),
            new SensitivePathPolicy(null),
            NullLogger<OneDriveContentService>.Instance,
            new FixedClock(Now));

        await Assert.ThrowsAsync<DomainException>(() => other.GetContentLinkAsync(item.Id));
        await Assert.ThrowsAsync<DomainException>(() => other.SaveTextAsync(item.Id, "hack"));
    }
}
