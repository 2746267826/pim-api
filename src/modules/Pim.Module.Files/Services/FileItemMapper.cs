using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

/// <summary>
/// <see cref="FileItemEntity"/> → <see cref="FileItemDto"/> 的唯一映射点。
/// 列表、单条、上传等出口都要产出一致的 DTO，集中在此避免各处漂移。
/// </summary>
public static class FileItemMapper
{
    public static FileItemDto Map(FileItemEntity item)
        => new(
            item.Id,
            item.ProviderId,
            item.ExternalFileId,
            item.ParentExternalFileId,
            FileOperationService.NormalizePath(item.Path),
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
