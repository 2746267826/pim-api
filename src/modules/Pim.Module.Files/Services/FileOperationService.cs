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

    /// <summary>列表默认页大小（工单 P3：100/页）。</summary>
    public const int DefaultPageSize = 100;

    /// <summary>列表分页上限（工单 P3：100/页）。</summary>
    internal const int MaxPageSize = 100;

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "未登录");

    /// <summary>
    /// 目录列表（REQ-2/3/8/9）：只返回 <paramref name="query"/> 指定目录的**直属子项**，
    /// 过滤、排序、计数与分页全部在数据库侧完成。
    ///
    /// 旧实现把目标路径的整棵子树 <c>ToListAsync</c> 进内存再筛直属子项：根目录在 12 万项的
    /// 树上要物化 12 万个带 Include 的实体（实测 3044ms），且时间随全树规模线性增长。
    /// 这里改成纯 SQL 谓词 + LIMIT/OFFSET，物化量恒为**一页**。
    /// </summary>
    public async Task<PagedResult<FileItemDto>> ListItemsAsync(
        FileListQuery query,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var userId = UserId;
        var parentPath = NormalizePath(query.Path);

        var items = DirectChildren(db, userId, parentPath);

        // REQ-4：树只要目录；REQ-9/AC-9.2：类型过滤与排序都要在分页之前生效
        var itemType = NormalizeItemType(query.Type);
        if (itemType is not null)
        {
            items = items.Where(item => item.ItemType == itemType);
        }

        // REQ-8：当前文件夹模式的关键词过滤（名称，大小写不敏感）
        var keyword = query.Q?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            var lowered = keyword.ToLowerInvariant();
            items = items.Where(item => item.Name.ToLower().Contains(lowered));
        }

        var totalCount = await items.CountAsync(ct);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        var rows = await ApplySort(items, query.Sort, query.Order)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(item => item.IndexJobs)
            .ToListAsync(ct);

        return new PagedResult<FileItemDto>(
            rows.Select(FileItemMapper.Map).ToList(),
            page,
            pageSize,
            totalCount,
            totalPages);
    }

    /// <summary>
    /// 直属子项的数据库侧谓词（REQ-2）。
    ///
    /// 判据与原来的内存版 <c>IsDirectChildPath</c> 等价：
    /// <list type="number">
    ///   <item>路径必须以 <c>父路径 + "/"</c> 开头（根目录即 <c>"/"</c>）；</item>
    ///   <item>去掉该前缀后的剩余部分，**去掉尾部斜杠**后既不能为空、也不能再含分隔符；
    ///   这一条同时覆盖了两个历史行为：<c>/main/</c>（代表目录自身的那一行）不算自己的子项、
    ///   <c>/main/report.txt/</c>（带尾斜杠的历史行）仍要被列出。</item>
    /// </list>
    ///
    /// **性能约束（REQ-2/AC-2.3）**：<c>StartsWith</c> 必须直接作用在 <c>path</c> 列上，
    /// 否则迁移里那条 <c>text_pattern_ops</c> 索引失效、退化成整表扫描。因此这里刻意
    /// **不做** <c>rtrim(path)</c> 这类「包住被索引列」的写法——规范化只作用在
    /// <c>Substring</c> 的**结果**上（它只是过滤条件，与索引无关）。
    /// 实测：包住列时为 Parallel Seq Scan，写成现在这样在选择性前缀上为
    /// Bitmap Index Scan on ix_file_items_provider_id_path_pattern。
    ///
    /// **已知取舍**：<c>StartsWith</c> 是大小写敏感的，而旧内存实现用 <c>OrdinalIgnoreCase</c>。
    /// 大小写折叠会让 <c>/Main</c> 与 <c>/main</c> 这两个在 OneDrive 上真实存在的不同目录混为一谈，
    /// 因此**不折叠**——这与 OneDrive 事实源一致，并已固化为契约用例
    /// （<c>ListItemsAsync_DoesNotFoldPathCaseBetweenSiblingDirectories</c>）。
    /// 同理不做反斜杠/首尾空白归一化：一条路径里出现 <c>\</c> 或首尾空白，
    /// 说明事实源数据异常，应当被记录而不是被静默吞掉。
    ///
    /// 注：大前缀（如 <c>/main/</c>，命中子树全部行）规划器可能选择 Seq Scan——这是
    /// 「结果集本就很大」的正常选择，不是索引失效；选择性前缀仍走索引。
    /// </summary>
    internal static IQueryable<FileItemEntity> DirectChildren(
        PimDbContext db,
        Guid userId,
        string parentPath)
    {
        var prefix = parentPath == "/" ? "/" : $"{parentPath}/";

        return db.Set<FileItemEntity>()
            .AsNoTracking()
            .Where(item =>
                item.Provider != null
                && item.Provider.UserId == userId
                && !item.IsDeleted
                // 前缀判据必须作用在**裸列**上（可用 text_pattern_ops 索引）
                && item.Path.StartsWith(prefix)
                // 注意：下面两条必须**内联**写在这里。抽成静态辅助方法后 EF 无法翻译，
                // 会在真库上抛 "The LINQ expression ... could not be translated"（真库用例守住）。
                && item.Path.Substring(prefix.Length).TrimEnd('/').Length > 0
                && !item.Path.Substring(prefix.Length).TrimEnd('/').Contains("/"));
    }

    /// <summary>
    /// 排序（REQ-9）：文件夹恒在前（Windows 资源管理器口径，也是 AC-2.2 的默认约定），
    /// 再按排序键；末位恒以 <c>Id</c> 兜底，保证翻页时是一个**全序**，不重不漏（AC-9.2）。
    /// 未知排序键/方向回落到默认（名称升序），不抛错。
    /// </summary>
    internal static IOrderedQueryable<FileItemEntity> ApplySort(
        IQueryable<FileItemEntity> source,
        string? sort,
        string? order)
    {
        var descending = string.Equals(order?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);
        var foldersFirst = source.OrderBy(item => item.ItemType == "folder" ? 0 : 1);

        return (sort?.Trim().ToLowerInvariant()) switch
        {
            "modified" when descending => foldersFirst.ThenByDescending(i => i.ModifiedAt).ThenBy(i => i.Id),
            "modified" => foldersFirst.ThenBy(i => i.ModifiedAt).ThenBy(i => i.Id),
            "size" when descending => foldersFirst.ThenByDescending(i => i.Size ?? 0).ThenBy(i => i.Id),
            "size" => foldersFirst.ThenBy(i => i.Size ?? 0).ThenBy(i => i.Id),
            "name" when descending => foldersFirst.ThenByDescending(i => i.Name.ToLower()).ThenBy(i => i.Id),
            _ => foldersFirst.ThenBy(i => i.Name.ToLower()).ThenBy(i => i.Id),
        };
    }

    /// <summary>只接受 folder/file；其余（含 null、脏值）表示不过滤。</summary>
    private static string? NormalizeItemType(string? type)
        => type?.Trim().ToLowerInvariant() switch
        {
            "folder" => "folder",
            "file" => "file",
            _ => null,
        };

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

    // 旧的 IsDirectChildPath（内存版直属子项判定）随 REQ-2 一并删除：
    // 判定已下推到 SQL（见 DirectChildren），保留一份内存实现只会让两处语义漂移。


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
