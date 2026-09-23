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
    /// <summary>返回结果上限（单页，不传 pageSize 时使用）。</summary>
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
    /// 元数据搜索（设计 §9）：支持真分页并返回总数（REQ-8 / P7）。
    ///
    /// 敏感路径的排除**下推到 SQL**（在分页与计数之前），因此：
    /// <list type="bullet">
    ///   <item>结果里没有敏感项；</item>
    ///   <item><c>TotalCount</c> 也不含敏感项——否则「共 N 项」会泄漏被保护文件的数量；</item>
    ///   <item>分页不再需要「先多取 60 条候选再内存过滤」的旧窗口，第 2 页起不会凭空缺页。</item>
    /// </list>
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
            return new FileSearchResultDto([], [], 0, 0);
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
                    || (item.MimeType != null && item.MimeType.ToLower().Contains(lowered))));

        matches = ExcludeProtectedDirectories(matches);

        var totalCount = await matches.CountAsync(ct);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        var items = await matches
            .OrderBy(item => item.ItemType == "folder" ? 0 : 1)
            .ThenBy(item => item.Name.ToLower())
            .ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new FileSearchResultDto(
            items.Select(FileItemMapper.Map).ToList(),
            [],
            totalCount,
            totalPages);
    }

    /// <summary>
    /// 把 <see cref="SensitivePathPolicy"/> 的「目录本身或其子树」判定翻译成 SQL 谓词。
    ///
    /// 逐条 AND 一个 <c>Where</c>（而不是 <c>Any(...)</c>）：EF 无法把闭包里的
    /// <c>Any</c> 翻译成 SQL，逐条下推才能既保持与 <c>IsProtected</c> 相同的语义，
    /// 又让排除发生在分页与计数之前。
    ///
    /// 两侧都必须转小写：<c>IsProtected</c> 用的是 <c>OrdinalIgnoreCase</c>，
    /// 而 SQL 的 <c>=</c> / <c>LIKE</c> 在非 C 排序规则下**大小写敏感**。早先只对前缀转小写、
    /// 对目录本身用裸 <c>=</c>，导致 <c>/secrets</c> 这类大小写变体能绕过排除、
    /// 把受保护目录的存在泄漏进结果与 <c>TotalCount</c>（复审 Critical）。
    ///
    /// 关键细节：**两侧都在 SQL 里转小写**（<c>lower(path)</c> 对 <c>lower(@dir)</c>），
    /// 而不是在应用侧预先把目录转好。应用侧 <c>ToLowerInvariant</c> 与 PostgreSQL
    /// <c>lower()</c> 对少数 Unicode 字符结果不同（例如 <c>İ</c>：.NET 保持不变、
    /// PG 归约为 <c>i</c>），混用会让「策略判定受保护、SQL 却保留」这一**泄漏方向**重新出现。
    /// 两边都交给数据库，比较至少在内部自洽。
    ///
    /// 残余差异（已知，方向安全）：<c>IsProtected</c> 的 <c>OrdinalIgnoreCase</c> 与
    /// 数据库 <c>lower()</c> 仍可能在个别 Unicode 目录名上分歧，此时 SQL 谓词**更倾向排除**
    /// （宁可多隐藏、不泄漏）。默认规则 <c>/Secrets/*</c>、<c>/Passwords/*</c> 均为 ASCII，不受影响。
    /// </summary>
    private IQueryable<FileItemEntity> ExcludeProtectedDirectories(IQueryable<FileItemEntity> source)
    {
        foreach (var directory in _sensitivePolicy.ProtectedDirectories)
        {
            // 传给 SQL 的是**原样**目录；转小写在两侧分别由数据库完成，
            // 避免应用侧与数据库的 Unicode 大小写规则不同造成漏排除。
            var protectedDirectory = directory;
            var protectedPrefix = $"{directory}/";
            source = source.Where(item =>
                item.Path.ToLower() != protectedDirectory.ToLower()
                && !item.Path.ToLower().StartsWith(protectedPrefix.ToLower()));
        }

        return source;
    }
}
