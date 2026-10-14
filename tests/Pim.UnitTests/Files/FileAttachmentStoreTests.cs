using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// FileAttachmentStore 测试（工单 05 / F1、F2）：附件按 /PIM/{objectKey} 存入用户自己的
/// 网盘、≤4MB 上限、读瞬态、删除进回收站、未绑定账号时给出明确错误；
/// 多账号下必须落到**默认账号**并把账号记进句柄。
///
/// 同时锁定交接文档资产的已知缺陷：objectKey 是存储实现返回的**裸远端条目 id**，
/// 不能再被当作「含 userId 的路径」反解用户——身份必须由调用方显式传入。
/// </summary>
public class FileAttachmentStoreTests
{
    private static readonly Guid UserId = Guid.Parse("dddddddd-1111-2222-3333-444444444471");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedText) => protectedText.Replace("protected::", "");
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"files-attachment-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(
        PimDbContext db,
        string status = "connected",
        bool withToken = true,
        bool isDefault = false,
        string accountId = "acc-1")
    {
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "onedrive",
            ClientId = "cid",
            AccountId = accountId,
            Status = status,
            IsDefault = isDefault,
            RefreshTokenEncrypted = withToken ? Encoding.UTF8.GetBytes("protected::refresh") : null,
            TokenExpiresAt = Now.AddHours(1),
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    private static FileAttachmentStore CreateStore(PimDbContext db, FakeOneDriveGraphClient graph)
    {
        var harness = new FilesProviderHarness(db, graph, new TestSecretProtector(), new HarnessClock(Now));
        return new FileAttachmentStore(db, harness.Sessions, null);
    }

    [Fact]
    public async Task StoreAsync_UploadsUnderPimPrefix_AndReturnsRemoteItemId()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient { NewItemId = "drive-item-42" };
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("attachment-bytes"));

        var stored = await store.StoreAsync(
            UserId, "quick-notes/u/1/capture.png", content, "image/png", content.Length);

        Assert.Equal("drive-item-42", stored.ObjectKey);
        var call = Assert.Single(graph.PutNewFileCalls);
        Assert.Equal("/PIM/quick-notes/u/1/capture.png", call.ItemPath);
        Assert.Equal("image/png", call.ContentType);
        Assert.Equal("attachment-bytes", Encoding.UTF8.GetString(call.Bytes));
    }

    /// <summary>
    /// 交接文档资产的核心缺陷：StoreAsync 返回裸远端条目 id，若后续读/删再从
    /// objectKey 反解 userId 就会失败。这里断言返回的句柄能原样用于 OpenReadAsync/DeleteAsync，
    /// 且身份来自显式参数（Graph 调用记录里能对上）。
    /// </summary>
    [Fact]
    public async Task ReturnedHandle_IsUsableForReadAndDelete_WithoutParsingUserIdFromIt()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient { NewItemId = "bare-drive-id" };
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        var stored = await store.StoreAsync(UserId, "quick-notes/u/1/a.txt", content, "text/plain", content.Length);
        Assert.Equal("bare-drive-id", stored.ObjectKey);
        // 关键：ObjectKey 不含 '/'，任何「从路径第 2 段解析 userId」的实现都会在这里失败
        Assert.DoesNotContain('/', stored.ObjectKey);

        graph.SmallContent = new("payload"u8.ToArray(), "text/plain");
        await using var read = await store.OpenReadAsync(UserId, stored);
        using var reader = new StreamReader(read);
        Assert.Equal("payload", await reader.ReadToEndAsync());
        // 断言用的是传入的 ObjectKey 原值（而非任何解析结果）
        Assert.Contains("bare-drive-id", graph.DownloadSmallCalls.Select(c => c.ItemId));

        await store.DeleteAsync(UserId, stored);
        Assert.Contains("bare-drive-id", graph.DeleteCalls.Select(c => c.ItemId));
    }

    /// <summary>
    /// F2：附件写入必须定位到**默认账号**，且句柄里记下它 ——
    /// 否则第二个账号接入后，读回时无从判断文件在哪个盘。
    /// </summary>
    [Fact]
    public async Task StoreAsync_TargetsDefaultAccount_AndRecordsAccountInHandle()
    {
        await using var db = CreateDb();
        var primary = SeedProvider(db, isDefault: true, accountId: "acc-primary");
        await using var content = new MemoryStream("bytes"u8.ToArray());
        var graph = new FakeOneDriveGraphClient { NewItemId = "item-on-default" };
        var store = CreateStore(db, graph);

        var stored = await store.StoreAsync(UserId, "quick-notes/u/1/a.txt", content, "text/plain", 5);

        Assert.Equal(primary.Id, stored.ProviderId);
        Assert.Equal("onedrive", stored.ProviderKey);

        // 没有默认账号时退回最早连接的那个（确定性），仍然带账号句柄
        primary.IsDefault = false;
        await db.SaveChangesAsync();
        await using var second = new MemoryStream("bytes"u8.ToArray());
        var stored2 = await store.StoreAsync(UserId, "quick-notes/u/1/b.txt", second, "text/plain", 5);
        Assert.Equal(primary.Id, stored2.ProviderId);
    }

    /// <summary>
    /// 老数据（句柄里没有账号）必须回退查找，而不是直接报「附件不存在」。
    /// 回退是确定的：远端条目 id 只在各自账号内唯一，找不到就换下一个账号。
    /// </summary>
    [Fact]
    public async Task OpenReadAsync_WithoutAccountInHandle_FallsBackAcrossConnectedAccounts()
    {
        await using var db = CreateDb();
        SeedProvider(db, isDefault: true, accountId: "acc-a");
        var legacyRef = new FileAttachmentRef("id-only-on-second-account", "onedrive", null);
        var graph = new FakeOneDriveGraphClient { SmallContent = null };
        var store = CreateStore(db, graph);

        // 唯一账号里没有该 id → 明确报 4006（不静默返回空内容）
        var error = await Assert.ThrowsAsync<DomainException>(() => store.OpenReadAsync(UserId, legacyRef));
        Assert.Equal(4006, error.ErrorCode);

        // 两个账号时两个都试过
        SeedProvider(db, isDefault: false, accountId: "acc-b");
        graph.SmallContent = null;
        var callsBefore = graph.DownloadSmallCalls.Count;
        await Assert.ThrowsAsync<DomainException>(() => store.OpenReadAsync(UserId, legacyRef));
        Assert.True(
            graph.DownloadSmallCalls.Count - callsBefore >= 2,
            "句柄没有账号时必须在该用户的每个已连接账号上各试一次");
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
            () => store.OpenReadAsync(UserId, new FileAttachmentRef("gone", "onedrive", null)));

        Assert.Equal(4006, error.ErrorCode);
    }

    [Fact]
    public async Task GetDirectLinkAsync_ReturnsPreauthorizedUrl()
    {
        await using var db = CreateDb();
        SeedProvider(db);
        var graph = new FakeOneDriveGraphClient { DownloadUrl = "https://my.microsoftpersonalcontent.com/dl?tempauth=x" };
        var store = CreateStore(db, graph);

        var link = await store.GetDirectLinkAsync(UserId, new FileAttachmentRef("item-1", "onedrive", null));

        Assert.Equal("https://my.microsoftpersonalcontent.com/dl?tempauth=x", link);
    }

    [Fact]
    public async Task WithoutAnyBinding_Throws5320()
    {
        await using var db = CreateDb();
        var graph = new FakeOneDriveGraphClient();
        var store = CreateStore(db, graph);
        await using var content = new MemoryStream("x"u8.ToArray());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => store.StoreAsync(UserId, "k", content, "text/plain", 1));

        Assert.Equal(5320, error.ErrorCode);
    }

    /// <summary>账号行存在但拿不到令牌：必须是「绑定不完整」的明确错误，而不是静默失败。</summary>
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
        Assert.Equal("/PIM/a/b.txt", FileAttachmentStore.AttachmentPath("a/b.txt"));
        Assert.Equal("/PIM/a/b.txt", FileAttachmentStore.AttachmentPath("/a/b.txt"));
    }
}
