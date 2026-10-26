using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Local;
using Pim.Core.Exceptions;

namespace Pim.Module.Files.Services;

public static class BuiltinLocalAccount
{
    public static async Task<FileProviderEntity> EnsureAsync(PimDbContext caller, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) throw new DomainException(40101, "请先登录");
        // Provision independently: attachment creation may already have unsaved business entities in the caller.
        await using var db = new PimDbContext((DbContextOptions<PimDbContext>)caller.GetService<IDbContextOptions>());
        var id = LocalFileProvider.IdFor(userId);
        var existing = await db.Set<FileProviderEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, ct);
        if (existing is not null) return existing;
        var row = new FileProviderEntity
        {
            Id = id, UserId = userId, Provider = LocalFileProvider.Key, AccountId = "builtin", AccountName = "内置存储（服务器）",
            Status = "connected", IsDefault = false
        };
        db.Add(row);
        db.Add(new FileItemEntity { ProviderId = id, ExternalFileId = LocalFileStore.RootId, Name = "内置存储", Path = "/", ItemType = "folder" });
        try { await db.SaveChangesAsync(ct); return row; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.Set<FileProviderEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, ct);
            if (winner is null) throw;
            return winner;
        }
    }
}
