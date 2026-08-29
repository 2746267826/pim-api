using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.TextExtraction;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

public interface IFileTextExtractionService
{
    Task<string> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken ct = default);
}

public sealed class TikaFileTextExtractionService(TikaClient tikaClient) : IFileTextExtractionService
{
    public Task<string> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken ct = default)
        => tikaClient.ExtractTextAsync(fileStream, fileName, ct);
}

public sealed class FileIndexingService(
    PimDbContext db,
    ICurrentUserService currentUser,
    FileOperationService fileOperations,
    IFileTextExtractionService textExtraction,
    IFileEmbeddingService embeddings,
    IFileVectorStore vectorStore,
    IConfiguration? configuration = null,
    ILogger<FileIndexingService>? logger = null)
{
    private static readonly HashSet<string> SupportedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/markdown",
        "text/csv",
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "未登录");

    public async Task<FileIndexJobDto> IndexCurrentVersionAsync(
        Guid fileItemId,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(fileItemId, includeIndexJobs: false, ct);
        var versionId = item.CurrentVersionId;
        var job = CreateJob(item, versionId);

        db.Set<FileIndexJobEntity>().Add(job);
        await db.SaveChangesAsync(ct);

        if (item.ItemType == "folder")
            return await SkipJobAsync(job, "metadata", "文件夹不能建立索引", ct);

        var version = await LoadCurrentVersionAsync(item, ct);
        job.VersionId = version.Id;
        await db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(item.MimeType) || !SupportedMimeTypes.Contains(item.MimeType))
            return await SkipJobAsync(job, "mime_type", $"不支持的 MIME 类型：{item.MimeType ?? "未知"}", ct);

        try
        {
            // 配置降级提示：若未配置 Tika/MinIO，明确记录而非静默 Hangfire 重试
            var tikaBase = configuration?["Tika:BaseUrl"];
            if (string.IsNullOrWhiteSpace(tikaBase))
                logger?.LogWarning("文件索引降级：Tika:BaseUrl 未配置，提取可能失败 fileItemId={FileItemId}", item.Id);
            var minioEndpoint = configuration?["Minio:Endpoint"];
            if (string.IsNullOrWhiteSpace(minioEndpoint))
                logger?.LogWarning("文件索引降级：Minio:Endpoint 未配置 fileItemId={FileItemId}", item.Id);

            job.Stage = "download";
            await db.SaveChangesAsync(ct);
            var download = await fileOperations.DownloadAsync(item.Id, ct);

            job.Stage = "extract";
            await db.SaveChangesAsync(ct);
            await using var content = download.Content;
            var text = await textExtraction.ExtractTextAsync(content, download.FileName, ct);
            if (string.IsNullOrWhiteSpace(text))
                return await SkipJobAsync(job, "extract", "未提取到文本", ct);

            job.Stage = "chunk";
            await db.SaveChangesAsync(ct);
            var chunks = FileChunker.Chunk(text);

            await vectorStore.EnsureCollectionAsync(ct);
            await vectorStore.DeleteFileVectorsAsync(item.Id, ct);
            var existingChunks = await db.Set<FileChunkEntity>()
                .Where(chunk => chunk.FileItemId == item.Id)
                .ToListAsync(ct);
            db.Set<FileChunkEntity>().RemoveRange(existingChunks);

            var chunkEntities = chunks.Select(chunk => new FileChunkEntity
            {
                FileItemId = item.Id,
                VersionId = version.Id,
                ChunkIndex = chunk.ChunkIndex,
                Text = chunk.Text,
                TextHash = chunk.TextHash,
                StartOffset = chunk.StartOffset,
                EndOffset = chunk.EndOffset,
                QdrantPointId = BuildPointId(item.Id, version.Id, chunk.ChunkIndex)
            }).ToList();

            db.Set<FileChunkEntity>().AddRange(chunkEntities);
            await db.SaveChangesAsync(ct);

            job.Stage = "embed";
            await db.SaveChangesAsync(ct);
            var vectors = new List<FileChunkVector>(chunkEntities.Count);
            foreach (var chunk in chunkEntities)
            {
                vectors.Add(new FileChunkVector(
                    chunk.QdrantPointId!,
                    UserId,
                    item.ProviderId,
                    item.Id,
                    version.Id,
                    chunk.Id,
                    item.Path,
                    item.MimeType,
                    item.ModifiedAt,
                    await embeddings.EmbedAsync(chunk.Text, ct)));
            }

            job.Stage = "qdrant";
            await db.SaveChangesAsync(ct);
            await vectorStore.UpsertChunksAsync(vectors, ct);

            job.Status = "succeeded";
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return MapJob(job);
        }
        catch (Exception ex) when (ex is not DomainException)
        {
            logger?.LogError(ex, "文件索引失败 fileItemId={FileItemId} stage={Stage}", item.Id, job.Stage);
            job.Status = "failed";
            job.LastError = ex.Message;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    public async Task<FileSearchResultDto> SearchAsync(
        FileSearchQuery query,
        CancellationToken ct = default)
    {
        var search = query.Q?.Trim();
        if (string.IsNullOrWhiteSpace(search))
            return new FileSearchResultDto([], []);

        var mode = string.IsNullOrWhiteSpace(query.Mode)
            ? "hybrid"
            : query.Mode.Trim().ToLowerInvariant();
        if (mode is not ("keyword" or "semantic" or "hybrid"))
            mode = "hybrid";

        var includeKeyword = mode is "keyword" or "hybrid";
        var includeSemantic = mode is "semantic" or "hybrid";

        var items = includeKeyword
            ? await SearchItemsAsync(search, ct)
            : [];
        var chunkHits = new List<FileChunkSearchHitDto>();

        if (includeSemantic)
        {
            var vector = await embeddings.EmbedAsync(search, ct);
            var hits = await vectorStore.SearchAsync(vector, UserId, mode, ct);
            var hitScores = hits.ToDictionary(hit => hit.ChunkId, hit => hit.Score);
            var hitOrder = hits
                .Select((hit, index) => new { hit.ChunkId, Index = index })
                .ToDictionary(hit => hit.ChunkId, hit => hit.Index);
            var hitIds = hits.Select(hit => hit.ChunkId).ToHashSet();
            var chunks = await db.Set<FileChunkEntity>()
                .AsNoTracking()
                .Include(chunk => chunk.FileItem)
                .ThenInclude(item => item!.Provider)
                .Where(chunk =>
                    hitIds.Contains(chunk.Id)
                    && chunk.FileItem != null
                    && chunk.FileItem.Provider != null
                    && chunk.FileItem.Provider.UserId == UserId
                    && !chunk.FileItem.IsDeleted)
                .ToListAsync(ct);

            chunkHits.AddRange(chunks
                .OrderBy(chunk => hitOrder[chunk.Id])
                .Select(chunk => new FileChunkSearchHitDto(
                    chunk.Id,
                    chunk.FileItemId,
                    chunk.VersionId,
                    chunk.Text,
                    hitScores[chunk.Id])));
        }

        return new FileSearchResultDto(items, chunkHits);
    }

    private async Task<IReadOnlyList<FileItemDto>> SearchItemsAsync(string search, CancellationToken ct)
    {
        var lowered = search.ToLowerInvariant();
        // DB-side filtering via IQueryable to avoid full table load (OOM risk)
        var entities = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(item => item.Provider)
            .Include(item => item.IndexJobs)
            .Where(item =>
                item.Provider != null
                && item.Provider.UserId == UserId
                && !item.IsDeleted
                && (item.Name.ToLower().Contains(lowered)
                    || item.Path.ToLower().Contains(lowered)
                    || (item.MimeType != null && item.MimeType.ToLower().Contains(lowered))))
            .OrderBy(item => item.ItemType == "folder" ? 0 : 1)
            .ThenBy(item => item.Name.ToLower())
            .ThenBy(item => item.Id)
            .Take(20)
            .ToListAsync(ct);

        return entities.Select(MapFileItem).ToList();
    }

    private async Task<FileItemEntity> LoadItemAsync(
        Guid id,
        bool includeIndexJobs,
        CancellationToken ct)
    {
        var query = db.Set<FileItemEntity>()
            .Include(item => item.Provider)
            .AsQueryable();
        if (includeIndexJobs)
            query = query.Include(item => item.IndexJobs);

        return await query.FirstOrDefaultAsync(item =>
                item.Id == id
                && item.Provider != null
                && item.Provider.UserId == UserId
                && !item.IsDeleted,
                ct)
            ?? throw new DomainException(5300, "文件不存在");
    }

    private async Task<FileVersionEntity> LoadCurrentVersionAsync(
        FileItemEntity item,
        CancellationToken ct)
    {
        if (item.CurrentVersionId is null)
            throw new DomainException(5304, "文件版本不存在");

        return await db.Set<FileVersionEntity>()
            .FirstOrDefaultAsync(version =>
                version.Id == item.CurrentVersionId
                && version.FileItemId == item.Id
                && version.Source == "current"
                && version.IsCurrent,
                ct)
            ?? throw new DomainException(5304, "文件版本不存在");
    }

    private static FileIndexJobEntity CreateJob(FileItemEntity item, Guid? versionId)
    {
        var now = DateTimeOffset.UtcNow;
        return new FileIndexJobEntity
        {
            FileItemId = item.Id,
            VersionId = versionId,
            Status = "running",
            Stage = "metadata",
            AttemptCount = 1,
            StartedAt = now
        };
    }

    private async Task<FileIndexJobDto> SkipJobAsync(
        FileIndexJobEntity job,
        string stage,
        string reason,
        CancellationToken ct)
    {
        job.Status = "skipped";
        job.Stage = stage;
        job.LastError = reason;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return MapJob(job);
    }

    private static string BuildPointId(Guid fileItemId, Guid versionId, int chunkIndex)
    {
        var input = $"{fileItemId:N}:{versionId:N}:{chunkIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]).ToString();
    }

    private static FileIndexJobDto MapJob(FileIndexJobEntity job)
        => new(job.Id, job.FileItemId, job.VersionId, job.Status, job.Stage, job.AttemptCount, job.LastError);

    private static FileItemDto MapFileItem(FileItemEntity item)
        => new(
            item.Id,
            item.ProviderId,
            item.ExternalFileId,
            item.ParentExternalFileId,
            item.Path,
            item.Name,
            item.ItemType,
            item.MimeType,
            item.Size,
            item.Etag,
            item.ContentHash,
            item.CurrentVersionId,
            item.Permissions,
            item.IsDeleted,
            item.DeletedAt,
            item.LastSeenAt,
            item.CreatedAt,
            item.ModifiedAt,
            item.SyncedAt,
            LatestIndexStatus(item),
            null);

    private static string LatestIndexStatus(FileItemEntity item)
        => item.IndexJobs
            .OrderByDescending(job => job.FinishedAt ?? job.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(job => job.Id)
            .FirstOrDefault()
            ?.Status
            ?? "not_indexed";
}
