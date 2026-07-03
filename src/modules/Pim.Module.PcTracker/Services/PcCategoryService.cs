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
        if (await _db.Set<PcCategoryEntity>().AnyAsync(ct))
            return;

        var categories = new List<PcCategoryEntity>
        {
            // Root: 娱乐
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000001"), Name = "娱乐", Color = "#EC4899", Icon = "🎮", Productivity = "distracting", SortOrder = 10, IsBuiltin = true },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000002"), Name = "游戏", Color = "#F43F5E", Icon = "🎮", Productivity = "distracting", SortOrder = 0, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000001") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000003"), Name = "单机游戏", Color = "#FB7185", Productivity = "distracting", SortOrder = 0, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000002") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000004"), Name = "网络游戏", Color = "#E11D48", Productivity = "distracting", SortOrder = 1, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000002") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000005"), Name = "视频", Color = "#F97316", Icon = "📺", Productivity = "distracting", SortOrder = 1, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000001") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000006"), Name = "音乐", Color = "#A855F7", Icon = "🎵", Productivity = "neutral", SortOrder = 2, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000001") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000007"), Name = "社交", Color = "#3B82F6", Icon = "💬", Productivity = "neutral", SortOrder = 3, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000001") },

            // Root: 工作
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000010"), Name = "工作", Color = "#22C55E", Icon = "💼", Productivity = "productive", SortOrder = 20, IsBuiltin = true },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000011"), Name = "编程", Color = "#22C55E", Icon = "💻", Productivity = "productive", SortOrder = 0, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000010") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000012"), Name = "前端", Color = "#3B82F6", Productivity = "productive", SortOrder = 0, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000011") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000013"), Name = "后端", Color = "#10B981", Productivity = "productive", SortOrder = 1, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000011") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000014"), Name = "文档", Color = "#F59E0B", Icon = "📄", Productivity = "productive", SortOrder = 1, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000010") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000015"), Name = "会议", Color = "#8B5CF6", Icon = "📞", Productivity = "productive", SortOrder = 2, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000010") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000016"), Name = "设计", Color = "#EC4899", Productivity = "productive", SortOrder = 3, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000010") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000017"), Name = "运维", Color = "#06B6D4", Productivity = "productive", SortOrder = 4, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000010") },

            // Root: 学习
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000020"), Name = "学习", Color = "#A855F7", Icon = "📚", Productivity = "productive", SortOrder = 30, IsBuiltin = true },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000021"), Name = "技术学习", Color = "#8B5CF6", Productivity = "productive", SortOrder = 0, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000020") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000022"), Name = "外语学习", Color = "#D946EF", Productivity = "productive", SortOrder = 1, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000020") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000023"), Name = "阅读", Color = "#F59E0B", Icon = "📖", Productivity = "neutral", SortOrder = 2, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000020") },

            // Root: 沟通
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000030"), Name = "沟通", Color = "#3B82F6", Icon = "💬", Productivity = "productive", SortOrder = 40, IsBuiltin = true },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000031"), Name = "即时消息", Color = "#6366F1", Productivity = "neutral", SortOrder = 0, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000030") },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000032"), Name = "邮件", Color = "#2563EB", Productivity = "productive", SortOrder = 1, IsBuiltin = true, ParentId = Guid.Parse("10000000-0000-0000-0000-000000000030") },

            // Root: 其他
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000099"), Name = "其他", Color = "#64748b", Icon = "📋", Productivity = "neutral", SortOrder = 99, IsBuiltin = true }
        };

        _db.Set<PcCategoryEntity>().AddRange(categories);
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
