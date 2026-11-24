using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

public sealed class NoteProcessingPreferencesService(
    PimDbContext db,
    ICurrentUserService user)
{
    public async Task<NoteProcessingPreferencesDto> GetAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var config = await db.NoteProcessingPreferences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner, ct);
        return config is null
            ? new(Guid.Empty, false)
            : new(config.Revision, config.AutoNoteProcessingEnabled);
    }

    public async Task<NoteProcessingPreferencesDto> SaveAsync(
        SaveNoteProcessingPreferencesRequest request,
        CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var config = await db.NoteProcessingPreferences.SingleOrDefaultAsync(
            x => x.OwnerId == owner, ct);
        if ((config?.Revision ?? Guid.Empty) != request.ExpectedRevision)
            throw new DomainException(40901, "笔记处理设置已变化，请刷新后重试");
        if (config is null)
        {
            config = new NoteProcessingPreferenceEntity
            {
                OwnerId = owner,
            };
            db.NoteProcessingPreferences.Add(config);
        }

        config.AutoNoteProcessingEnabled = request.AutoProcessingEnabled;
        config.Revision = Guid.NewGuid();
        config.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(40901, "笔记处理设置已被并发修改，请刷新后重试");
        }
        catch (DbUpdateException) when (config is not null)
        {
            throw new DomainException(40901, "笔记处理设置已被并发创建，请刷新后重试");
        }

        return new(config.Revision, config.AutoNoteProcessingEnabled);
    }

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = user.UserId ?? throw new DomainException(40101, "请先登录");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用，不能修改笔记处理设置");
        return owner;
    }
}
