using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Files.Services;

/// <summary>
/// 元数据搜索（设计文档 §9，决策 D3）：只搜 <c>name</c> / <c>path</c> / <c>mime_type</c>。
///
/// v2 刻意**不做**内容索引与向量检索：内容级查找交给 Hermes 走 <c>read_file_text</c> 现取现抽。
/// 因此这里没有嵌入、没有向量库、也不需要任何外部引擎——纯 PostgreSQL ILIKE。
/// 敏感路径文件与其他内容出口保持一致，绝不进搜索结果（§13）。
/// </summary>
public sealed class FileSearchService(
    PimDbContext db,
    ICurrentUserService currentUser,
    SensitivePathPolicy? sensitivePolicy = null,
    IConfiguration? configuration = null)
{
    /// <summary>先按上限多取一些再做敏感过滤，避免敏感项吃掉结果预算。</summary>
    private const int CandidateLimit = 60;

    /// <summary>返回结果上限（单页）。</summary>
    private const int ResultLimit = 20;

    /// <summary>按 id 排序、批量取一页元数据（供翻页使用）。</summary>
    private const int PageSizeCap = 100;

    // 与其它出口一致：没有注入实例时按配置构造，而不是退回硬编码默认值，
    // 否则自定义 Files:SensitivePathPatterns 会被静默忽略。
    private readonly SensitivePathPolicy _sensitivePolicy = sensitivePolicy ?? new SensitivePathPolicy(configuration);

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "未登录");

    /// <summary>
    /// 兼容旧签名：单页搜索结果（MCP 的 search_files 与 Web 搜索框都用这一档）。
    /// </summary>
    public Task<FileSearchResultDto> SearchAsync(FileSearchQuery query, CancellationToken ct = default)
        => SearchAsync(query, page: 1, pageSize: ResultLimit, ct);

    /// <summary>
    /// 元数据搜索（设计 §9）：支持分页。
    /// 合约里 `search_files` 声明了 page/pageSize，因此这里必须真的翻页，否则
    /// page=2 会返回第一页、超过单页的结果永远取不到（复审发现）。
    /// </summary>
    public async Task<FileSearchResultDto> SearchAsync(
        FileSearchQuery query,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, PageSizeCap);

        var search = query.Q?.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return new FileSearchResultDto([], []);
        }

        var lowered = search.ToLowerInvariant();
        // 先把未登录错误抛在查询表达式**之外**：若在 Where 里取 UserId，
        // 异常会被 EF 包装成 InvalidOperationException，最终变成 500 而不是 1002。
        var userId = UserId;
        // 数据库侧过滤（IQueryable），避免全表加载导致 OOM
        var matches = db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(item => item.Provider)
            .Include(item => item.IndexJobs)
            .Where(item =>
                item.Provider != null
                && item.Provider.UserId == userId
                && !item.IsDeleted
                && (item.Name.ToLower().Contains(lowered)
                    || item.Path.ToLower().Contains(lowered)
                    || (item.MimeType != null && item.MimeType.ToLower().Contains(lowered))))
            .OrderBy(item => item.ItemType == "folder" ? 0 : 1)
            .ThenBy(item => item.Name.ToLower())
            .ThenBy(item => item.Id);

        // 敏感路径不进搜索结果（§13）。过滤必须在分页**之前**完成，否则敏感项会占掉页名额，
        // 使返回条数少于 pageSize 且翻页错位；因此先在库侧多取候选，再在内存过滤后分页。
        var candidates = await matches.Take(CandidateLimit).ToListAsync(ct);
        var allowed = candidates
            .Where(item => !_sensitivePolicy.IsProtected(item.Path))
            .ToList();

        var items = allowed
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(FileItemMapper.Map)
            .ToList();

        return new FileSearchResultDto(items, []);
    }
}
