using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

/// <summary>
/// 文件模块的**元数据**读取服务（文件模块 v2）。
///
/// v2 事实源是 OneDrive，内容与写操作走 Graph（见 <see cref="OneDriveWriteService"/>、
/// <see cref="OneDriveContentService"/>）。本服务因此只保留与 provider 无关的元数据能力：
/// 列表 / 单条 / 整理建议；一切 WebDAV（Nextcloud）适配器能力随 P4 退役。
/// </summary>
public sealed class FileOperationService(
    PimDbContext db,
    ICurrentUserService currentUser,
    IAuditLogService auditLog)
{
    private const string ResourceType = "file";
    private const string AuditSource = "files";

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "未登录");

    public async Task<PagedResult<FileItemDto>> ListItemsAsync(
        FileListQuery query,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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

        var allItems = candidates
            .Where(item => IsDirectChildPath(item.Path, parentPath))
            .OrderBy(item => item.ItemType == "folder" ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Select(FileItemMapper.Map)
            .ToList();

        var totalCount = allItems.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        var items = allItems
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResult<FileItemDto>(
            items,
            page,
            pageSize,
            totalCount,
            totalPages);
    }

    public async Task<FileItemDto> GetItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await LoadItemAsync(id, ct);
        return FileItemMapper.Map(item);
    }

    /// <summary>
    /// 按 id 取已落库项的 DTO（不做用户校验，调用方已在自己的写服务里校验归属）。
    /// 供写端点写完 Graph、本地收敛后回读最新状态。
    /// </summary>
    internal static async Task<FileItemDto> GetItemDtoAsync(PimDbContext db, Guid id, CancellationToken ct)
    {
        var item = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(row => row.IndexJobs)
            .FirstOrDefaultAsync(row => row.Id == id, ct)
            ?? throw new DomainException(5300, "文件不存在");
        return FileItemMapper.Map(item);
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
                && item.Provider.UserId == userId
                && !item.IsDeleted,
                ct)
            ?? throw new DomainException(5300, "文件不存在");
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
            ?? throw new DomainException(5305, "文件建议不存在");
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

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = $"/{normalized}";

        normalized = normalized.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

}
