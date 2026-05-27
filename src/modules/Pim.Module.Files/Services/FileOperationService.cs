using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

public sealed class FileOperationService(
    PimDbContext db,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    FileProviderBindingService providerBindings,
    IFileProviderAdapter adapter)
{
    private const string ResourceType = "file";
    private const string AuditSource = "files";

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "Not authenticated");

    public async Task<PagedResult<FileItemDto>> ListItemsAsync(
        FileListQuery query,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var parentPath = NormalizePath(query.Path);
        var candidatePrefix = parentPath == "/" ? "/" : $"{parentPath}/";

        var candidates = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(item => item.IndexJobs)
            .Where(item =>
                item.Provider != null
                && item.Provider.UserId == userId
                && !item.IsDeleted
                && item.Path.StartsWith(candidatePrefix))
            .ToListAsync(ct);

        var items = candidates
            .Where(item => IsDirectChildPath(item.Path, parentPath))
            .OrderBy(item => item.ItemType == "folder" ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Select(MapFileItem)
            .ToList();

        return new PagedResult<FileItemDto>(
            items,
            1,
            items.Count,
            items.Count,
            items.Count == 0 ? 0 : 1);
    }

    public async Task<FileItemDto> GetItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        return MapFileItem(item);
    }

    public async Task<FileItemDto> MoveAsync(
        Guid id,
        MoveFileRequest request,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var oldPath = NormalizePath(item.Path);
        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);
        var destinationPath = NormalizePath(request.DestinationPath);
        if (destinationPath == "/")
            throw new DomainException(5301, "Destination path must include a file or folder name");

        var providerItem = await adapter.MoveAsync(connection, NormalizePath(item.Path), destinationPath, ct);
        var now = DateTimeOffset.UtcNow;
        ApplyProviderItem(item, providerItem, now, preserveExternalFileId: true);
        await UpdateDescendantPathsAsync(item, oldPath, NormalizePath(item.Path), now, ct);
        if (item.ItemType == "file")
            await UpsertCurrentVersionAsync(item, providerItem, now, ct);

        await db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.move", item.Id, ct);

        return MapFileItem(item);
    }

    public async Task<FileItemDto> RenameAsync(
        Guid id,
        RenameFileRequest request,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var oldPath = NormalizePath(item.Path);
        var name = NormalizeRenameName(request.Name);
        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);

        var providerItem = await adapter.RenameAsync(connection, NormalizePath(item.Path), name, ct);
        var now = DateTimeOffset.UtcNow;
        ApplyProviderItem(item, providerItem, now, preserveExternalFileId: true);
        await UpdateDescendantPathsAsync(item, oldPath, NormalizePath(item.Path), now, ct);
        if (item.ItemType == "file")
            await UpsertCurrentVersionAsync(item, providerItem, now, ct);

        await db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.rename", item.Id, ct);

        return MapFileItem(item);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);

        await adapter.DeleteToTrashAsync(connection, NormalizePath(item.Path), ct);

        var now = DateTimeOffset.UtcNow;
        item.IsDeleted = true;
        item.DeletedAt = now;
        item.SyncedAt = now;
        await MarkDescendantsDeletedAsync(item, now, ct);

        await db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.delete_to_trash", item.Id, ct);
    }

    public async Task<FileItemDto> UploadAsync(
        Guid providerId,
        string destinationPath,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var normalizedDestinationPath = NormalizePath(destinationPath);
        if (!PathHasFileName(normalizedDestinationPath))
            throw new DomainException(5301, "Destination path must include a file or folder name");

        var connection = await providerBindings.GetConnectionAsync(providerId, ct);
        var provider = await db.Set<FileProviderEntity>()
            .FirstOrDefaultAsync(entity => entity.Id == providerId && entity.UserId == userId, ct)
            ?? throw new DomainException(5104, "File provider not found");

        var providerItem = await adapter.UploadAsync(connection, normalizedDestinationPath, content, contentType, ct);
        var now = DateTimeOffset.UtcNow;
        var item = await db.Set<FileItemEntity>()
            .Include(candidate => candidate.Versions)
            .Include(candidate => candidate.IndexJobs)
            .FirstOrDefaultAsync(candidate =>
                candidate.ProviderId == provider.Id
                && candidate.ExternalFileId == providerItem.ExternalFileId,
                ct);
        var isNewItem = item is null;

        if (item is null)
        {
            item = new FileItemEntity
            {
                ProviderId = provider.Id,
                ExternalFileId = providerItem.ExternalFileId,
                CreatedAt = now
            };
            db.Set<FileItemEntity>().Add(item);
        }

        ApplyProviderItem(item, providerItem, now, preserveExternalFileId: false);
        if (item.ItemType == "file")
        {
            if (isNewItem)
                await db.SaveChangesAsync(ct);

            await UpsertCurrentVersionAsync(item, providerItem, now, ct);
        }

        provider.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.upload", item.Id, ct);

        return MapFileItem(item);
    }

    public async Task<ProviderDownload> DownloadAsync(Guid id, CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        if (item.ItemType == "folder")
            throw new DomainException(5303, "Folders cannot be downloaded through this endpoint");

        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);
        return await adapter.DownloadAsync(connection, NormalizePath(item.Path), ct);
    }

    public async Task<IReadOnlyList<ProviderTrashItem>> ListTrashAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var providerIds = await db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(provider => provider.UserId == userId)
            .OrderBy(provider => provider.CreatedAt)
            .ThenBy(provider => provider.Id)
            .Select(provider => provider.Id)
            .ToListAsync(ct);
        var trashItems = new List<ProviderTrashItem>();

        foreach (var providerId in providerIds)
        {
            var connection = await providerBindings.GetConnectionAsync(providerId, ct);
            trashItems.AddRange(await adapter.ListTrashAsync(connection, ct));
        }

        return trashItems;
    }

    public async Task RestoreTrashAsync(Guid providerId, string trashId, CancellationToken ct = default)
    {
        var connection = await providerBindings.GetConnectionAsync(providerId, ct);

        await adapter.RestoreTrashAsync(connection, trashId, ct);
        await RecordAuditAsync("files.trash_restore", "file_provider", providerId, ct);
    }

    public async Task<IReadOnlyList<FileItemDto>> SyncProviderAsync(
        Guid providerId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var connection = await providerBindings.GetConnectionAsync(providerId, ct);
        var provider = await db.Set<FileProviderEntity>()
            .FirstOrDefaultAsync(entity => entity.Id == providerId && entity.UserId == userId, ct)
            ?? throw new DomainException(5104, "File provider not found");
        var providerItems = await adapter.ListFolderAsync(connection, "/", ct);
        var now = DateTimeOffset.UtcNow;

        var existingItems = await db.Set<FileItemEntity>()
            .Include(item => item.Versions)
            .Where(item => item.ProviderId == providerId)
            .ToListAsync(ct);
        var existingByExternalId = existingItems.ToDictionary(
            item => item.ExternalFileId,
            StringComparer.Ordinal);
        var seenExternalIds = new HashSet<string>(StringComparer.Ordinal);
        var syncedItems = new List<FileItemEntity>();

        foreach (var providerItem in providerItems)
        {
            if (!seenExternalIds.Add(providerItem.ExternalFileId))
                continue;

            if (!existingByExternalId.TryGetValue(providerItem.ExternalFileId, out var item))
            {
                item = new FileItemEntity
                {
                    ProviderId = providerId,
                    ExternalFileId = providerItem.ExternalFileId,
                    CreatedAt = now
                };
                db.Set<FileItemEntity>().Add(item);
                existingByExternalId[item.ExternalFileId] = item;
            }

            ApplyProviderItem(item, providerItem, now, preserveExternalFileId: false);
            syncedItems.Add(item);
        }

        foreach (var missingItem in existingItems.Where(item =>
            IsDirectChildPath(item.Path, "/")
            && !seenExternalIds.Contains(item.ExternalFileId)))
        {
            if (!missingItem.IsDeleted)
            {
                missingItem.IsDeleted = true;
                missingItem.DeletedAt = now;
            }

            missingItem.SyncedAt = now;
        }

        provider.LastSyncAt = now;
        provider.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        foreach (var syncedItem in syncedItems.Where(item => item.ItemType == "file"))
        {
            var providerItem = providerItems.First(source => source.ExternalFileId == syncedItem.ExternalFileId);
            await UpsertCurrentVersionAsync(syncedItem, providerItem, now, ct);
        }

        await db.SaveChangesAsync(ct);

        return syncedItems
            .OrderBy(item => item.ItemType == "folder" ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Select(MapFileItem)
            .ToList();
    }

    public async Task<IReadOnlyList<FileVersionDto>> ListVersionsAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);
        var providerVersions = await adapter.ListVersionsAsync(connection, item.ExternalFileId, ct);
        var now = DateTimeOffset.UtcNow;

        var existingVersions = await db.Set<FileVersionEntity>()
            .Where(version => version.FileItemId == item.Id)
            .ToListAsync(ct);
        var existingByExternalId = existingVersions.ToDictionary(
            version => version.ExternalVersionId,
            StringComparer.Ordinal);

        foreach (var providerVersion in providerVersions)
        {
            if (!existingByExternalId.TryGetValue(providerVersion.ExternalVersionId, out var version))
            {
                version = new FileVersionEntity
                {
                    FileItemId = item.Id,
                    ExternalVersionId = providerVersion.ExternalVersionId
                };
                db.Set<FileVersionEntity>().Add(version);
                existingVersions.Add(version);
                existingByExternalId[version.ExternalVersionId] = version;
            }

            version.Etag = providerVersion.Etag;
            version.Size = providerVersion.Size;
            version.ModifiedAt = providerVersion.ModifiedAt;
            version.Source = NormalizeVersionSource(providerVersion.Source);
            version.IsCurrent = providerVersion.IsCurrent;
            version.SyncedAt = now;

            if (version.IsCurrent)
            {
                foreach (var otherCurrent in existingVersions.Where(existing => existing.Id != version.Id))
                    otherCurrent.IsCurrent = false;

                item.CurrentVersionId = version.Id;
            }
        }

        await db.SaveChangesAsync(ct);

        return await db.Set<FileVersionEntity>()
            .AsNoTracking()
            .Where(version => version.FileItemId == item.Id)
            .OrderByDescending(version => version.ExternalVersionId)
            .Select(version => MapVersion(version))
            .ToListAsync(ct);
    }

    public async Task<ProviderDownload> DownloadVersionAsync(
        Guid id,
        Guid versionId,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var version = await LoadVersionAsync(item.Id, versionId, ct);
        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);

        return await adapter.DownloadVersionAsync(
            connection,
            item.ExternalFileId,
            version.ExternalVersionId,
            item.Name,
            ct);
    }

    public async Task RestoreVersionAsync(
        Guid id,
        Guid versionId,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var version = await LoadVersionAsync(item.Id, versionId, ct);
        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);

        await adapter.RestoreVersionAsync(connection, item.ExternalFileId, version.ExternalVersionId, ct);

        var versions = await db.Set<FileVersionEntity>()
            .Where(candidate => candidate.FileItemId == item.Id)
            .ToListAsync(ct);
        var currentRowsToUnset = versions
            .Where(candidate => candidate.Id != version.Id && candidate.IsCurrent)
            .ToList();
        foreach (var currentVersion in currentRowsToUnset)
            currentVersion.IsCurrent = false;

        if (currentRowsToUnset.Count > 0 && !version.IsCurrent)
            await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        version.IsCurrent = true;
        version.SyncedAt = now;
        item.CurrentVersionId = version.Id;
        item.Etag = version.Etag;
        item.Size = version.Size;
        item.ModifiedAt = version.ModifiedAt;
        item.SyncedAt = now;

        await db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.version_restore", item.Id, ct);
    }

    public async Task<VersionRestorePreviewDto> RestoreVersionPreviewAsync(
        Guid id,
        Guid versionId,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var version = await db.Set<FileVersionEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == versionId && candidate.FileItemId == item.Id, ct)
            ?? throw new DomainException(5304, "File version not found");
        var currentVersion = item.CurrentVersionId is null
            ? null
            : await db.Set<FileVersionEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == item.CurrentVersionId && candidate.FileItemId == item.Id, ct);

        return new VersionRestorePreviewDto(
            item.Id,
            version.Id,
            FormatVersionLabel(currentVersion),
            FormatVersionLabel(version),
            RequiresConfirmation: true,
            $"Restoring {FormatVersionLabel(version)} will replace the current contents of {item.Name}.");
    }

    public async Task<FileOpenLinkDto> BuildOpenLinkAsync(
        Guid id,
        string? mode,
        CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        var connection = await providerBindings.GetConnectionAsync(item.ProviderId, ct);
        var link = adapter.BuildOpenLink(connection, NormalizePath(item.Path), mode ?? "view", item.ExternalFileId);

        return new FileOpenLinkDto(link.Url, link.Mode);
    }

    public async Task<IReadOnlyList<FileSuggestionDto>> ListSuggestionsAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        return await db.Set<FileSuggestionEntity>()
            .AsNoTracking()
            .Where(suggestion =>
                suggestion.FileItem != null
                && suggestion.FileItem.Provider != null
                && suggestion.FileItem.Provider.UserId == userId)
            .OrderByDescending(suggestion => suggestion.UpdatedAt)
            .ThenByDescending(suggestion => suggestion.CreatedAt)
            .Select(suggestion => MapSuggestion(suggestion))
            .ToListAsync(ct);
    }

    public async Task<FileSuggestionDto> DismissSuggestionAsync(Guid id, CancellationToken ct = default)
    {
        var suggestion = await LoadSuggestionAsync(id, ct);
        suggestion.Status = "dismissed";
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.suggestion_dismiss", suggestion.FileItemId, ct);

        return MapSuggestion(suggestion);
    }

    public async Task<FileSuggestionDto> AcceptSuggestionAsync(Guid id, CancellationToken ct = default)
    {
        var suggestion = await LoadSuggestionAsync(id, ct);
        suggestion.Status = "accepted";
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.suggestion_accept", suggestion.FileItemId, ct);

        return MapSuggestion(suggestion);
    }

    private async Task<FileItemEntity> LoadItemAsync(Guid id, CancellationToken ct)
    {
        var userId = UserId;
        return await db.Set<FileItemEntity>()
            .Include(item => item.Provider)
            .Include(item => item.IndexJobs)
            .FirstOrDefaultAsync(item =>
                item.Id == id
                && item.Provider != null
                && item.Provider.UserId == userId,
                ct)
            ?? throw new DomainException(5300, "File item not found");
    }

    private async Task<FileSuggestionEntity> LoadSuggestionAsync(Guid id, CancellationToken ct)
    {
        var userId = UserId;
        return await db.Set<FileSuggestionEntity>()
            .Include(suggestion => suggestion.FileItem)
            .ThenInclude(item => item!.Provider)
            .FirstOrDefaultAsync(suggestion =>
                suggestion.Id == id
                && suggestion.FileItem != null
                && suggestion.FileItem.Provider != null
                && suggestion.FileItem.Provider.UserId == userId,
                ct)
            ?? throw new DomainException(5305, "File suggestion not found");
    }

    private async Task<FileVersionEntity> LoadVersionAsync(Guid fileItemId, Guid versionId, CancellationToken ct)
        => await db.Set<FileVersionEntity>()
            .FirstOrDefaultAsync(version => version.Id == versionId && version.FileItemId == fileItemId, ct)
            ?? throw new DomainException(5304, "File version not found");

    private static void ApplyProviderItem(
        FileItemEntity item,
        ProviderFileItem providerItem,
        DateTimeOffset now,
        bool preserveExternalFileId)
    {
        if (!preserveExternalFileId)
            item.ExternalFileId = providerItem.ExternalFileId;

        item.ParentExternalFileId = providerItem.ParentExternalFileId;
        item.Path = NormalizePath(providerItem.Path);
        item.Name = NormalizeDisplayName(providerItem.Name, item.Path);
        item.ItemType = string.IsNullOrWhiteSpace(providerItem.ItemType) ? "file" : providerItem.ItemType.Trim();
        item.MimeType = providerItem.MimeType;
        item.Size = providerItem.Size;
        item.Etag = providerItem.Etag;
        item.Permissions = providerItem.Permissions;
        item.IsDeleted = false;
        item.DeletedAt = null;
        item.LastSeenAt = now;
        item.ModifiedAt = providerItem.ModifiedAt;
        item.SyncedAt = now;
    }

    private async Task UpsertCurrentVersionAsync(
        FileItemEntity item,
        ProviderFileItem providerItem,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var currentExternalVersionId = BuildCurrentVersionExternalId(item, providerItem);
        var versions = await db.Set<FileVersionEntity>()
            .Where(version => version.FileItemId == item.Id)
            .ToListAsync(ct);
        var currentVersion = versions.FirstOrDefault(version => version.ExternalVersionId == currentExternalVersionId);

        if (currentVersion is null)
        {
            currentVersion = new FileVersionEntity
            {
                FileItemId = item.Id,
                ExternalVersionId = currentExternalVersionId
            };
            db.Set<FileVersionEntity>().Add(currentVersion);
            versions.Add(currentVersion);
        }

        var currentRowsToUnset = versions
            .Where(version => version.Id != currentVersion.Id && version.IsCurrent)
            .ToList();
        foreach (var version in currentRowsToUnset)
            version.IsCurrent = false;

        if (currentRowsToUnset.Count > 0 && !currentVersion.IsCurrent)
            await db.SaveChangesAsync(ct);

        currentVersion.ExternalVersionId = currentExternalVersionId;
        currentVersion.Etag = providerItem.Etag;
        currentVersion.Size = providerItem.Size;
        currentVersion.ModifiedAt = providerItem.ModifiedAt;
        currentVersion.Source = "current";
        currentVersion.IsCurrent = true;
        currentVersion.SyncedAt = now;
        item.CurrentVersionId = currentVersion.Id;
    }

    private async Task UpdateDescendantPathsAsync(
        FileItemEntity item,
        string oldPath,
        string newPath,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (item.ItemType != "folder" || string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return;

        var oldPrefix = $"{oldPath}/";
        var newPrefix = $"{newPath}/";
        var candidates = await db.Set<FileItemEntity>()
            .Where(descendant =>
                descendant.ProviderId == item.ProviderId
                && descendant.Id != item.Id)
            .ToListAsync(ct);

        foreach (var descendant in candidates.Where(descendant =>
            descendant.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            descendant.Path = $"{newPrefix}{descendant.Path[oldPrefix.Length..]}";
            descendant.SyncedAt = now;
        }
    }

    private async Task MarkDescendantsDeletedAsync(
        FileItemEntity item,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (item.ItemType != "folder")
            return;

        var pathPrefix = $"{NormalizePath(item.Path)}/";
        var candidates = await db.Set<FileItemEntity>()
            .Where(descendant =>
                descendant.ProviderId == item.ProviderId
                && descendant.Id != item.Id)
            .ToListAsync(ct);

        foreach (var descendant in candidates.Where(descendant =>
            descendant.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            descendant.IsDeleted = true;
            descendant.DeletedAt = now;
            descendant.SyncedAt = now;
        }
    }

    private async Task RecordAuditAsync(string action, Guid fileId, CancellationToken ct)
        => await RecordAuditAsync(action, ResourceType, fileId, ct);

    private async Task RecordAuditAsync(string action, string resourceType, Guid resourceId, CancellationToken ct)
    {
        await auditLog.RecordAsync(new CreateAuditLogRequest(
            UserId,
            AuditActorType.User,
            action,
            resourceType,
            resourceId.ToString(),
            AuditSource,
            AuditResult.Success,
            null,
            null,
            null,
            null,
            null,
            null), ct);
    }

    private static FileItemDto MapFileItem(FileItemEntity item)
        => new(
            item.Id,
            item.ProviderId,
            item.ExternalFileId,
            item.ParentExternalFileId,
            NormalizePath(item.Path),
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

    private static FileVersionDto MapVersion(FileVersionEntity version)
        => new(
            version.Id,
            version.FileItemId,
            version.ExternalVersionId,
            version.Etag,
            version.Size,
            version.ModifiedAt,
            version.Source,
            version.IsCurrent,
            version.SyncedAt);

    private static FileSuggestionDto MapSuggestion(FileSuggestionEntity suggestion)
        => new(
            suggestion.Id,
            suggestion.FileItemId,
            suggestion.SuggestionType,
            suggestion.Title,
            suggestion.Reason,
            suggestion.Confidence,
            suggestion.PayloadJson,
            suggestion.Status,
            suggestion.AiRequestLogId,
            suggestion.CreatedAt,
            suggestion.UpdatedAt);

    private static bool IsDirectChildPath(string itemPath, string parentPath)
    {
        var normalizedPath = NormalizePath(itemPath);
        if (normalizedPath == parentPath)
            return false;

        if (parentPath == "/")
        {
            var rootRelativePath = normalizedPath.Trim('/');
            return rootRelativePath.Length > 0 && !rootRelativePath.Contains('/', StringComparison.Ordinal);
        }

        var prefix = $"{parentPath}/";
        if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var relativePath = normalizedPath[prefix.Length..];
        return relativePath.Length > 0 && !relativePath.Contains('/', StringComparison.Ordinal);
    }

    private static bool PathHasFileName(string path)
        => NormalizePath(path).Trim('/').Split('/').LastOrDefault() is { Length: > 0 };

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = $"/{normalized}";

        normalized = normalized.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static string NormalizeDisplayName(string? name, string path)
    {
        var normalized = name?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var lastSegment = NormalizePath(path).Trim('/').Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(lastSegment) ? "/" : lastSegment;
    }

    private static string NormalizeRenameName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized is "." or ".."
            || normalized.Contains('/', StringComparison.Ordinal)
            || normalized.Contains('\\', StringComparison.Ordinal))
        {
            throw new DomainException(5302, "Rename target is not a safe file name");
        }

        return normalized;
    }

    private static string NormalizeVersionSource(string source)
        => string.IsNullOrWhiteSpace(source) || source is "nextcloud" ? "history" : source.Trim();

    private static string BuildCurrentVersionExternalId(FileItemEntity item, ProviderFileItem providerItem)
        => $"current:{(string.IsNullOrWhiteSpace(providerItem.Etag) ? item.ExternalFileId : providerItem.Etag)}";

    private static string LatestIndexStatus(FileItemEntity item)
        => item.IndexJobs
            .OrderByDescending(job => job.FinishedAt ?? job.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(job => job.Id)
            .FirstOrDefault()
            ?.Status
            ?? "not_indexed";

    private static string FormatVersionLabel(FileVersionEntity? version)
    {
        if (version is null)
            return "no current version";

        var identity = string.IsNullOrWhiteSpace(version.Etag)
            ? version.ExternalVersionId
            : version.Etag;
        return $"{identity} ({version.ModifiedAt:O})";
    }
}
