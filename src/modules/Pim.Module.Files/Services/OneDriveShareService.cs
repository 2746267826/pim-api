using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

/// <summary>分享链接的对外视图（REQ-21）。</summary>
public sealed record FileShareDto(
    Guid ItemId,
    string ItemName,
    string Path,
    string PermissionType,
    string? PermissionId,
    string WebUrl,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// OneDrive 分享链接（REQ-21）：生成 / 撤销 / 列出。
///
/// 安全约束（与直链、缩略图、预览、文本、搜索同样的出口口径）：
/// <list type="bullet">
///   <item><b>敏感路径不得分享</b>（AC-21.4）——否则 /Secrets/* 会借分享绕开全部保护；</item>
///   <item><b>不把完整链接写进日志或审计</b>（AC-21.4）：审计只记条目 id 与权限档，
///   绝不记 webUrl。链接本身只在 HTTP 响应里返回给发起人。</item>
/// </list>
/// </summary>
public sealed class OneDriveShareService(
    PimDbContext db,
    IOneDriveGraphClient client,
    OneDriveTokenService tokens,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    SensitivePathPolicy? sensitivePolicy = null)
{
    /// <summary>个人版支持的权限档（V3 结论：view / edit 均可请求；embed 不作为 UI 档位）。</summary>
    public static readonly IReadOnlyList<string> SupportedPermissionTypes = ["view", "edit"];

    private readonly SensitivePathPolicy _sensitivePolicy = sensitivePolicy ?? new SensitivePathPolicy(null);

    private Guid UserId => currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<FileShareDto> CreateAsync(
        Guid itemId,
        string permissionType,
        int? expiresInDays,
        CancellationToken ct = default)
    {
        var permission = ParsePermission(permissionType);
        var expiration = ResolveExpiration(expiresInDays);
        var (item, provider) = await LoadShareableItemAsync(itemId, ct);

        var token = await tokens.GetAccessTokenAsync(provider.Id, ct);
        var link = await client.CreateShareLinkAsync(token, item.ExternalFileId, permission, expiration, ct);

        // 审计只记「谁对哪个条目建了哪一档权限」，不记链接本身（AC-21.4）
        await auditLog.RecordAsync(new CreateAuditLogRequest(
            UserId,
            AuditActorType.User,
            "files.share_create",
            "file_item",
            item.Id.ToString(),
            "files",
            AuditResult.Success,
            null, null, null, null, null, null), ct);

        return new FileShareDto(
            item.Id,
            item.Name,
            item.Path,
            link.PermissionType,
            link.PermissionId,
            link.WebUrl,
            link.ExpiresAt ?? expiration,
            DateTimeOffset.UtcNow);
    }

    /// <summary>撤销某个条目的分享权限（AC-21.1：撤销后链接失效）。</summary>
    public async Task RevokeAsync(Guid itemId, string permissionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(permissionId))
        {
            throw new DomainException(5300, "需要权限 ID");
        }

        var (item, provider) = await LoadShareableItemAsync(itemId, ct);
        var token = await tokens.GetAccessTokenAsync(provider.Id, ct);
        await client.RevokeSharePermissionAsync(token, item.ExternalFileId, permissionId, ct);

        await auditLog.RecordAsync(new CreateAuditLogRequest(
            UserId,
            AuditActorType.User,
            "files.share_revoke",
            "file_item",
            item.Id.ToString(),
            "files",
            AuditResult.Success,
            null, null, null, null, null, null), ct);
    }

    /// <summary>列出某条目当前的全部分享（AC-21.3：预览面板就地撤销 / 我的分享）。</summary>
    public async Task<IReadOnlyList<FileShareDto>> ListForItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadShareableItemAsync(itemId, ct);
        var token = await tokens.GetAccessTokenAsync(provider.Id, ct);
        var links = await client.ListSharePermissionsAsync(token, item.ExternalFileId, ct);

        return links
            .Select(link => new FileShareDto(
                item.Id,
                item.Name,
                item.Path,
                link.PermissionType,
                link.PermissionId,
                link.WebUrl,
                link.ExpiresAt,
                DateTimeOffset.UtcNow))
            .ToList();
    }

    /// <summary>
    /// 「我的分享」列表：对当前用户已同步的条目逐个查询分享权限。
    ///
    /// 个人版没有「列出我的全部分享」的接口（V2/V3 探测结论），只能按条目查询；
    /// 因此这里限定在一个**有界**的候选集内（最近同步的文件），并在响应里如实说明范围，
    /// 而不是假装能列全（AC-21.3 的口径）。
    /// </summary>
    public async Task<IReadOnlyList<FileShareDto>> ListAllAsync(int limit = 50, CancellationToken ct = default)
    {
        var userId = UserId;
        var candidates = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(row => row.Provider)
            .Where(row => row.Provider != null
                && row.Provider.UserId == userId
                && row.Provider.Provider == "onedrive"
                && row.Provider.Status == "connected"
                && !row.IsDeleted
                && row.ItemType == "file")
            .OrderByDescending(row => row.ModifiedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

        var result = new List<FileShareDto>();
        foreach (var item in candidates)
        {
            if (_sensitivePolicy.IsProtected(item.Path))
            {
                continue;
            }

            try
            {
                var provider = item.Provider!;
                var token = await tokens.GetAccessTokenAsync(provider.Id, ct);
                var links = await client.ListSharePermissionsAsync(token, item.ExternalFileId, ct);
                result.AddRange(links.Select(link => new FileShareDto(
                    item.Id,
                    item.Name,
                    item.Path,
                    link.PermissionType,
                    link.PermissionId,
                    link.WebUrl,
                    link.ExpiresAt,
                    DateTimeOffset.UtcNow)));
            }
            catch (OneDriveGraphException)
            {
                // 单个条目查询失败不影响整表（例如该条目刚被远端删除）
            }
        }

        return result;
    }

    private static OneDriveSharePermission ParsePermission(string? permissionType)
        => permissionType?.Trim().ToLowerInvariant() switch
        {
            "edit" => OneDriveSharePermission.Edit,
            "view" or null or "" => OneDriveSharePermission.View,
            _ => throw new DomainException(5300, "分享权限只支持 view（可看）或 edit（可编辑）"),
        };

    /// <summary>P6：无 / 7 天 / 30 天。其余取值一律拒绝，避免静默降级成「不过期」。</summary>
    private static DateTimeOffset? ResolveExpiration(int? expiresInDays)
        => expiresInDays switch
        {
            null or 0 => null,
            7 => DateTimeOffset.UtcNow.AddDays(7),
            30 => DateTimeOffset.UtcNow.AddDays(30),
            _ => throw new DomainException(5300, "分享有效期只支持 7 天或 30 天（或不过期）"),
        };

    /// <summary>归属校验 + 敏感路径闸门（与直链/缩略图/预览/文本同样的口径）。</summary>
    private async Task<(FileItemEntity Item, FileProviderEntity Provider)> LoadShareableItemAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.Set<FileItemEntity>()
            .Include(row => row.Provider)
            .SingleOrDefaultAsync(row => row.Id == itemId, ct)
            ?? throw new DomainException(5104, "文件不存在");

        if (item.Provider is null
            || item.Provider.UserId != UserId
            || item.Provider.Provider != "onedrive"
            || item.Provider.Status != "connected"
            || item.IsDeleted)
        {
            throw new DomainException(5104, "文件不存在");
        }

        if (_sensitivePolicy.IsProtected(item.Path))
        {
            throw new DomainException(40303, "敏感路径受保护，不允许分享");
        }

        return (item, item.Provider);
    }
}
