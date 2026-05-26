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

public class QuickNoteServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task CreateAsync_CreatesInboxNoteAndAuditLog()
    {
        await using var db = CreateDb();
        var service = CreateService(db, UserId);

        var created = await service.CreateAsync(new CreateQuickNoteRequest("hello **world**", "web-floating", null));

        Assert.Equal("hello **world**", created.ContentMarkdown);
        Assert.Equal(QuickNoteStatuses.Inbox, created.Status);
        Assert.Equal("web-floating", created.Source);

        var note = await db.Set<QuickNoteEntity>().SingleAsync();
        Assert.Equal(UserId, note.UserId);

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("quick_notes.create", audit.Action);
        Assert.Equal(created.Id.ToString(), audit.ResourceId);
    }

    [Fact]
    public async Task ListAsync_FiltersByStatusAndSearch()
    {
        await using var db = CreateDb();
        var service = CreateService(db, UserId);
        var alpha = await service.CreateAsync(new CreateQuickNoteRequest("alpha note", "web-page", null));
        var beta = await service.CreateAsync(new CreateQuickNoteRequest("beta note", "web-page", null));
        await service.ArchiveAsync(beta.Id);

        var result = await service.ListAsync(new QuickNoteListQuery(QuickNoteStatuses.Inbox, "alpha", 1, 20));

        var item = Assert.Single(result.Items);
        Assert.Equal(alpha.Id, item.Id);
        Assert.Equal(QuickNoteStatuses.Inbox, item.Status);
    }

    [Fact]
    public async Task UpdateAsync_RejectsInvalidStatus()
    {
        await using var db = CreateDb();
        var service = CreateService(db, UserId);
        var note = await service.CreateAsync(new CreateQuickNoteRequest("hello", "web-page", null));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.UpdateAsync(note.Id, new UpdateQuickNoteRequest("hello", "done", null)));

        Assert.Equal(4003, error.ErrorCode);
    }

    [Fact]
    public async Task ProcessArchiveRestoreAndDelete_ApplyExpectedState()
    {
        await using var db = CreateDb();
        var service = CreateService(db, UserId);
        var note = await service.CreateAsync(new CreateQuickNoteRequest("hello", "web-page", null));

        var processed = await service.ProcessAsync(note.Id);
        Assert.Equal(QuickNoteStatuses.Processed, processed.Status);

        var archived = await service.ArchiveAsync(note.Id);
        Assert.Equal(QuickNoteStatuses.Archived, archived.Status);
        Assert.NotNull(archived.ArchivedAt);

        var restored = await service.RestoreAsync(note.Id, new RestoreQuickNoteRequest(QuickNoteStatuses.Inbox));
        Assert.Equal(QuickNoteStatuses.Inbox, restored.Status);
        Assert.Null(restored.ArchivedAt);

        await service.DeleteAsync(note.Id);

        Assert.Empty(await db.Set<QuickNoteEntity>().ToListAsync());
        var deleted = await db.Set<QuickNoteEntity>().IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(deleted.DeletedAt);
    }

    [Fact]
    public async Task GetAsync_RejectsOtherUsersNote()
    {
        await using var db = CreateDb();
        var otherNote = new QuickNoteEntity
        {
            UserId = OtherUserId,
            ContentMarkdown = "private",
            Source = QuickNoteSources.WebPage
        };
        db.Set<QuickNoteEntity>().Add(otherNote);
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetAsync(otherNote.Id));

        Assert.Equal(4004, error.ErrorCode);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(QuickNoteEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"quick-note-service-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static QuickNoteService CreateService(PimDbContext db, Guid userId)
    {
        var currentUser = new FixedCurrentUserService(userId);
        var attachments = new QuickNoteAttachmentService(db, currentUser, new FakeObjectStorage());
        return new QuickNoteService(db, currentUser, new AuditLogService(db), attachments);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeObjectStorage : IQuickNoteObjectStorage
    {
        public Task<string> StoreAsync(
            string objectKey,
            Stream content,
            string contentType,
            long sizeBytes,
            CancellationToken ct = default)
            => Task.FromResult(objectKey);

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string objectKey, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
