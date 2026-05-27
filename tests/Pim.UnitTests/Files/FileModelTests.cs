using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileModelTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task FileProvider_DefaultsToNextcloudPendingAndProtectsPerUserUniqueness()
    {
        await using var db = CreateDb();
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            BaseUrl = "https://cloud.example.test",
            InternalBaseUrl = "http://nextcloud",
            Username = "alice",
            AppPasswordSecret = "protected-secret"
        };

        db.Set<FileProviderEntity>().Add(provider);
        await db.SaveChangesAsync();

        var saved = await db.Set<FileProviderEntity>().SingleAsync();
        Assert.Equal("nextcloud", saved.Provider);
        Assert.Equal("pending", saved.Status);
        Assert.Equal("protected-secret", saved.AppPasswordSecret);
    }

    [Fact]
    public async Task FileItem_UsesExternalFileIdAsStableProviderIdentity()
    {
        await using var db = CreateDb();
        var provider = CreateProvider();
        var item = new FileItemEntity
        {
            Provider = provider,
            ExternalFileId = "00000123ocabc",
            ParentExternalFileId = "00000001ocabc",
            Path = "/Reports/report.docx",
            Name = "report.docx",
            ItemType = "file",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Size = 12345,
            Etag = "\"abc\"",
            Permissions = "RGDNVW",
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow,
            SyncedAt = DateTimeOffset.UtcNow
        };

        db.Set<FileItemEntity>().Add(item);
        await db.SaveChangesAsync();

        var saved = await db.Set<FileItemEntity>().SingleAsync();
        Assert.Equal("00000123ocabc", saved.ExternalFileId);
        Assert.Equal("/Reports/report.docx", saved.Path);
        Assert.False(saved.IsDeleted);
    }

    [Fact]
    public async Task HistoricalVersions_AreNotCurrentByDefault()
    {
        await using var db = CreateDb();
        var item = CreateItem(CreateProvider());
        var current = new FileVersionEntity
        {
            FileItem = item,
            ExternalVersionId = "current:etag-1",
            Etag = "etag-1",
            Size = 100,
            ModifiedAt = DateTimeOffset.UtcNow,
            Source = "current",
            IsCurrent = true,
            SyncedAt = DateTimeOffset.UtcNow
        };
        var history = new FileVersionEntity
        {
            FileItem = item,
            ExternalVersionId = "1700000000",
            Etag = "etag-old",
            Size = 90,
            ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Source = "history",
            IsCurrent = false,
            SyncedAt = DateTimeOffset.UtcNow
        };

        db.Set<FileVersionEntity>().AddRange(current, history);
        await db.SaveChangesAsync();

        Assert.Single(await db.Set<FileVersionEntity>().Where(v => v.IsCurrent).ToListAsync());
        Assert.Single(await db.Set<FileVersionEntity>().Where(v => v.Source == "history").ToListAsync());
    }

    [Fact]
    public async Task DeletedItems_AreVisibleToFilesModuleQueries()
    {
        await using var db = CreateDb();
        var item = CreateItem(CreateProvider());
        item.IsDeleted = true;
        item.DeletedAt = DateTimeOffset.UtcNow;

        db.Set<FileItemEntity>().Add(item);
        await db.SaveChangesAsync();

        var saved = await db.Set<FileItemEntity>().SingleAsync();
        Assert.True(saved.IsDeleted);
        Assert.NotNull(saved.DeletedAt);
    }

    [Fact]
    public void FilesModel_UsesSnakeCaseColumns()
    {
        using var db = CreateDb();

        Assert.Equal("id", ColumnName<FileProviderEntity>(db, nameof(FileProviderEntity.Id)));
        Assert.Equal("user_id", ColumnName<FileProviderEntity>(db, nameof(FileProviderEntity.UserId)));
        Assert.Equal("app_password_secret", ColumnName<FileProviderEntity>(db, nameof(FileProviderEntity.AppPasswordSecret)));
        Assert.Equal("external_file_id", ColumnName<FileItemEntity>(db, nameof(FileItemEntity.ExternalFileId)));
        Assert.Equal("file_item_id", ColumnName<FileVersionEntity>(db, nameof(FileVersionEntity.FileItemId)));
        Assert.Equal("qdrant_point_id", ColumnName<FileChunkEntity>(db, nameof(FileChunkEntity.QdrantPointId)));
        Assert.Equal("evidence_chunk_ids_json", ColumnName<FileAiResultEntity>(db, nameof(FileAiResultEntity.EvidenceChunkIdsJson)));
        Assert.Equal("suggestion_type", ColumnName<FileSuggestionEntity>(db, nameof(FileSuggestionEntity.SuggestionType)));
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"files-model-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static string? ColumnName<TEntity>(PimDbContext db, string propertyName)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entityType);
        var tableName = entityType.GetTableName();
        Assert.NotNull(tableName);
        var property = entityType.FindProperty(propertyName);
        Assert.NotNull(property);

        return property.GetColumnName(StoreObjectIdentifier.Table(tableName));
    }

    private static FileProviderEntity CreateProvider() => new()
    {
        UserId = UserId,
        BaseUrl = "https://cloud.example.test",
        InternalBaseUrl = "http://nextcloud",
        Username = "alice",
        AppPasswordSecret = "protected-secret"
    };

    private static FileItemEntity CreateItem(FileProviderEntity provider) => new()
    {
        Provider = provider,
        ExternalFileId = "00000123ocabc",
        Path = "/report.txt",
        Name = "report.txt",
        ItemType = "file",
        MimeType = "text/plain",
        Size = 12,
        Etag = "etag-1",
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow,
        SyncedAt = DateTimeOffset.UtcNow
    };
}
