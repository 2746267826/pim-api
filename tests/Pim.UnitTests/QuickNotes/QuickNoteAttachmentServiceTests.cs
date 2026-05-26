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

        var uploaded = await service.UploadAsync(content, @"C:\tmp\capture.png", "image/png", content.Length);

        Assert.Equal("capture.png", uploaded.FileName);
        Assert.Equal("image/png", uploaded.ContentType);
        Assert.Equal(content.Length, uploaded.SizeBytes);
        Assert.Equal($"/api/v1/quick-notes/attachments/{uploaded.Id}/download", uploaded.DownloadUrl);
        Assert.Equal(uploaded.DownloadUrl, uploaded.PreviewUrl);

        var attachment = await db.Set<QuickNoteAttachmentEntity>().SingleAsync();
        Assert.Equal(uploaded.Id, attachment.Id);
        Assert.Equal(UserId, attachment.UserId);
        Assert.Null(attachment.QuickNoteId);
        Assert.Equal("minio", attachment.StorageProvider);
        Assert.StartsWith($"quick-notes/{UserId:N}/{uploaded.Id:N}/", attachment.ObjectKey);
        Assert.True(storage.StoredObjects.ContainsKey(attachment.ObjectKey));
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
    public async Task DownloadAsync_RejectsOtherUsersAttachment()
    {
        await using var db = CreateDb();
        var storage = new FakeObjectStorage();
        var otherAttachments = CreateAttachmentService(db, OtherUserId, storage);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("image-bytes"));
        var uploaded = await otherAttachments.UploadAsync(content, "private.png", "image/png", content.Length);
        var attachments = CreateAttachmentService(db, UserId, storage);

        var error = await Assert.ThrowsAsync<DomainException>(() => attachments.DownloadAsync(uploaded.Id));

        Assert.Equal(4006, error.ErrorCode);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(QuickNoteEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
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

        public async Task<string> StoreAsync(
            string objectKey,
            Stream content,
            string contentType,
            long sizeBytes,
            CancellationToken ct = default)
        {
            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            StoredObjects[objectKey] = new StoredObject(copy.ToArray(), contentType, sizeBytes);
            return objectKey;
        }

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default)
        {
            Stream stream = new MemoryStream(StoredObjects[objectKey].Bytes);
            return Task.FromResult(stream);
        }

        public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        {
            StoredObjects.Remove(objectKey);
            return Task.CompletedTask;
        }
    }

    private sealed record StoredObject(byte[] Bytes, string ContentType, long SizeBytes);
}
