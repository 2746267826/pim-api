using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class PcCategoryService
{
    private readonly PimDbContext _db;

    public PcCategoryService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<List<CategoryTreeNode>> GetTreeAsync(CancellationToken ct)
    {
        var all = await _db.Set<PcCategoryEntity>()
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

        return BuildTree(all, null);
    }

    /// <summary>平铺字典：7 大类 + 自定义分类，按 sort_order 排序（前端打标组件用）。</summary>
    public async Task<List<CategoryDictionaryItemDto>> GetDictionaryAsync(CancellationToken ct)
    {
        return await _db.Set<PcCategoryEntity>()
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(c => new CategoryDictionaryItemDto(c.Id, c.Name, c.Color, c.Icon))
            .ToListAsync(ct);
    }

    private static List<CategoryTreeNode> BuildTree(List<PcCategoryEntity> all, Guid? parentId)
    {
        return all
            .Where(c => c.ParentId == parentId)
            .Select(c => new CategoryTreeNode
            {
                Id = c.Id,
                ParentId = c.ParentId,
                Name = c.Name,
                Color = c.Color,
                Icon = c.Icon,
                Productivity = c.Productivity,
                SortOrder = c.SortOrder,
                IsBuiltin = c.IsBuiltin,
                Children = BuildTree(all, c.Id)
            })
            .ToList();
    }

    public async Task<CategoryTreeNode> SaveAsync(CategorySaveRequest req, CancellationToken ct)
    {
        PcCategoryEntity entity;
        if (req.Id.HasValue)
        {
            entity = await _db.Set<PcCategoryEntity>().FindAsync(new object[] { req.Id.Value }, ct)
                ?? throw new KeyNotFoundException("分类不存在");
            entity.Name = req.Name;
            entity.Color = req.Color;
            entity.Icon = req.Icon;
            entity.Productivity = req.Productivity;
            entity.ParentId = req.ParentId;
            entity.SortOrder = req.SortOrder;
            entity.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            entity = new PcCategoryEntity
            {
                Id = Guid.NewGuid(),
                ParentId = req.ParentId,
                Name = req.Name,
                Color = req.Color,
                Icon = req.Icon,
                Productivity = req.Productivity,
                SortOrder = req.SortOrder,
                IsBuiltin = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Set<PcCategoryEntity>().Add(entity);
        }

        await _db.SaveChangesAsync(ct);
        return MapToNode(entity, new List<CategoryTreeNode>());
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<PcCategoryEntity>()
            .Include(c => c.Children)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity is null || entity.IsBuiltin)
            return false;

        // Has children? Block deletion
        if (entity.Children.Count > 0)
            throw new InvalidOperationException("该分类下还有子分类，请先删除子分类");

        // Check if any app signatures reference this category
        var hasRefs = await _db.Set<AppSignatureEntity>()
            .AnyAsync(s => s.CategoryPath != null && s.CategoryPath.Contains(entity.Name), ct);

        _db.Set<PcCategoryEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReorderAsync(ReorderCategoriesRequest req, CancellationToken ct)
    {
        var ids = req.Items.Select(i => i.Id).ToList();
        var entities = await _db.Set<PcCategoryEntity>()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);

        foreach (var item in req.Items)
        {
            var entity = entities.FirstOrDefault(e => e.Id == item.Id);
            if (entity is not null)
            {
                entity.ParentId = item.ParentId;
                entity.SortOrder = item.SortOrder;
                entity.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task SeedDefaultsAsync(CancellationToken ct)
    {
        var rootWorkId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var rootLearnId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var rootVideoId = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var rootChatId = Guid.Parse("20000000-0000-0000-0000-000000000004");
        var rootDocsId = Guid.Parse("20000000-0000-0000-0000-000000000005");
        var rootGameId = Guid.Parse("20000000-0000-0000-0000-000000000006");
        var rootOtherId = Guid.Parse("20000000-0000-0000-0000-000000000007");

        var categories = new List<PcCategoryEntity>
        {
            new() { Id = rootWorkId, Name = CategoryLegacyMapper.ProgrammingTinkering, Color = "#6B5EE4", Icon = "💻", Productivity = "productive", SortOrder = 10, IsBuiltin = true },
            new() { Id = rootLearnId, Name = CategoryLegacyMapper.Learning, Color = "#14b8a6", Icon = "📚", Productivity = "productive", SortOrder = 20, IsBuiltin = true },
            new() { Id = rootVideoId, Name = CategoryLegacyMapper.Video, Color = "#F97316", Icon = "📺", Productivity = "distracting", SortOrder = 30, IsBuiltin = true },
            new() { Id = rootChatId, Name = CategoryLegacyMapper.Chat, Color = "#3B82F6", Icon = "💬", Productivity = "neutral", SortOrder = 40, IsBuiltin = true },
            new() { Id = rootDocsId, Name = CategoryLegacyMapper.Documents, Color = "#F59E0B", Icon = "📄", Productivity = "productive", SortOrder = 50, IsBuiltin = true },
            new() { Id = rootGameId, Name = CategoryLegacyMapper.Gaming, Color = "#F43F5E", Icon = "🎮", Productivity = "distracting", SortOrder = 60, IsBuiltin = true },
            new() { Id = rootOtherId, Name = CategoryLegacyMapper.Other, Color = "#64748b", Icon = "📋", Productivity = "neutral", SortOrder = 99, IsBuiltin = true },

            // Subcategories under ProgrammingTinkering
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000101"), ParentId = rootWorkId, Name = "前端", Color = "#818cf8", Icon = "🌐", Productivity = "productive", SortOrder = 11, IsBuiltin = true },
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000102"), ParentId = rootWorkId, Name = "后端", Color = "#6366f1", Icon = "⚙️", Productivity = "productive", SortOrder = 12, IsBuiltin = true },
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000103"), ParentId = rootWorkId, Name = "运维", Color = "#4f46e5", Icon = "☸️", Productivity = "productive", SortOrder = 13, IsBuiltin = true },
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000104"), ParentId = rootWorkId, Name = "设计", Color = "#a855f7", Icon = "🎨", Productivity = "productive", SortOrder = 14, IsBuiltin = true },

            // Subcategories under Gaming
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000601"), ParentId = rootGameId, Name = "单机游戏", Color = "#fb7185", Icon = "🕹️", Productivity = "distracting", SortOrder = 61, IsBuiltin = true },
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000602"), ParentId = rootGameId, Name = "网络游戏", Color = "#e11d48", Icon = "⚔️", Productivity = "distracting", SortOrder = 62, IsBuiltin = true },

            // Subcategories under Documents
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000501"), ParentId = rootDocsId, Name = "办公", Color = "#fbbf24", Icon = "📑", Productivity = "productive", SortOrder = 51, IsBuiltin = true },
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000502"), ParentId = rootDocsId, Name = "笔记与知识库", Color = "#d97706", Icon = "📓", Productivity = "productive", SortOrder = 52, IsBuiltin = true }
        };

        var existingCategories = await _db.Set<PcCategoryEntity>()
            .Select(category => new { category.Id, category.Name })
            .ToListAsync(ct);
        var existingIdSet = existingCategories
            .Select(category => category.Id)
            .ToHashSet();
        var existingByName = existingCategories
            .GroupBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var resolvedIds = new Dictionary<Guid, Guid>();
        var missing = new List<PcCategoryEntity>();

        foreach (var category in categories)
        {
            if (existingIdSet.Contains(category.Id))
            {
                resolvedIds[category.Id] = category.Id;
                continue;
            }

            if (existingByName.TryGetValue(category.Name, out var existingId))
            {
                resolvedIds[category.Id] = existingId;
                continue;
            }

            if (category.ParentId is Guid parentId && resolvedIds.TryGetValue(parentId, out var resolvedParentId))
                category.ParentId = resolvedParentId;

            missing.Add(category);
            existingIdSet.Add(category.Id);
            existingByName[category.Name] = category.Id;
            resolvedIds[category.Id] = category.Id;
        }

        if (missing.Count == 0)
            return;

        _db.Set<PcCategoryEntity>().AddRange(missing);
        await _db.SaveChangesAsync(ct);
    }

    private static CategoryTreeNode MapToNode(PcCategoryEntity entity, List<CategoryTreeNode> children)
    {
        return new CategoryTreeNode
        {
            Id = entity.Id,
            ParentId = entity.ParentId,
            Name = entity.Name,
            Color = entity.Color,
            Icon = entity.Icon,
            Productivity = entity.Productivity,
            SortOrder = entity.SortOrder,
            IsBuiltin = entity.IsBuiltin,
            Children = children
        };
    }
}
