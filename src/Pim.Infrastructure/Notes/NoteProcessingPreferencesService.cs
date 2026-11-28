using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

public sealed class NoteProcessingPreferencesService(
    PimDbContext db,
    ICurrentUserService user,
    NoteProcessingStore processing,
    NoteProcessingTargetPolicyService targetPolicies)
{
    public async Task<NoteProcessingPreferencesDto> GetAsync(CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        var config = await db.NoteProcessingPreferences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner, ct);
        var targetPolicy = await targetPolicies.ResolveAsync(owner, ct);
        return config is null
            ? Empty(Guid.Empty, targetPolicy)
            : Describe(config, targetPolicy);
    }

    public async Task<NoteProcessingPreferencesDto> SaveAsync(
        SaveNoteProcessingPreferencesRequest request,
        CancellationToken ct)
    {
        var owner = await RequireActiveOwnerAsync(ct);
        return await processing.WithLockAsync(owner,
            lockedCt => SaveLockedAsync(owner, request, lockedCt), ct);
    }

    private async Task<NoteProcessingPreferencesDto> SaveLockedAsync(Guid owner,
        SaveNoteProcessingPreferencesRequest request, CancellationToken ct)
    {
        var config = await db.NoteProcessingPreferences.SingleOrDefaultAsync(
            x => x.OwnerId == owner, ct);
        if ((config?.Revision ?? Guid.Empty) != request.ExpectedRevision)
            throw new DomainException(40901, "笔记处理设置已变化，请刷新后重试");
        var targetPolicy = await targetPolicies.ResolveAsync(owner, ct);
        if (request.DefaultTargetRevision is { } expectedTarget &&
            expectedTarget != targetPolicy.Revision)
            throw new DomainException(40901,
                "账号默认处理目标已变化，请刷新目标并重新确认持续授权");
        if (config is null)
        {
            config = new NoteProcessingPreferenceEntity
            {
                OwnerId = owner,
            };
            db.NoteProcessingPreferences.Add(config);
        }

        if (request.AutoProcessingEnabled)
        {
            var hasStandingAuthorization =
                NoteProcessingAuthorizationRules.IsStandingAuthorizationValid(
                    config.StandingAuthorizationRevision,
                    config.StandingAuthorizationPurpose,
                    config.StandingAuthorizationMaterialScope,
                    config.StandingAuthorizationCostNotice,
                    config.StandingAuthorizationPossibleChargeAcknowledged,
                    config.StandingAuthorizationTargetRevision,
                    targetPolicy.Revision);
            if (!hasStandingAuthorization)
            {
                if (!request.ConfirmStandingAuthorization ||
                    !request.AcknowledgePossibleCharge)
                    throw new DomainException(40001,
                        "开启自动处理前必须确认处理目标、材料范围，并知悉云端处理可能产生费用");
                if (request.DefaultTargetRevision != targetPolicy.Revision)
                    throw new DomainException(40901,
                        "持续授权目标已变化，请刷新目标后重新确认");
                config.StandingAuthorizationRevision = Guid.NewGuid();
                config.StandingAuthorizationTargetRevision = targetPolicy.Revision;
                config.StandingAuthorizationPurpose =
                    NoteProcessingAuthorizationPurpose.NativeNoteProcessing;
                config.StandingAuthorizationMaterialScope =
                    NoteProcessingAuthorizationPurpose.MaterialScope;
                config.StandingAuthorizationCostNotice =
                    NoteProcessingAuthorizationRules.CostNotice;
                config.StandingAuthorizationPossibleChargeAcknowledged = true;
            }
        }
        else
        {
            config.StandingAuthorizationRevision = null;
            config.StandingAuthorizationTargetRevision = null;
            config.StandingAuthorizationPurpose = null;
            config.StandingAuthorizationMaterialScope = null;
            config.StandingAuthorizationCostNotice = null;
            config.StandingAuthorizationPossibleChargeAcknowledged = false;
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

        return Describe(config, targetPolicy);
    }

    internal static NoteProcessingPreferencesDto Describe(NoteProcessingPreferenceEntity config,
        NoteProcessingTargetPolicy targetPolicy)
    {
        var valid = NoteProcessingAuthorizationRules.IsStandingAuthorizationValid(
            config.StandingAuthorizationRevision,
            config.StandingAuthorizationPurpose,
            config.StandingAuthorizationMaterialScope,
            config.StandingAuthorizationCostNotice,
            config.StandingAuthorizationPossibleChargeAcknowledged,
            config.StandingAuthorizationTargetRevision,
            targetPolicy.Revision);
        return new(config.Revision, config.AutoNoteProcessingEnabled,
            valid ? config.StandingAuthorizationRevision : null,
            NoteProcessingAuthorizationPurpose.NativeNoteProcessing,
            NoteProcessingAuthorizationPurpose.PurposeText,
            NoteProcessingAuthorizationPurpose.MaterialScope,
            NoteProcessingAuthorizationPurpose.MaterialScopeText,
            NoteProcessingAuthorizationRules.CostNotice,
            config.StandingAuthorizationPossibleChargeAcknowledged,
            valid,
            targetPolicy.Revision,
            valid ? config.StandingAuthorizationTargetRevision : null,
            targetPolicy.Descriptions);
    }

    private static NoteProcessingPreferencesDto Empty(Guid revision,
        NoteProcessingTargetPolicy targetPolicy) =>
        new(revision, false, null, NoteProcessingAuthorizationPurpose.NativeNoteProcessing,
            NoteProcessingAuthorizationPurpose.PurposeText,
            NoteProcessingAuthorizationPurpose.MaterialScope,
            NoteProcessingAuthorizationPurpose.MaterialScopeText,
            NoteProcessingAuthorizationRules.CostNotice, false, false,
            targetPolicy.Revision, null, targetPolicy.Descriptions);

    private async Task<Guid> RequireActiveOwnerAsync(CancellationToken ct)
    {
        var owner = user.UserId ?? throw new DomainException(40101, "请先登录");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == owner && x.IsActive, ct))
            throw new DomainException(40301, "账号已停用，不能修改笔记处理设置");
        return owner;
    }
}
