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
    public async Task FileProvider_NoImplicitNextcloudDefault_StatusStartsPending()
    {
        await using var db = CreateDb();
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            Provider = "onedrive",
        };

        db.Set<FileProviderEntity>().Add(provider);
        await db.SaveChangesAsync();

        var saved = await db.Set<FileProviderEntity>().SingleAsync();
        // 工单 05 / F3：Nextcloud 时代的 "nextcloud" 默认值已删除 —— 未显式赋值时
        // 落库的就是空串，而不是被误读成「存在一条 Nextcloud 来源」。
        Assert.Equal("onedrive", saved.Provider);
        Assert.Equal("pending", saved.Status);
    }

    [Fact]
    public void FileProvider_ModelHasNoDefaultForProviderColumn()
    {
        using var db = CreateDb();

        // F3：列保留、默认值去掉。模型层必须也能看出来，否则迁移与模型会漂移。
        var provider = db.Model.FindEntityType(typeof(FileProviderEntity))!;
        var property = provider.FindProperty(nameof(FileProviderEntity.Provider))!;
        Assert.Null(property.GetDefaultValue());
    }

    [Fact]
    public void FileProvider_UniqueIndexIsScopedToAccount_NotToNextcloudCoordinates()
    {
        using var db = CreateDb();

        // F2：旧的 (user_id, provider, base_url, username) 唯一索引已随删列一起消失，
        // 取而代之的是账号维度；且必须是**部分**唯一索引
        // （account_id 为 NULL 的待绑定行要允许多条并存）。
        var index = Index<FileProviderEntity>(
            db,
            nameof(FileProviderEntity.UserId),
            nameof(FileProviderEntity.Provider),
            nameof(FileProviderEntity.AccountId));

        Assert.True(index.IsUnique);
        Assert.Equal("account_id IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void FileProvider_RemovedNextcloudColumnsAreNotMapped()
    {
        using var db = CreateDb();

        // F3 的模型侧证据：四个死字段不再是实体属性/列。
        // 真正的「零残留」由源码 grep + 迁移后的真库断言（AC-3）共同举证。
        var provider = db.Model.FindEntityType(typeof(FileProviderEntity))!;
        var columnNames = provider.GetProperties().Select(p => p.GetColumnName()).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("base_url", columnNames);
        Assert.DoesNotContain("internal_base_url", columnNames);
        Assert.DoesNotContain("username", columnNames);
        Assert.DoesNotContain("app_password_secret", columnNames);
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
        Assert.Equal("device_code_encrypted", ColumnName<FileProviderEntity>(db, nameof(FileProviderEntity.PendingAuthorizationSecretEncrypted)));
        Assert.Equal("is_default", ColumnName<FileProviderEntity>(db, nameof(FileProviderEntity.IsDefault)));
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
        Provider = "onedrive",
        AccountId = "account-1",
        Status = "connected"
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
