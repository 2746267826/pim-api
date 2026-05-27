using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Infrastructure.TextExtraction;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileIndexingServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task IndexCurrentVersionAsync_WhenMimeTypeUnsupportedCreatesSkippedJobAndNoChunks()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var vectorStore = new FakeFileVectorStore();
        var item = await SeedFileWithCurrentVersionAsync(db, mimeType: "image/png");
        var service = CreateService(db, adapter, vectorStore, extractedText: "ignored text");

        var job = await service.IndexCurrentVersionAsync(item.Id);

        Assert.Equal("skipped", job.Status);
        Assert.Equal("mime_type", job.Stage);
        Assert.Equal(0, adapter.DownloadCallCount);
        Assert.Empty(await db.Set<FileChunkEntity>().ToListAsync());
        Assert.Empty(vectorStore.UpsertedVectors);
    }

    [Fact]
    public async Task IndexCurrentVersionAsync_WhenExtractedTextEmptyCreatesSkippedJobAndNoVectors()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var vectorStore = new FakeFileVectorStore();
        var item = await SeedFileWithCurrentVersionAsync(db);
        adapter.DownloadResult = new ProviderDownload(new MemoryStream("empty"u8.ToArray()), "text/plain", "report.txt");
        var service = CreateService(db, adapter, vectorStore, extractedText: "   ");

        var job = await service.IndexCurrentVersionAsync(item.Id);

        Assert.Equal("skipped", job.Status);
        Assert.Equal("extract", job.Stage);
        Assert.Equal(item.CurrentVersionId, job.VersionId);
        Assert.Empty(await db.Set<FileChunkEntity>().ToListAsync());
        Assert.Empty(vectorStore.UpsertedVectors);
    }

    [Fact]
    public async Task IndexCurrentVersionAsync_WhenCurrentVersionChangesReplacesChunksAndVectors()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var vectorStore = new FakeFileVectorStore();
        var item = await SeedFileWithCurrentVersionAsync(db);
        var oldVersion = await SeedVersionAsync(db, item, "current:old", "etag-old", isCurrent: false);
        db.Set<FileChunkEntity>().Add(new FileChunkEntity
        {
            FileItemId = item.Id,
            VersionId = oldVersion.Id,
            ChunkIndex = 0,
            Text = "old",
            TextHash = "old-hash",
            StartOffset = 0,
            EndOffset = 3,
            QdrantPointId = "old-point"
        });
        await db.SaveChangesAsync();
        adapter.DownloadResult = new ProviderDownload(new MemoryStream("download"u8.ToArray()), "text/plain", "report.txt");
        var service = CreateService(
            db,
            adapter,
            vectorStore,
            extractedText: "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda");

        var job = await service.IndexCurrentVersionAsync(item.Id);

        Assert.Equal("succeeded", job.Status);
        Assert.Equal("qdrant", job.Stage);
        Assert.Equal(1, vectorStore.EnsureCollectionCallCount);
        Assert.Equal(1, vectorStore.DeleteFileVectorsCallCount);
        Assert.Equal(item.Id, vectorStore.LastDeletedFileItemId);

        var chunks = await db.Set<FileChunkEntity>()
            .OrderBy(chunk => chunk.ChunkIndex)
            .ToListAsync();
        Assert.NotEmpty(chunks);
        Assert.DoesNotContain(chunks, chunk => chunk.VersionId == oldVersion.Id);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(item.Id, chunk.FileItemId);
            Assert.Equal(item.CurrentVersionId, chunk.VersionId);
            Assert.NotNull(chunk.QdrantPointId);
        });
        Assert.Equal(chunks.Count, vectorStore.UpsertedVectors.Count);
        Assert.All(vectorStore.UpsertedVectors, vector =>
        {
            Assert.Equal(UserId, vector.UserId);
            Assert.Equal(item.ProviderId, vector.ProviderId);
            Assert.Equal(item.Id, vector.FileItemId);
            Assert.Equal(item.CurrentVersionId, vector.VersionId);
            Assert.Equal(item.Path, vector.Path);
            Assert.Equal(item.MimeType, vector.MimeType);
            Assert.Equal(item.ModifiedAt, vector.ModifiedAt);
        });
    }

    [Fact]
    public async Task IndexCurrentVersionAsync_WhenNoCurrentVersionThrowsNotFound()
    {
        await using var db = CreateDb();
        var item = await SeedFileWithoutVersionAsync(db);
        var service = CreateService(db, new FakeFileProviderAdapter(), new FakeFileVectorStore(), extractedText: "text");

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.IndexCurrentVersionAsync(item.Id));

        Assert.Equal(5304, ex.ErrorCode);
    }

    [Fact]
    public async Task SearchAsync_ReturnsKeywordAndSemanticResultsForCurrentUser()
    {
        await using var db = CreateDb();
        var item = await SeedFileWithCurrentVersionAsync(db, path: "/Reports/budget.txt", name: "budget.txt");
        var chunk = new FileChunkEntity
        {
            FileItemId = item.Id,
            VersionId = item.CurrentVersionId!.Value,
            ChunkIndex = 0,
            Text = "budget forecast details",
            TextHash = "hash",
            StartOffset = 0,
            EndOffset = 23
        };
        db.Set<FileChunkEntity>().Add(chunk);
        await db.SaveChangesAsync();
        var vectorStore = new FakeFileVectorStore();
        vectorStore.SearchHits =
        [
            new FileChunkSearchHit(chunk.Id, item.Id, item.CurrentVersionId.Value, 0.75m)
        ];
        var service = CreateService(db, new FakeFileProviderAdapter(), vectorStore, extractedText: "text");

        var result = await service.SearchAsync(new Pim.Module.Files.DTOs.FileSearchQuery("budget", "hybrid"));

        Assert.Single(result.Items);
        Assert.Single(result.Chunks);
        Assert.Equal(chunk.Id, result.Chunks[0].ChunkId);
        Assert.Equal(UserId, vectorStore.LastSearchUserId);
        Assert.Equal("hybrid", vectorStore.LastSearchMode);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-indexing-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileIndexingService CreateService(
        PimDbContext db,
        FakeFileProviderAdapter adapter,
        FakeFileVectorStore vectorStore,
        string extractedText)
    {
        var currentUser = new FixedCurrentUserService(UserId);
        var bindings = new FileProviderBindingService(db, currentUser, new FakeSecretProtector(), adapter);
        var operations = new FileOperationService(db, currentUser, new AuditLogService(db), bindings, adapter);
        return new FileIndexingService(
            db,
            currentUser,
            operations,
            new FakeTextExtractionService(extractedText),
            new FakeFileEmbeddingService(),
            vectorStore);
    }

    private static async Task<FileItemEntity> SeedFileWithCurrentVersionAsync(
        PimDbContext db,
        string mimeType = "text/plain",
        string path = "/Reports/report.txt",
        string name = "report.txt")
    {
        var item = await SeedFileWithoutVersionAsync(db, mimeType, path, name);
        var version = await SeedVersionAsync(db, item, "current:etag-1", "etag-1", isCurrent: true);
        item.CurrentVersionId = version.Id;
        await db.SaveChangesAsync();
        return item;
    }

    private static async Task<FileItemEntity> SeedFileWithoutVersionAsync(
        PimDbContext db,
        string mimeType = "text/plain",
        string path = "/Reports/report.txt",
        string name = "report.txt")
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new FileProviderEntity
        {
            UserId = UserId,
            BaseUrl = "https://cloud.example.test",
            InternalBaseUrl = "http://nextcloud",
            Username = "alice",
            AppPasswordSecret = "protected:app-password",
            Status = "connected",
            CreatedAt = now,
            UpdatedAt = now
        };
        var item = new FileItemEntity
        {
            Provider = provider,
            ExternalFileId = "file-1",
            Path = path,
            Name = name,
            ItemType = "file",
            MimeType = mimeType,
            Size = 100,
            Etag = "etag-1",
            Permissions = "RGDNVW",
            LastSeenAt = now,
            CreatedAt = now,
            ModifiedAt = now,
            SyncedAt = now
        };

        db.Set<FileProviderEntity>().Add(provider);
        db.Set<FileItemEntity>().Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private static async Task<FileVersionEntity> SeedVersionAsync(
        PimDbContext db,
        FileItemEntity item,
        string externalVersionId,
        string? etag,
        bool isCurrent)
    {
        var version = new FileVersionEntity
        {
            FileItemId = item.Id,
            ExternalVersionId = externalVersionId,
            Etag = etag,
            Size = 100,
            Source = isCurrent ? "current" : "history",
            IsCurrent = isCurrent,
            ModifiedAt = item.ModifiedAt,
            SyncedAt = DateTimeOffset.UtcNow
        };
        db.Set<FileVersionEntity>().Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string value) => $"protected:{value}";

        public string Unprotect(string protectedValue)
            => protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue["protected:".Length..]
                : protectedValue;
    }

    private sealed class FakeTextExtractionService(string text) : IFileTextExtractionService
    {
        public Task<string> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken ct = default)
            => Task.FromResult(text);
    }

    private sealed class FakeFileEmbeddingService : IFileEmbeddingService
    {
        public int Dimensions => 3;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new[] { 1f, 0f, 0f });
    }

    private sealed class FakeFileVectorStore : IFileVectorStore
    {
        public List<FileChunkVector> UpsertedVectors { get; } = [];
        public IReadOnlyList<FileChunkSearchHit> SearchHits { get; set; } = [];
        public int EnsureCollectionCallCount { get; private set; }
        public int DeleteFileVectorsCallCount { get; private set; }
        public Guid? LastDeletedFileItemId { get; private set; }
        public Guid? LastSearchUserId { get; private set; }
        public string? LastSearchMode { get; private set; }

        public Task EnsureCollectionAsync(CancellationToken ct = default)
        {
            EnsureCollectionCallCount++;
            return Task.CompletedTask;
        }

        public Task UpsertChunksAsync(IReadOnlyList<FileChunkVector> vectors, CancellationToken ct = default)
        {
            UpsertedVectors.AddRange(vectors);
            return Task.CompletedTask;
        }

        public Task DeleteFileVectorsAsync(Guid fileItemId, CancellationToken ct = default)
        {
            DeleteFileVectorsCallCount++;
            LastDeletedFileItemId = fileItemId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FileChunkSearchHit>> SearchAsync(
            float[] vector,
            Guid userId,
            string? mode,
            CancellationToken ct = default)
        {
            LastSearchUserId = userId;
            LastSearchMode = mode;
            return Task.FromResult(SearchHits);
        }
    }

    private sealed class FakeFileProviderAdapter : IFileProviderAdapter
    {
        public ProviderDownload? DownloadResult { get; set; }
        public int DownloadCallCount { get; private set; }

        public Task<FileProviderTestResult> TestConnectionAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
            => Task.FromResult(new FileProviderTestResult(true, "connected", null));

        public Task<IReadOnlyList<ProviderFileItem>> ListFolderAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderFileItem>>([]);

        public Task<ProviderFileItem> GetMetadataAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderFileItem> UploadAsync(
            FileProviderConnection connection,
            string destinationPath,
            Stream content,
            string contentType,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderDownload> DownloadAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
        {
            DownloadCallCount++;
            return Task.FromResult(DownloadResult ?? new ProviderDownload(new MemoryStream(), "text/plain", "download.txt"));
        }

        public Task<ProviderFileItem> MoveAsync(
            FileProviderConnection connection,
            string sourcePath,
            string destinationPath,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderFileItem> RenameAsync(
            FileProviderConnection connection,
            string sourcePath,
            string name,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteToTrashAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderTrashItem>> ListTrashAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderTrashItem>>([]);

        public Task RestoreTrashAsync(
            FileProviderConnection connection,
            string trashId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderFileVersion>> ListVersionsAsync(
            FileProviderConnection connection,
            string externalFileId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderFileVersion>>([]);

        public Task<ProviderDownload> DownloadVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            string fileName,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RestoreVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public ProviderOpenLink BuildOpenLink(
            FileProviderConnection connection,
            string path,
            string mode,
            string? externalFileId = null)
            => throw new NotSupportedException();
    }
}
