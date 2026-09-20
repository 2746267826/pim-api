using Microsoft.EntityFrameworkCore;
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
    SensitivePathPolicy? sensitivePolicy = null)
{
    /// <summary>先按上限多取一些再做敏感过滤，避免敏感项吃掉结果预算。</summary>
    private const int CandidateLimit = 60;

    /// <summary>返回结果上限。</summary>
    private const int ResultLimit = 20;

    private readonly SensitivePathPolicy _sensitivePolicy = sensitivePolicy ?? new SensitivePathPolicy(null);

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "未登录");

    public async Task<FileSearchResultDto> SearchAsync(
        FileSearchQuery query,
        CancellationToken ct = default)
    {
        var search = query.Q?.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return new FileSearchResultDto([], []);
        }

        var lowered = search.ToLowerInvariant();
        // 数据库侧过滤（IQueryable），避免全表加载导致 OOM
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
            .Take(CandidateLimit)
            .ToListAsync(ct);

        var items = entities
            .Where(item => !_sensitivePolicy.IsProtected(item.Path))
            .Select(FileItemMapper.Map)
            .Take(ResultLimit)
            .ToList();

        return new FileSearchResultDto(items, []);
    }
}
