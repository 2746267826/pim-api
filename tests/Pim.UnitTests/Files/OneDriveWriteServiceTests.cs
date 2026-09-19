using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveWriteService 测试：移动/重命名走 Graph 并收敛本地元数据、删除软删、
/// 恢复远端校验、上传收敛新行、审计落库、非 OneDrive 拒绝。
/// </summary>
public class OneDriveWriteServiceTests
{
    private static readonly Guid UserId = Guid.Parse("eeeeeeee-1111-2222-3333-444444444491");
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
            .UseInMemoryDatabase($"onedrive-write-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static (FileProviderEntity Provider, FileItemEntity Item, FileItemEntity Folder) SeedTree(PimDbContext db)
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
        var root = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "root-ext",
            Path = "/",
            Name = "root",
            ItemType = "folder",
        };
        var folder = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "folder-ext",
            ParentExternalFileId = "root-ext",
            Path = "/合同",
            Name = "合同",
            ItemType = "folder",
        };
        var item = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "file-ext",
            ParentExternalFileId = "root-ext",
            Path = "/a.txt",
            Name = "a.txt",
            ItemType = "file",
            MimeType = "text/plain",
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().AddRange(root, folder, item);
        db.SaveChanges();
        return (provider, item, folder);
    }

    private static OneDriveWriteService CreateService(PimDbContext db, FakeOneDriveGraphClient graph, StubAuditLog? audit = null)
        => new(
            db,
            graph,
            new OneDriveTokenService(db, graph, new TestSecretProtector(), clock: new FixedClock(Now)),
            new StubCurrentUser(UserId),
            audit ?? new StubAuditLog(),
            NullLogger<OneDriveWriteService>.Instance,
            new FixedClock(Now));

    [Fact]
    public async Task Move_PatchesGraphParent_AndConvergesLocalPath()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var result = await service.MoveAsync(item.Id, "/合同");

        Assert.Equal("/合同/a.txt", result.Path);
        var patch = Assert.Single(graph.PatchCalls);
        Assert.Equal("file-ext", patch.ItemId);
        Assert.Null(patch.NewName);
        Assert.Equal("folder-ext", patch.NewParentId);
    }

    [Fact]
    public async Task Move_IntoMissingFolder_Throws()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.MoveAsync(item.Id, "/不存在"));
        Assert.Equal(5304, error.ErrorCode);
    }

    [Fact]
    public async Task Rename_PatchesGraphName_AndConvergesLocalPath()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var result = await service.RenameAsync(item.Id, "b.txt");

        Assert.Equal("/b.txt", result.Path);
        var patch = Assert.Single(graph.PatchCalls);
        Assert.Equal("b.txt", patch.NewName);
        Assert.Null(patch.NewParentId);
    }

    [Fact]
    public async Task Delete_GraphDeleteThenSoftDelete_AndAudits()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        var graph = new FakeOneDriveGraphClient();
        var audit = new StubAuditLog();
        var service = CreateService(db, graph, audit);

        await service.DeleteToTrashAsync(item.Id);

        Assert.Single(graph.DeleteCalls);
        var deleted = await db.Set<FileItemEntity>().SingleAsync(i => i.Id == item.Id);
        Assert.True(deleted.IsDeleted);
        Assert.Contains("files.onedrive.delete_to_trash", audit.Actions);
    }

    [Fact]
    public async Task Restore_ClearsDeleted_WhenRemoteStillExists()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        await service.DeleteToTrashAsync(item.Id);

        var result = await service.RestoreAsync(item.Id);

        Assert.Equal("/a.txt", result.Path);
        var restored = await db.Set<FileItemEntity>().SingleAsync(i => i.Id == item.Id);
        Assert.False(restored.IsDeleted);
    }

    [Fact]
    public async Task Restore_WhenRemoteGone_Throws404MappedCode()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);
        await service.DeleteToTrashAsync(item.Id);

        graph.DownloadUrlException = new OneDriveGraphException(404, null, "itemNotFound");

        var error = await Assert.ThrowsAsync<DomainException>(() => service.RestoreAsync(item.Id));
        Assert.Equal(5340, error.ErrorCode);
    }

    [Fact]
    public async Task Upload_PutsByPath_ConvergesNewItem_AndAudits()
    {
        await using var db = CreateDb();
        SeedTree(db);
        var graph = new FakeOneDriveGraphClient { NewItemId = "uploaded-ext" };
        var audit = new StubAuditLog();
        var service = CreateService(db, graph, audit);

        var result = await service.UploadAsync("/合同", "新文件.md", new MemoryStream("内容"u8.ToArray()), "text/markdown");

        Assert.Equal("/合同/新文件.md", result.Path);
        var put = Assert.Single(graph.PutNewFileCalls);
        Assert.Contains("合同/新文件.md", put.ItemPath);
        Assert.Equal("text/markdown", put.ContentType);
        var item = await db.Set<FileItemEntity>().SingleAsync(i => i.ExternalFileId == "uploaded-ext");
        Assert.Equal("/合同/新文件.md", item.Path);
        Assert.Contains("files.onedrive.upload", audit.Actions);
    }

    [Fact]
    public async Task Upload_Oversized_Throws()
    {
        await using var db = CreateDb();
        SeedTree(db);
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.UploadAsync("/", "big.bin", new MemoryStream(new byte[5 * 1024 * 1024]), "application/octet-stream"));
        Assert.Equal(5331, error.ErrorCode);
    }

    [Fact]
    public async Task Operations_NonOneDriveProvider_Throws()
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
        var item = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "nc-1",
            Path = "/a.txt",
            Name = "a.txt",
            ItemType = "file",
        };
        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().Add(item);
        db.SaveChanges();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await Assert.ThrowsAsync<DomainException>(() => service.MoveAsync(item.Id, "/x"));
        await Assert.ThrowsAsync<DomainException>(() => service.RenameAsync(item.Id, "b.txt"));
        await Assert.ThrowsAsync<DomainException>(() => service.DeleteToTrashAsync(item.Id));
    }
}

internal sealed class StubAuditLog : IAuditLogService
{
    public List<string> Actions { get; } = [];

    public Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default)
    {
        Actions.Add(request.Action);
        return Task.FromResult(new AuditLogDto(
            Guid.NewGuid(),
            request.UserId,
            request.ActorType,
            request.Action,
            request.ResourceType,
            request.ResourceId,
            request.Source,
            request.Result,
            null,
            DateTimeOffset.UtcNow));
    }
}
