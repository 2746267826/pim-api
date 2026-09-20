using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveAttachmentStore 测试（设计文档 §10）：附件按 /PIM/{objectKey} 存入用户自己的
/// OneDrive、≤4MB 上限、读瞬态、删除进回收站、未绑定 OneDrive 时给出明确错误。
///
/// 同时锁定交接文档资产的已知缺陷：objectKey 是存储实现返回的**裸 driveItem id**，
/// 不能再被当作「含 userId 的路径」反解用户——身份必须由调用方显式传入。
/// </summary>
public class OneDriveAttachmentStoreTests
{
    private static readonly Guid UserId = Guid.Parse("dddddddd-1111-2222-3333-444444444471");

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"onedrive-attachment-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(PimDbContext db, string status = "connected", bool withToken = true)
    {
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "onedrive",
            ClientId = "cid",
            Status = status,
            RefreshTokenEncrypted = withToken ? Encoding.UTF8.GetBytes("protected::refresh") : null,
            TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    private static OneDriveAttachmentStore CreateStore(PimDbContext db, FakeOneDriveGraphClient graph)
        => new(db, graph, new OneDriveTokenService(db, graph, new TestSecretProtector()));

    [Fact]
    public async Task StoreAsync_UploadsUnderPimPrefix_AndReturnsDriveItemId()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient { NewItemId = "drive-item-42" };
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("attachment-bytes"));

        var objectKey = await store.StoreAsync(
            UserId, "quick-notes/u/1/capture.png", content, "image/png", content.Length);

        Assert.Equal("drive-item-42", objectKey);
        var call = Assert.Single(graph.PutNewFileCalls);
        Assert.Equal("/PIM/quick-notes/u/1/capture.png", call.ItemPath);
        Assert.Equal("image/png", call.ContentType);
        Assert.Equal("attachment-bytes", Encoding.UTF8.GetString(call.Bytes));
    }

    /// <summary>
    /// 交接文档资产的核心缺陷：StoreAsync 返回裸 driveItem id，若后续读/删再从
    /// objectKey 反解 userId 就会失败。这里断言返回的 key 能原样用于 OpenReadAsync/DeleteAsync，
    /// 且身份来自显式参数（Graph 调用记录里能对上）。
    /// </summary>
    [Fact]
    public async Task ReturnedObjectKey_IsUsableForReadAndDelete_WithoutParsingUserIdFromIt()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient { NewItemId = "bare-drive-id" };
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        var objectKey = await store.StoreAsync(UserId, "quick-notes/u/1/a.txt", content, "text/plain", content.Length);
        Assert.Equal("bare-drive-id", objectKey);
        // 关键：objectKey 不含 '/'，任何「从路径第 2 段解析 userId」的实现都会在这里失败
        Assert.DoesNotContain('/', objectKey);

        graph.SmallContent = new("payload"u8.ToArray(), "text/plain");
        await using var read = await store.OpenReadAsync(UserId, objectKey);
        using var reader = new StreamReader(read);
        Assert.Equal("payload", await reader.ReadToEndAsync());
        // 断言用的是传入的 objectKey 原值（而非任何解析结果）
        Assert.Contains("bare-drive-id", graph.DownloadSmallCalls.Select(c => c.ItemId));

        await store.DeleteAsync(UserId, objectKey);
        Assert.Contains("bare-drive-id", graph.DeleteCalls.Select(c => c.ItemId));
    }

    [Fact]
    public async Task StoreAsync_OverFourMegabytes_IsRejectedWith5331()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient();
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream(new byte[5 * 1024 * 1024]);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => store.StoreAsync(UserId, "quick-notes/u/1/big.bin", content, "application/octet-stream", content.Length));

        Assert.Equal(5331, error.ErrorCode);
        Assert.Empty(graph.PutNewFileCalls);
    }

    [Fact]
    public async Task ReadAsync_WhenRemoteMissing_Throws4006()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient { SmallContent = null };
        var store = CreateStore(db, graph);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => store.OpenReadAsync(UserId, "gone"));

        Assert.Equal(4006, error.ErrorCode);
    }

    [Fact]
    public async Task GetDirectLinkAsync_ReturnsGraphPreauthorizedUrl()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient { DownloadUrl = "https://my.microsoftpersonalcontent.com/dl?tempauth=x" };
        var store = CreateStore(db, graph);

        var link = await store.GetDirectLinkAsync(UserId, "item-1");

        Assert.Equal("https://my.microsoftpersonalcontent.com/dl?tempauth=x", link);
    }

    [Fact]
    public async Task WithoutOneDriveBinding_Throws5320()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream("x"u8.ToArray());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => store.StoreAsync(UserId, "k", content, "text/plain", 1));

        Assert.Equal(5320, error.ErrorCode);
    }

    [Fact]
    public async Task ProviderWithoutRefreshToken_Throws5321()
    {
        await using var db = CreateDb();
        SeedProvider(db, withToken: false);
        var graph = new FakeOneDriveGraphClient();
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream("x"u8.ToArray());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => store.StoreAsync(UserId, "k", content, "text/plain", 1));

        Assert.Equal(5321, error.ErrorCode);
    }

    /// <summary>附件路径永远落在 /PIM 下，不会与用户文件树混淆。</summary>
    [Fact]
    public void AttachmentPath_IsAlwaysUnderPimRoot()
    {
        Assert.Equal("/PIM/a/b.txt", OneDriveAttachmentStore.OneDriveAttachmentPath("a/b.txt"));
        Assert.Equal("/PIM/a/b.txt", OneDriveAttachmentStore.OneDriveAttachmentPath("/a/b.txt"));
    }
}
