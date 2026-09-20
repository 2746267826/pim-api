using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.QuickNotes.DTOs;
using Pim.Module.QuickNotes.Entities;
using Pim.Module.QuickNotes.Services;
using Xunit;

namespace Pim.UnitTests.QuickNotes;

public class QuickNoteAttachmentServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task UploadAsync_CreatesTemporaryAttachmentAndStoresObject()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var service = CreateAttachmentService(db, UserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));

        var uploaded = await service.UploadAsync(content, "/tmp/capture.png", "image/png", content.Length);

        Assert.Equal("capture.png", uploaded.FileName);
        Assert.Equal("image/png", uploaded.ContentType);
        Assert.Equal(content.Length, uploaded.SizeBytes);
        Assert.Equal($"/api/v1/quick-notes/attachments/{uploaded.Id}/download", uploaded.DownloadUrl);
        Assert.Equal(uploaded.DownloadUrl, uploaded.PreviewUrl);

        var attachment = await db.Set<QuickNoteAttachmentEntity>().SingleAsync();
        Assert.Equal(uploaded.Id, attachment.Id);
        Assert.Equal(UserId, attachment.UserId);
        Assert.Null(attachment.QuickNoteId);
        // MinIO 随 P4 退役；测试替身不是 OneDrive 适配器，故记录实现类型名
        Assert.Equal(nameof(FakeObjectStorage), attachment.StorageProvider);
        Assert.StartsWith($"quick-notes/{UserId:N}/{uploaded.Id:N}/", attachment.ObjectKey);
        Assert.True(storage.StoredObjects.ContainsKey(attachment.ObjectKey));

        // 用户身份必须显式传给存储层（不再从 objectKey 反解）
        Assert.Equal(UserId, storage.LastStoreUserId);
    }

    [Fact]
    public async Task CreateAsync_BindsExplicitAndMarkdownAttachmentIds()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var attachments = CreateAttachmentService(db, UserId, storage);
        var notes = CreateNoteService(db, UserId, attachments);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "inline.png", "image/png", content.Length);
        var markdown = $"![inline]({uploaded.DownloadUrl})";

        var created = await notes.CreateAsync(new CreateQuickNoteRequest(markdown, "web-page", [uploaded.Id]));

        var attachment = Assert.Single(created.Attachments);
        Assert.Equal(uploaded.Id, attachment.Id);
        Assert.Equal(created.Id, await db.Set<QuickNoteAttachmentEntity>()
            .Where(a => a.Id == uploaded.Id)
            .Select(a => a.QuickNoteId)
            .SingleAsync());
    }

    [Fact]
    public async Task CreateAsync_BindsExplicitTemporaryAttachmentIdWithoutMarkdownReference()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var attachments = CreateAttachmentService(db, UserId, storage);
        var notes = CreateNoteService(db, UserId, attachments);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "upload.png", "image/png", content.Length);

        var created = await notes.CreateAsync(new CreateQuickNoteRequest("draft with upload", "web-page", [uploaded.Id]));

        var attachment = Assert.Single(created.Attachments);
        Assert.Equal(uploaded.Id, attachment.Id);
        Assert.Equal(created.Id, await db.Set<QuickNoteAttachmentEntity>()
            .Where(a => a.Id == uploaded.Id)
            .Select(a => a.QuickNoteId)
            .SingleAsync());
    }

    [Fact]
    public async Task UpdateAsync_WithNullAttachmentIdsRemovesAttachmentsNoLongerReferencedByMarkdown()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var attachments = CreateAttachmentService(db, UserId, storage);
        var notes = CreateNoteService(db, UserId, attachments);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "inline.png", "image/png", content.Length);
        var created = await notes.CreateAsync(new CreateQuickNoteRequest(
            $"before ![inline]({uploaded.DownloadUrl})",
            "web-page",
            [uploaded.Id]));

        var updated = await notes.UpdateAsync(created.Id, new UpdateQuickNoteRequest("after without attachment", null, null));

        Assert.Empty(updated.Attachments);
        var stored = await db.Set<QuickNoteAttachmentEntity>()
            .IgnoreQueryFilters()
            .SingleAsync(a => a.Id == uploaded.Id);
        Assert.Equal(created.Id, stored.QuickNoteId);
        Assert.NotNull(stored.DeletedAt);
    }

    [Fact]
    public async Task CreateAsync_RejectsAttachmentOwnedByAnotherUser()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var otherAttachments = CreateAttachmentService(db, OtherUserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await otherAttachments.UploadAsync(content, "private.png", "image/png", content.Length);
        var attachments = CreateAttachmentService(db, UserId, storage);
        var notes = CreateNoteService(db, UserId, attachments);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => notes.CreateAsync(new CreateQuickNoteRequest("private", "web-page", [uploaded.Id])));

        Assert.Equal(4005, error.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_RejectsDeletedAttachment()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var attachments = CreateAttachmentService(db, UserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "deleted.png", "image/png", content.Length);
        await attachments.DeleteAsync(uploaded.Id);
        var notes = CreateNoteService(db, UserId, attachments);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => notes.CreateAsync(new CreateQuickNoteRequest("deleted", "web-page", [uploaded.Id])));

        Assert.Equal(4005, error.ErrorCode);
    }

    [Fact]
    public async Task UpdateAsync_RejectsRebindingDeletedAttachmentFromMarkdownOrExplicitId()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var attachments = CreateAttachmentService(db, UserId, storage);
        var notes = CreateNoteService(db, UserId, attachments);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "removed.png", "image/png", content.Length);
        var created = await notes.CreateAsync(new CreateQuickNoteRequest("with attachment", "web-page", [uploaded.Id]));

        await notes.UpdateAsync(created.Id, new UpdateQuickNoteRequest("without attachment", null, []));

        var deletedAt = await db.Set<QuickNoteAttachmentEntity>()
            .IgnoreQueryFilters()
            .Where(a => a.Id == uploaded.Id)
            .Select(a => a.DeletedAt)
            .SingleAsync();
        Assert.NotNull(deletedAt);

        var markdownError = await Assert.ThrowsAsync<DomainException>(
            () => notes.UpdateAsync(
                created.Id,
                new UpdateQuickNoteRequest($"again ![removed]({uploaded.DownloadUrl})", null, null)));
        var explicitError = await Assert.ThrowsAsync<DomainException>(
            () => notes.UpdateAsync(
                created.Id,
                new UpdateQuickNoteRequest("again", null, [uploaded.Id])));

        Assert.Equal(4005, markdownError.ErrorCode);
        Assert.Equal(4005, explicitError.ErrorCode);
        Assert.Equal(deletedAt, await db.Set<QuickNoteAttachmentEntity>()
            .IgnoreQueryFilters()
            .Where(a => a.Id == uploaded.Id)
            .Select(a => a.DeletedAt)
            .SingleAsync());
    }

    [Fact]
    public async Task DownloadAsync_RejectsOtherUsersAttachment()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var otherAttachments = CreateAttachmentService(db, OtherUserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await otherAttachments.UploadAsync(content, "private.png", "image/png", content.Length);
        var attachments = CreateAttachmentService(db, UserId, storage);

        var error = await Assert.ThrowsAsync<DomainException>(() => attachments.DownloadAsync(uploaded.Id));

        Assert.Equal(40301, error.ErrorCode);
    }

    [Fact]
    public async Task DownloadAsync_RejectsDeletedAttachment()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var attachments = CreateAttachmentService(db, UserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "deleted.png", "image/png", content.Length);
        await attachments.DeleteAsync(uploaded.Id);

        var error = await Assert.ThrowsAsync<DomainException>(() => attachments.DownloadAsync(uploaded.Id));

        Assert.Equal(4006, error.ErrorCode);
    }

    /// <summary>
    /// 删除附件必须同时清理**存储层**：附件实体存在用户自己的 OneDrive 里，
    /// 只软删元数据会把文件永久留在对方网盘（用户删了附件却在 OneDrive 里仍能看到）。
    /// </summary>
    [Fact]
    public async Task DeleteAsync_RemovesObjectFromStorage()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var attachments = CreateAttachmentService(db, UserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "gone.png", "image/png", content.Length);

        var attachment = await db.Set<QuickNoteAttachmentEntity>().AsNoTracking().SingleAsync();
        Assert.Contains(attachment.ObjectKey, storage.StoredObjects.Keys);

        await attachments.DeleteAsync(uploaded.Id);

        Assert.Contains(attachment.ObjectKey, storage.DeletedObjectKeys);
        Assert.DoesNotContain(attachment.ObjectKey, storage.StoredObjects.Keys);
        // 软删后要被全局过滤器挡在常规查询外，因此这里显式忽略过滤器回读
        var reloaded = await db.Set<QuickNoteAttachmentEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync();
        Assert.NotNull(reloaded.DeletedAt);
    }

    /// <summary>
    /// 远端删除失败时本地**不得**标记为已删除：否则用户以为附件没了，
    /// 实际文件仍留在 OneDrive，且本地再也无法重试清理。
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WhenStorageFails_KeepsLocalStateUnchanged()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage { DeleteException = new InvalidOperationException("graph down") };
        var attachments = CreateAttachmentService(db, UserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await attachments.UploadAsync(content, "stay.png", "image/png", content.Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() => attachments.DeleteAsync(uploaded.Id));

        var reloaded = await db.Set<QuickNoteAttachmentEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync();
        Assert.Null(reloaded.DeletedAt);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(QuickNoteEntity).Assembly);        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"quick-note-attachments-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static QuickNoteAttachmentService CreateAttachmentService(
        PimDbContext db,
        Guid userId,
        IQuickNoteObjectStorage storage)
        => new(db, new FixedCurrentUserService(userId), storage);

    private static QuickNoteService CreateNoteService(
        PimDbContext db,
        Guid userId,
        QuickNoteAttachmentService attachments)
        => new(db, new FixedCurrentUserService(userId), new AuditLogService(db), attachments);

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeObjectStorage : IQuickNoteObjectStorage
    {
        public Dictionary<string, StoredObject> StoredObjects { get; } = new();

        /// <summary>最近一次 StoreAsync 收到的 userId，用于验证身份是显式传入的。</summary>
        public Guid? LastStoreUserId { get; private set; }

        public async Task<string> StoreAsync(
            Guid userId,
            string objectKey,
            Stream content,
            string contentType,
            long sizeBytes,
            CancellationToken ct = default)
        {
            LastStoreUserId = userId;
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            StoredObjects[objectKey] = new StoredObject(copy.ToArray(), contentType, sizeBytes);
            return objectKey;
        }

        public Task<Stream> OpenReadAsync(Guid userId, string objectKey, CancellationToken ct = default)
        {
            Stream stream = new MemoryStream(StoredObjects[objectKey].Bytes);
            return Task.FromResult(stream);
        }

        public Task DeleteAsync(Guid userId, string objectKey, CancellationToken ct = default)
        {
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            DeletedObjectKeys.Add(objectKey);
            StoredObjects.Remove(objectKey);
            return Task.CompletedTask;
        }

        /// <summary>被删除的远端 objectKey 记录，用于验证删除确实清了存储层。</summary>
        public List<string> DeletedObjectKeys { get; } = [];

        /// <summary>设置后 DeleteAsync 抛出该异常，用于验证本地状态不被提前改动。</summary>
        public Exception? DeleteException { get; set; }
    }

    private sealed record StoredObject(byte[] Bytes, string ContentType, long SizeBytes);
}
