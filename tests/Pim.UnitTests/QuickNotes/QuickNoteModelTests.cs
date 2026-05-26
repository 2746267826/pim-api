using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.QuickNotes.Entities;
using Xunit;

namespace Pim.UnitTests.QuickNotes;

public class QuickNoteModelTests
{
    [Fact]
    public async Task QuickNote_DefaultsToInboxAndFiltersSoftDeletedRows()
    {
        PimDbContext.RegisterModuleAssembly(typeof(QuickNoteEntity).Assembly);
        await using var db = CreateDb();
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var active = new QuickNoteEntity
        {
            UserId = userId,
            ContentMarkdown = "active note",
            Source = "web-page"
        };
        var deleted = new QuickNoteEntity
        {
            UserId = userId,
            ContentMarkdown = "deleted note",
            Source = "web-page",
            DeletedAt = DateTimeOffset.UtcNow
        };

        db.Set<QuickNoteEntity>().AddRange(active, deleted);
        await db.SaveChangesAsync();

        var notes = await db.Set<QuickNoteEntity>().ToListAsync();

        var note = Assert.Single(notes);
        Assert.Equal(active.Id, note.Id);
        Assert.Equal(QuickNoteStatuses.Inbox, note.Status);
        Assert.Equal("{}", note.MetadataJson);
    }

    [Fact]
    public async Task QuickNoteAttachment_CanBeTemporaryBeforeNoteSave()
    {
        PimDbContext.RegisterModuleAssembly(typeof(QuickNoteEntity).Assembly);
        await using var db = CreateDb();
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var attachment = new QuickNoteAttachmentEntity
        {
            UserId = userId,
            StorageProvider = "minio",
            ObjectKey = "quick-notes/aaaaaaaa/file.txt",
            FileName = "file.txt",
            ContentType = "text/plain",
            SizeBytes = 12
        };

        db.Set<QuickNoteAttachmentEntity>().Add(attachment);
        await db.SaveChangesAsync();

        var saved = await db.Set<QuickNoteAttachmentEntity>().SingleAsync();
        Assert.Null(saved.QuickNoteId);
        Assert.Equal("{}", saved.MetadataJson);
    }

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"quick-note-model-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }
}
