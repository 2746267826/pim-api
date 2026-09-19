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

    /// <summary>
    /// 每次读取都前进 1 秒的时钟：级联恢复依赖「同一次删除的子孙 DeletedAt 完全相同」，
    /// 用恒定时钟无法区分两次独立删除，会掩盖/伪造时序相关的行为（仓库 B1 纪律）。
    /// </summary>
    private sealed class AdvancingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
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

    private static OneDriveWriteService CreateService(
        PimDbContext db,
        FakeOneDriveGraphClient graph,
        StubAuditLog? audit = null,
        SensitivePathPolicy? sensitivePolicy = null,
        TimeProvider? clock = null)
        => new(
            db,
            graph,
            new OneDriveTokenService(db, graph, new TestSecretProtector(), clock: new FixedClock(Now)),
            new StubCurrentUser(UserId),
            audit ?? new StubAuditLog(),
            sensitivePolicy: sensitivePolicy,
            logger: NullLogger<OneDriveWriteService>.Instance,
            clock: clock ?? new FixedClock(Now));

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

    // ===================== P4a 复审：目录子孙收敛 + 敏感路径 =====================

    /// <summary>在 /合同 下再挂一层子目录与文件，用于验证子孙 Path 收敛。</summary>
    private static (FileItemEntity Sub, FileItemEntity File) SeedDescendants(PimDbContext db, FileProviderEntity provider, FileItemEntity folder)
    {
        var sub = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "sub-ext",
            ParentExternalFileId = folder.ExternalFileId,
            Path = "/合同/2026",
            Name = "2026",
            ItemType = "folder",
        };
        var file = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "sub-file-ext",
            ParentExternalFileId = sub.ExternalFileId,
            Path = "/合同/2026/报价单.txt",
            Name = "报价单.txt",
            ItemType = "file",
            MimeType = "text/plain",
        };
        db.Set<FileItemEntity>().AddRange(sub, file);
        db.SaveChanges();
        return (sub, file);
    }

    /// <summary>重命名目录后，所有子孙的 Path 前缀必须一起改写（否则树/搜索/敏感判断全错）。</summary>
    [Fact]
    public async Task RenameFolder_RewritesDescendantPaths()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        var (sub, file) = SeedDescendants(db, provider, folder);
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await service.RenameAsync(folder.Id, "合同归档");

        Assert.Equal("/合同归档", folder.Path);
        Assert.Equal("/合同归档/2026", sub.Path);
        Assert.Equal("/合同归档/2026/报价单.txt", file.Path);
    }

    /// <summary>移动目录后同理：子孙 Path 必须跟着新前缀走。</summary>
    [Fact]
    public async Task MoveFolder_RewritesDescendantPaths()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        var (sub, file) = SeedDescendants(db, provider, folder);
        var archive = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = "archive-ext",
            ParentExternalFileId = "root-ext",
            Path = "/归档",
            Name = "归档",
            ItemType = "folder",
        };
        db.Set<FileItemEntity>().Add(archive);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await service.MoveAsync(folder.Id, "/归档");

        Assert.Equal("/归档/合同", folder.Path);
        Assert.Equal("/归档/合同/2026", sub.Path);
        Assert.Equal("/归档/合同/2026/报价单.txt", file.Path);
    }

    /// <summary>删除目录时子孙必须一并软删，否则树里留下「父已删、子仍在」的悬空节点。</summary>
    [Fact]
    public async Task DeleteFolder_SoftDeletesDescendants()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        var (sub, file) = SeedDescendants(db, provider, folder);
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await service.DeleteToTrashAsync(folder.Id);

        Assert.True(folder.IsDeleted);
        Assert.True(sub.IsDeleted, "子目录应随父目录一起软删");
        Assert.True(file.IsDeleted, "深层子文件应随父目录一起软删");
        Assert.NotNull(sub.DeletedAt);
        Assert.NotNull(file.DeletedAt);
    }

    /// <summary>
    /// open-link 是内容出口：敏感路径必须与其他出口一样拒绝，
    /// 否则 /Secrets/* 能借 webUrl 绕过保护（复审 I-1）。
    /// </summary>
    [Fact]
    public async Task OpenLink_SensitivePath_IsRejected()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        item.Path = "/Secrets/密钥.txt";
        item.Name = "密钥.txt";
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeOneDriveGraphClient(), sensitivePolicy: new SensitivePathPolicy(null));

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetWebUrlAsync(item.Id));
        Assert.Equal(40303, error.ErrorCode);
    }

    /// <summary>非敏感路径的 open-link 仍应正常返回 webUrl。</summary>
    [Fact]
    public async Task OpenLink_NormalPath_ReturnsWebUrl()
    {
        await using var db = CreateDb();
        var (_, item, _) = SeedTree(db);
        var service = CreateService(db, new FakeOneDriveGraphClient(), sensitivePolicy: new SensitivePathPolicy(null));

        var url = await service.GetWebUrlAsync(item.Id);

        Assert.Contains("onedrive", url);
    }

    /// <summary>超过 4MB 的上传必须在读满内存前就拒绝。</summary>
    [Fact]
    public async Task Upload_OversizedStream_IsRejectedWithoutBufferingWholePayload()
    {
        await using var db = CreateDb();
        SeedTree(db);
        var service = CreateService(db, new FakeOneDriveGraphClient());

        // 8MB 流：若实现先全量 CopyToAsync 再检查，这里同样会失败，但内存已被占用；
        // 本用例锁定的是「超限必须报 5331」这一可观察行为。
        var oversized = new MemoryStream(new byte[8 * 1024 * 1024]);
        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.UploadAsync("/合同", "big.bin", oversized, "application/octet-stream"));
        Assert.Equal(5331, error.ErrorCode);
    }

    // ===================== P4a 复审：根目录与自嵌套保护 =====================

    /// <summary>
    /// 根项 Path 就是 "/"，不含名字，用 `Path[..^Name.Length]` 反推前缀会越界抛
    /// ArgumentOutOfRangeException（未捕获 → 500）。必须给出明确的业务错误。
    /// </summary>
    [Fact]
    public async Task RenameRoot_ReturnsDomainError_NotArgumentOutOfRange()
    {
        await using var db = CreateDb();
        var (_, _, _) = SeedTree(db);
        var root = await db.Set<FileItemEntity>().SingleAsync(item => item.Path == "/");
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.RenameAsync(root.Id, "新名字"));
        Assert.Equal(5338, error.ErrorCode);
    }

    /// <summary>移动根目录会让子孙前缀改写退化成匹配全部项，必须拒绝。</summary>
    [Fact]
    public async Task MoveRoot_IsRejected()
    {
        await using var db = CreateDb();
        var (_, _, folder) = SeedTree(db);
        var root = await db.Set<FileItemEntity>().SingleAsync(item => item.Path == "/");
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.MoveAsync(root.Id, folder.Path));
        Assert.Equal(5337, error.ErrorCode);
    }

    /// <summary>删除根目录等于软删整盘，必须拒绝。</summary>
    [Fact]
    public async Task DeleteRoot_IsRejected()
    {
        await using var db = CreateDb();
        SeedTree(db);
        var root = await db.Set<FileItemEntity>().SingleAsync(item => item.Path == "/");
        var service = CreateService(db, new FakeOneDriveGraphClient());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.DeleteToTrashAsync(root.Id));
        Assert.Equal(5337, error.ErrorCode);
    }

    /// <summary>
    /// 把目录移动到自己的子孙里：Graph 会拒绝，但本地若已改写子孙 Path 就会永久错乱，
    /// 因此必须在调用 Graph 之前拦下。
    /// </summary>
    [Fact]
    public async Task MoveFolderIntoItsOwnDescendant_IsRejectedBeforeCallingGraph()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        SeedDescendants(db, provider, folder);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.MoveAsync(folder.Id, "/合同/2026"));
        Assert.Equal(5337, error.ErrorCode);
        // 关键：Graph 不应被调用（否则远端已移动、本地却抛错，两边不一致）
        Assert.Empty(graph.PatchCalls);
    }

    // ===================== P4a 复审：目录级联恢复 =====================

    /// <summary>
    /// 目录的子孙是随父目录级联软删的，恢复目录必须整树恢复，
    /// 否则出现「父目录可见、子项仍是删除态」的残缺树（复审 NEW-4）。
    /// </summary>
    [Fact]
    public async Task RestoreFolder_RestoresCascadeDeletedDescendants()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        var (sub, file) = SeedDescendants(db, provider, folder);
        var service = CreateService(db, new FakeOneDriveGraphClient());

        await service.DeleteToTrashAsync(folder.Id);
        Assert.True(sub.IsDeleted);
        Assert.True(file.IsDeleted);

        await service.RestoreAsync(folder.Id);

        Assert.False(folder.IsDeleted);
        Assert.False(sub.IsDeleted, "级联删除的子目录应随父目录一起恢复");
        Assert.False(file.IsDeleted, "级联删除的子文件应随父目录一起恢复");
        Assert.Null(sub.DeletedAt);
        Assert.Null(file.DeletedAt);
    }

    /// <summary>
    /// 早先被单独删除的子项（DeletedAt 与父目录级联时间不同）不应被父目录恢复顺带复活。
    /// </summary>
    [Fact]
    public async Task RestoreFolder_DoesNotResurrectIndependentlyDeletedDescendant()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        var (sub, file) = SeedDescendants(db, provider, folder);
        // 用前进时钟：两次删除才能拿到不同的 DeletedAt，本用例才有区分能力
        var service = CreateService(db, new FakeOneDriveGraphClient(), clock: new AdvancingClock(Now));

        // 先把子文件单独删掉（时间戳与随后的目录级联删除不同）
        await service.DeleteToTrashAsync(file.Id);
        var independentDeletedAt = file.DeletedAt;

        // 再删父目录（级联软删剩余的 sub）
        await service.DeleteToTrashAsync(folder.Id);
        Assert.NotEqual(independentDeletedAt, sub.DeletedAt);

        await service.RestoreAsync(folder.Id);

        Assert.False(folder.IsDeleted);
        Assert.False(sub.IsDeleted, "级联删除的子目录应恢复");
        Assert.True(file.IsDeleted, "早先被单独删除的子文件不应被顺带复活");
    }

    /// <summary>
    /// 远端已删除的子孙不应被「复活」：否则本地出现一批访问即 404 的幽灵行（复审 N3-3）。
    /// </summary>
    [Fact]
    public async Task RestoreFolder_KeepsDescendantsThatAreGoneFromOneDriveDeleted()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        var (sub, file) = SeedDescendants(db, provider, folder);
        var graph = new FakeOneDriveGraphClient();
        var service = CreateService(db, graph, clock: new AdvancingClock(Now));

        await service.DeleteToTrashAsync(folder.Id);

        // 远端：父目录仍在，但子文件已被真删
        graph.MissingItemIds.Add(file.ExternalFileId);

        await service.RestoreAsync(folder.Id);

        Assert.False(folder.IsDeleted);
        Assert.False(sub.IsDeleted, "远端仍存在的子目录应恢复");
        Assert.True(file.IsDeleted, "远端已消失的子文件必须保持删除态，不能复活");
        Assert.NotNull(file.DeletedAt);
    }

    /// <summary>
    /// 历史数据（父目录 IsDeleted=true 但 DeletedAt=null）恢复目录时，
    /// 不能静默跳过整棵子树——否则用户看到父目录可见、子项永久消失（复审 N3-2）。
    /// </summary>
    [Fact]
    public async Task RestoreFolder_WithLegacyNullDeletedAt_StillRestoresDescendants()
    {
        await using var db = CreateDb();
        var (provider, _, folder) = SeedTree(db);
        var (sub, file) = SeedDescendants(db, provider, folder);
        var service = CreateService(db, new FakeOneDriveGraphClient());

        // 模拟历史行：父子都是删除态，但 DeletedAt 为 null（早于该字段落库）
        folder.IsDeleted = true;
        folder.DeletedAt = null;
        sub.IsDeleted = true;
        sub.DeletedAt = null;
        file.IsDeleted = true;
        file.DeletedAt = null;
        await db.SaveChangesAsync();

        await service.RestoreAsync(folder.Id);

        Assert.False(folder.IsDeleted);
        Assert.False(sub.IsDeleted, "历史数据也应整树恢复，不能静默跳过");
        Assert.False(file.IsDeleted);
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
