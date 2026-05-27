using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileModelTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task FileProvider_DefaultsToNextcloudPendingAndStoresSecret()
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
    public void FileProvider_UniqueIndexProtectsPerUserProviderUrlAndUsername()
    {
        using var db = CreateDb();

        var index = Index<FileProviderEntity>(
            db,
            nameof(FileProviderEntity.UserId),
            nameof(FileProviderEntity.Provider),
            nameof(FileProviderEntity.BaseUrl),
            nameof(FileProviderEntity.Username));

        Assert.True(index.IsUnique);
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

    [Fact]
    public void FileVersion_ModelEnforcesCurrentVersionBelongsToItemAndSingleCurrentVersion()
    {
        using var db = CreateDb();

        var currentVersionFk = ForeignKey<FileItemEntity>(
            db,
            typeof(FileVersionEntity),
            nameof(FileItemEntity.Id),
            nameof(FileItemEntity.CurrentVersionId));
        Assert.True(
            PropertyNames(currentVersionFk.PrincipalKey.Properties).SequenceEqual(
                new[] { nameof(FileVersionEntity.FileItemId), nameof(FileVersionEntity.Id) }));
        Assert.Equal(DeleteBehavior.Restrict, currentVersionFk.DeleteBehavior);

        var versionItemKey = Entity<FileVersionEntity>(db)
            .GetKeys()
            .SingleOrDefault(key => PropertyNames(key.Properties).SequenceEqual(
                [nameof(FileVersionEntity.FileItemId), nameof(FileVersionEntity.Id)]));
        Assert.NotNull(versionItemKey);

        var currentIndex = Index<FileVersionEntity>(
            db,
            nameof(FileVersionEntity.FileItemId),
            nameof(FileVersionEntity.IsCurrent));
        Assert.True(currentIndex.IsUnique);
        Assert.Equal("is_current = true", currentIndex.GetFilter());
    }

    [Fact]
    public void VersionScopedChildren_UseCompositeVersionForeignKeys()
    {
        using var db = CreateDb();

        AssertVersionScopedChild<FileChunkEntity>(
            db,
            DeleteBehavior.Cascade,
            nameof(FileChunkEntity.FileItemId),
            nameof(FileChunkEntity.VersionId));
        AssertVersionScopedChild<FileAiResultEntity>(
            db,
            DeleteBehavior.Cascade,
            nameof(FileAiResultEntity.FileItemId),
            nameof(FileAiResultEntity.VersionId));
        AssertVersionScopedChild<FileIndexJobEntity>(
            db,
            DeleteBehavior.Restrict,
            nameof(FileIndexJobEntity.FileItemId),
            nameof(FileIndexJobEntity.VersionId));
    }

    [Fact]
    public void FileChunk_QdrantPointIdIsUniqueWhenPresent()
    {
        using var db = CreateDb();

        var index = Index<FileChunkEntity>(db, nameof(FileChunkEntity.QdrantPointId));

        Assert.True(index.IsUnique);
        Assert.Equal("qdrant_point_id IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void FileSuggestion_ConfidenceHasExplicitPrecisionAndRange()
    {
        using var db = CreateDb();

        var entityType = Entity<FileSuggestionEntity>(db);
        var confidence = entityType.FindProperty(nameof(FileSuggestionEntity.Confidence));
        Assert.NotNull(confidence);
        Assert.Equal(5, confidence.GetPrecision());
        Assert.Equal(4, confidence.GetScale());

        var designTimeEntityType = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(FileSuggestionEntity));
        Assert.NotNull(designTimeEntityType);
        var constraint = designTimeEntityType.GetCheckConstraints()
            .SingleOrDefault(c => c.Name == "CK_file_suggestions_confidence_range");
        Assert.NotNull(constraint);
        Assert.Equal("confidence >= 0 AND confidence <= 1", constraint.Sql);
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
        var entityType = Entity<TEntity>(db);
        var tableName = entityType.GetTableName();
        Assert.NotNull(tableName);
        var property = entityType.FindProperty(propertyName);
        Assert.NotNull(property);

        return property.GetColumnName(StoreObjectIdentifier.Table(tableName));
    }

    private static IEntityType Entity<TEntity>(PimDbContext db)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entityType);
        return entityType;
    }

    private static IIndex Index<TEntity>(PimDbContext db, params string[] propertyNames)
    {
        var index = Entity<TEntity>(db)
            .GetIndexes()
            .SingleOrDefault(i => PropertyNames(i.Properties).SequenceEqual(propertyNames));
        Assert.NotNull(index);
        return index;
    }

    private static IForeignKey ForeignKey<TEntity>(PimDbContext db, Type principalType, params string[] propertyNames)
    {
        var foreignKey = Entity<TEntity>(db)
            .GetForeignKeys()
            .SingleOrDefault(fk =>
                fk.PrincipalEntityType.ClrType == principalType &&
                PropertyNames(fk.Properties).SequenceEqual(propertyNames));
        Assert.NotNull(foreignKey);
        return foreignKey;
    }

    private static void AssertVersionScopedChild<TEntity>(
        PimDbContext db,
        DeleteBehavior deleteBehavior,
        params string[] propertyNames)
    {
        var foreignKey = ForeignKey<TEntity>(db, typeof(FileVersionEntity), propertyNames);

        Assert.True(
            PropertyNames(foreignKey.PrincipalKey.Properties).SequenceEqual(
                new[] { nameof(FileVersionEntity.FileItemId), nameof(FileVersionEntity.Id) }));
        Assert.Equal(deleteBehavior, foreignKey.DeleteBehavior);
    }

    private static string[] PropertyNames(IEnumerable<IProperty> properties) =>
        properties.Select(p => p.Name).ToArray();

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
