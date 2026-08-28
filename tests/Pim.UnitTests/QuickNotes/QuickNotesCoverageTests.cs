using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.QuickNotes.DTOs;
using Pim.Module.QuickNotes.Entities;
using Pim.Module.QuickNotes.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.QuickNotes;

public class QuickNotesCoverageTests
{
    private static readonly Guid UserId = ServiceTestBase.DefaultUserId;
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // 1. ListAsync clamps page/pageSize
    [Fact]
    public async Task ListAsync_ClampsPageAndPageSize()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        for (int i = 0; i < 3; i++)
            await svc.CreateAsync(new CreateQuickNoteRequest($"note {i}", "web-page", null));

        var pageZero = await svc.ListAsync(new QuickNoteListQuery(null, null, 0, 0));
        Assert.Equal(1, pageZero.Page);
        Assert.Equal(1, pageZero.PageSize);
        Assert.Equal(3, pageZero.TotalCount);
        Assert.Single(pageZero.Items);

        var huge = await svc.ListAsync(new QuickNoteListQuery(null, null, 1, 200));
        Assert.Equal(100, huge.PageSize);
        Assert.Equal(1, huge.TotalPages);
    }

    // 2. ListAsync trims status and rejects invalid
    [Fact]
    public async Task ListAsync_TrimsStatusAndRejectsInvalid()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        await svc.CreateAsync(new CreateQuickNoteRequest("a", "web-page", null));
        // trimmed status should work
        var ok = await svc.ListAsync(new QuickNoteListQuery(" inbox ", null, 1, 20));
        Assert.Single(ok.Items);

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.ListAsync(new QuickNoteListQuery("done", null, 1, 20)));
        Assert.Equal(4003, ex.ErrorCode);
    }

    // 3. ListAsync search trimmed
    [Fact]
    public async Task ListAsync_SearchTrimmedAndMatches()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        await svc.CreateAsync(new CreateQuickNoteRequest("alpha beta", "web-page", null));
        await svc.CreateAsync(new CreateQuickNoteRequest("gamma", "web-page", null));

        var r = await svc.ListAsync(new QuickNoteListQuery(null, " alpha ", 1, 20));
        Assert.Single(r.Items);
        Assert.Contains("alpha", r.Items[0].ContentPreview);

        var empty = await svc.ListAsync(new QuickNoteListQuery(null, "   ", 1, 20));
        Assert.Equal(2, empty.TotalCount);
    }

    // 4. BuildPreview strips markdown and truncates
    [Fact]
    public async Task ListAsync_BuildPreview_StripsMarkdownAndTruncates()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        var longContent = "# " + new string('a', 200) + " *bold* _italic_ `code`\r\nnext line";
        await svc.CreateAsync(new CreateQuickNoteRequest(longContent, "web-page", null));

        var r = await svc.ListAsync(new QuickNoteListQuery(null, null, 1, 20));
        var preview = r.Items[0].ContentPreview;
        Assert.DoesNotContain("#", preview);
        Assert.DoesNotContain("*", preview);
        Assert.DoesNotContain("_", preview);
        Assert.DoesNotContain("`", preview);
        Assert.DoesNotContain("\r", preview);
        Assert.DoesNotContain("\n", preview);
        Assert.True(preview.Length <= 140);
        Assert.Equal(140, preview.Length);
    }

    // 5. CreateAsync normalizes source
    [Fact]
    public async Task CreateAsync_NormalizesSource()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);

        var n1 = await svc.CreateAsync(new CreateQuickNoteRequest("c1", null, null));
        Assert.Equal(QuickNoteSources.WebPage, n1.Source);

        var n2 = await svc.CreateAsync(new CreateQuickNoteRequest("c2", "   ", null));
        Assert.Equal(QuickNoteSources.WebPage, n2.Source);

        var n3 = await svc.CreateAsync(new CreateQuickNoteRequest("c3", " web-floating ", null));
        Assert.Equal("web-floating", n3.Source);

        var n4 = await svc.CreateAsync(new CreateQuickNoteRequest("c4", "web-floating", null));
        Assert.Equal("web-floating", n4.Source);
    }

    // 6. MergeAttachmentIds dedupes explicit + markdown
    [Fact]
    public async Task CreateAsync_MergesExplicitAndMarkdownIds_Dedupes()
    {
        await using var db = ServiceTestBase.CreateDb();
        var storage = new FakeStorage();
        var attSvc = CreateAttachmentService(db, UserId, storage);
        var svc = CreateService(db, UserId, attSvc);
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var up = await attSvc.UploadAsync(ms, "a.png", "image/png", 10);
        var markdown = $"![a]({up.DownloadUrl})";
        // explicit contains same id plus duplicate, markdown also contains same id -> deduped to single
        var created = await svc.CreateAsync(new CreateQuickNoteRequest(markdown, "web-page", new List<Guid> { up.Id, up.Id }));
        Assert.Single(created.Attachments);
        Assert.Equal(up.Id, created.Attachments[0].Id);
    }

    // 7. Markdown only binding without explicit ids
    [Fact]
    public async Task CreateAsync_BindsFromMarkdownOnly()
    {
        await using var db = ServiceTestBase.CreateDb();
        var storage = new FakeStorage();
        var attSvc = CreateAttachmentService(db, UserId, storage);
        var svc = CreateService(db, UserId, attSvc);
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var up = await attSvc.UploadAsync(ms, "b.png", "image/png", 10);
        var md = $"ref {up.DownloadUrl} end";
        var created = await svc.CreateAsync(new CreateQuickNoteRequest(md, "web-page", null));
        Assert.Single(created.Attachments);
        Assert.Equal(up.Id, created.Attachments[0].Id);
        // previewUrl for image should be downloadUrl
        Assert.Equal(up.DownloadUrl, created.Attachments[0].PreviewUrl);
    }

    // 8. UpdateAsync status branches sets/clears ArchivedAt
    [Fact]
    public async Task UpdateAsync_StatusChange_SetsAndClearsArchivedAt()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        var note = await svc.CreateAsync(new CreateQuickNoteRequest("hello", "web-page", null));

        var archived = await svc.UpdateAsync(note.Id, new UpdateQuickNoteRequest("hello", "archived", null));
        Assert.Equal(QuickNoteStatuses.Archived, archived.Status);
        Assert.NotNull(archived.ArchivedAt);

        var inbox = await svc.UpdateAsync(note.Id, new UpdateQuickNoteRequest("hello", " inbox ", null));
        Assert.Equal(QuickNoteStatuses.Inbox, inbox.Status);
        Assert.Null(inbox.ArchivedAt);
    }

    // 9. UpdateAsync whitespace status keeps previous
    [Fact]
    public async Task UpdateAsync_WhitespaceStatusKeepsPrevious()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        var note = await svc.CreateAsync(new CreateQuickNoteRequest("hello", "web-page", null));
        var updated = await svc.UpdateAsync(note.Id, new UpdateQuickNoteRequest("new content", "   ", null));
        Assert.Equal(QuickNoteStatuses.Inbox, updated.Status);
        Assert.Equal("new content", updated.ContentMarkdown);
        // null content markdown -> empty string
        var empty = await svc.UpdateAsync(note.Id, new UpdateQuickNoteRequest(null!, null, null));
        Assert.Equal(string.Empty, empty.ContentMarkdown);
    }

    // 10. RestoreAsync null -> inbox and archived branches
    [Fact]
    public async Task RestoreAsync_NullBecomesInbox_AndArchivedSetsTime()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        var note = await svc.CreateAsync(new CreateQuickNoteRequest("x", "web-page", null));
        await svc.ArchiveAsync(note.Id);

        var r1 = await svc.RestoreAsync(note.Id, new RestoreQuickNoteRequest(null!));
        Assert.Equal(QuickNoteStatuses.Inbox, r1.Status);
        Assert.Null(r1.ArchivedAt);

        var r2 = await svc.RestoreAsync(note.Id, "archived");
        Assert.Equal(QuickNoteStatuses.Archived, r2.Status);
        Assert.NotNull(r2.ArchivedAt);

        var r3 = await svc.RestoreAsync(note.Id, " inbox ");
        Assert.Equal(QuickNoteStatuses.Inbox, r3.Status);
        Assert.Null(r3.ArchivedAt);
    }

    // 11. RestoreAsync rejects invalid status
    [Fact]
    public async Task RestoreAsync_RejectsInvalidStatus()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db, UserId);
        var note = await svc.CreateAsync(new CreateQuickNoteRequest("x", "web-page", null));
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.RestoreAsync(note.Id, "done"));
        Assert.Equal(4003, ex.ErrorCode);
        var ex2 = await Assert.ThrowsAsync<DomainException>(() => svc.RestoreAsync(note.Id, new RestoreQuickNoteRequest("invalid")));
        Assert.Equal(4003, ex2.ErrorCode);
    }

    // 12. DeleteAsync soft deletes note and attachments
    [Fact]
    public async Task DeleteAsync_SoftDeletesNoteAndAttachments()
    {
        await using var db = ServiceTestBase.CreateDb();
        var storage = new FakeStorage();
        var attSvc = CreateAttachmentService(db, UserId, storage);
        var svc = CreateService(db, UserId, attSvc);
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var up = await attSvc.UploadAsync(ms, "f.png", "image/png", 10);
        var note = await svc.CreateAsync(new CreateQuickNoteRequest($"a {up.DownloadUrl}", "web-page", new List<Guid> { up.Id }));
        Assert.Single(note.Attachments);

        await svc.DeleteAsync(note.Id);

        Assert.Empty(await db.Set<QuickNoteEntity>().ToListAsync());
        var deletedNote = await db.Set<QuickNoteEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(deletedNote.DeletedAt);
        var deletedAtt = await db.Set<QuickNoteAttachmentEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(deletedAtt.DeletedAt);
        Assert.Equal(note.Id, deletedAtt.QuickNoteId);
    }

    // 13. Unauthenticated and not found
    [Fact]
    public async Task LoadNote_ThrowsWhenUnauthenticatedOrNotFound()
    {
        await using var db = ServiceTestBase.CreateDb();
        var anonSvc = CreateService(db, null);
        var exAuth = await Assert.ThrowsAsync<DomainException>(() => anonSvc.ListAsync(new QuickNoteListQuery(null, null, 1, 20)));
        Assert.Equal(1002, exAuth.ErrorCode);

        var svc = CreateService(db, UserId);
        var note = await svc.CreateAsync(new CreateQuickNoteRequest("own", "web-page", null));
        var otherSvc = CreateService(db, OtherUserId);
        var exNotFound = await Assert.ThrowsAsync<DomainException>(() => otherSvc.GetAsync(note.Id));
        Assert.Equal(4004, exNotFound.ErrorCode);

        var exMissing = await Assert.ThrowsAsync<DomainException>(() => svc.GetAsync(Guid.NewGuid()));
        Assert.Equal(4004, exMissing.ErrorCode);
    }

    // 14. Attachment upload validates and normalizes
    [Fact]
    public async Task AttachmentUpload_ValidatesAndNormalizes()
    {
        await using var db = ServiceTestBase.CreateDb();
        var storage = new FakeStorage();
        var svc = CreateAttachmentService(db, UserId, storage);

        await Assert.ThrowsAsync<DomainException>(() => svc.UploadAsync(new MemoryStream(new byte[1]), "", "image/png", 1));
        await Assert.ThrowsAsync<DomainException>(() => svc.UploadAsync(new MemoryStream(new byte[1]), "   ", "image/png", 1));
        await Assert.ThrowsAsync<DomainException>(() => svc.UploadAsync(new MemoryStream(new byte[1]), "a.txt", "text/plain", -1));
        // Path.GetFileName branch: "/tmp/../" -> safeName empty -> throws
        await Assert.ThrowsAsync<DomainException>(() => svc.UploadAsync(new MemoryStream(new byte[1]), "///", "text/plain", 1));

        // contentType null -> octet-stream, whitespace trimmed, image detection
        await using var ms1 = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var up1 = await svc.UploadAsync(ms1, "a.txt", null, 1);
        Assert.Equal("application/octet-stream", up1.ContentType);
        Assert.Null(up1.PreviewUrl);

        await using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var up2 = await svc.UploadAsync(ms2, "/tmp/cap.PNG", " image/png ", 1);
        Assert.Equal("cap.PNG", up2.FileName);
        Assert.Equal("image/png", up2.ContentType);
        Assert.NotNull(up2.PreviewUrl);
        Assert.Equal(up2.DownloadUrl, up2.PreviewUrl);

        await using var ms3 = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var up3 = await svc.UploadAsync(ms3, "doc.pdf", "application/pdf", 5);
        Assert.Null(up3.PreviewUrl);
    }

    // 15. Attachment Download/Delete/LoadBindable and Null storage
    [Fact]
    public async Task Attachment_DownloadDeleteAndLoadBindableBranches()
    {
        await using var db = ServiceTestBase.CreateDb();
        var storage = new FakeStorage();
        var svc = CreateAttachmentService(db, UserId, storage);

        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var up = await svc.UploadAsync(ms, "file.txt", "text/plain", 5);
        // Download success
        var dl = await svc.DownloadAsync(up.Id);
        Assert.Equal("file.txt", dl.FileName);
        Assert.Equal("text/plain", dl.ContentType);
        // delete then download should fail 4006
        await svc.DeleteAsync(up.Id);
        var del = await db.Set<QuickNoteAttachmentEntity>().IgnoreQueryFilters().SingleAsync(a => a.Id == up.Id);
        Assert.NotNull(del.DeletedAt);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.DownloadAsync(up.Id));
        Assert.Equal(4006, ex.ErrorCode);

        // LoadBindable empty returns empty
        var empty = await svc.LoadBindableAttachmentsAsync(Array.Empty<Guid>(), null);
        Assert.Empty(empty);

        // mismatch count throws 4005
        var fakeId = Guid.NewGuid();
        var ex2 = await Assert.ThrowsAsync<DomainException>(() => svc.LoadBindableAttachmentsAsync(new[] { fakeId }, null));
        Assert.Equal(4005, ex2.ErrorCode);

        // attachment already bound to other note throws 4005
        await using var db2 = ServiceTestBase.CreateDb();
        var svc2 = CreateAttachmentService(db2, UserId, storage);
        await using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var up2 = await svc2.UploadAsync(ms2, "a.png", "image/png", 1);
        var noteSvc2 = CreateService(db2, UserId, svc2);
        var note1 = await noteSvc2.CreateAsync(new CreateQuickNoteRequest("n1", "web-page", new List<Guid> { up2.Id }));
        var ex3 = await Assert.ThrowsAsync<DomainException>(() => svc2.LoadBindableAttachmentsAsync(new[] { up2.Id }, Guid.NewGuid()));
        Assert.Equal(4005, ex3.ErrorCode);
        // same targetNoteId should succeed (re-binding to same note)
        var ok = await svc2.LoadBindableAttachmentsAsync(new[] { up2.Id }, note1.Id);
        Assert.Single(ok);

        // Null storage throws
        var nullStorage = new NullQuickNoteObjectStorage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => nullStorage.StoreAsync("k", new MemoryStream(), "text/plain", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => nullStorage.OpenReadAsync("k"));
        await nullStorage.DeleteAsync("k"); // should not throw
    }

    private static QuickNoteService CreateService(PimDbContext db, Guid? userId, QuickNoteAttachmentService? attachments = null)
    {
        var user = new StubUser(userId);
        var att = attachments ?? new QuickNoteAttachmentService(db, user, new FakeStorage());
        return new QuickNoteService(db, user, new AuditLogService(db), att);
    }

    private static QuickNoteAttachmentService CreateAttachmentService(PimDbContext db, Guid userId, IQuickNoteObjectStorage storage)
        => new(db, new StubUser(userId), storage);

    private sealed class StubUser(Guid? uid) : ICurrentUserService
    {
        public Guid? UserId { get; } = uid;
        public string? Role => "user";
    }

    private sealed class FakeStorage : IQuickNoteObjectStorage
    {
        private readonly Dictionary<string, byte[]> _store = new();
        public Task<string> StoreAsync(string objectKey, Stream content, string contentType, long sizeBytes, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _store[objectKey] = ms.ToArray();
            return Task.FromResult(objectKey);
        }
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default)
        {
            _store.TryGetValue(objectKey, out var b);
            return Task.FromResult<Stream>(new MemoryStream(b ?? Array.Empty<byte>()));
        }
        public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        {
            _store.Remove(objectKey);
            return Task.CompletedTask;
        }
    }
}
