using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

public sealed record OneDriveWriteResult(Guid ItemId, string Path);

/// <summary>
/// OneDrive 写操作（设计文档 §12）：移动 / 重命名 / 删除进回收站 / 本地恢复 /
/// 小文件上传 / OneDrive 网页链接。全部写操作经 Graph 执行并写审计；
/// 失败时本地元数据不变（Graph 先行，成功后本地收敛）。
/// </summary>
public sealed class OneDriveWriteService
{
    private readonly PimDbContext _db;
    private readonly IOneDriveGraphClient _client;
    private readonly OneDriveTokenService _tokens;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<OneDriveWriteService>? _logger;
    private readonly TimeProvider _clock;

    public OneDriveWriteService(
        PimDbContext db,
        IOneDriveGraphClient client,
        OneDriveTokenService tokens,
        ICurrentUserService currentUser,
        IAuditLogService auditLog,
        ILogger<OneDriveWriteService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _currentUser = currentUser;
        _auditLog = auditLog;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<OneDriveWriteResult> MoveAsync(Guid itemId, string destinationFolderPath, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);

        var folder = await ResolveFolderAsync(provider.Id, destinationFolderPath, ct)
            ?? throw new DomainException(5304, "目标文件夹不存在（未同步或已删除）");
        if (folder.Id == item.Id)
        {
            throw new DomainException(5337, "不能把文件夹移动到自身");
        }

        var newId = await _client.PatchItemAsync(token, item.ExternalFileId, null, folder.ExternalFileId, ct);
        var newPath = (folder.Path == "/" ? string.Empty : folder.Path) + "/" + item.Name;
        item.ParentExternalFileId = folder.ExternalFileId;
        item.Path = newPath;
        item.SyncedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.onedrive.move", item.Id, ct);
        return new OneDriveWriteResult(item.Id, newPath);
    }

    public async Task<OneDriveWriteResult> RenameAsync(Guid itemId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/'))
        {
            throw new DomainException(5338, "新名称不能为空且不能包含 /");
        }

        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);

        await _client.PatchItemAsync(token, item.ExternalFileId, newName.Trim(), null, ct);
        var oldName = item.Name;
        item.Name = newName.Trim();
        item.Path = item.Path[..^oldName.Length] + item.Name;
        item.SyncedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.onedrive.rename", item.Id, ct);
        return new OneDriveWriteResult(item.Id, item.Path);
    }

    public async Task DeleteToTrashAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);

        // Graph DELETE 把文件移入 OneDrive 自身回收站；本地软删提供 PIM 回收站语义
        await _client.DeleteItemAsync(token, item.ExternalFileId, ct);
        item.IsDeleted = true;
        item.DeletedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.onedrive.delete_to_trash", item.Id, ct);
    }

    /// <summary>
    /// 本地恢复：OneDrive 个人版无回收站 API（设计 §14-V5），
    /// 恢复 = 校验文件仍在 OneDrive（delta 同步语义下软删即远端已删，
    /// 因此只有「本地软删但远端仍存在」的短暂窗口可恢复），其余返回明确错误。
    /// </summary>
    public async Task<OneDriveWriteResult> RestoreAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadItemIncludingDeletedAsync(itemId, ct);
        if (!item.IsDeleted)
        {
            throw new DomainException(5339, "该文件不在回收站中");
        }

        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        try
        {
            await _client.GetDownloadUrlAsync(token, item.ExternalFileId, ct);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode == 404)
        {
            throw new DomainException(5340, "文件已从 OneDrive 删除，无法恢复");
        }

        item.IsDeleted = false;
        item.DeletedAt = null;
        item.SyncedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.onedrive.restore", item.Id, ct);
        return new OneDriveWriteResult(item.Id, item.Path);
    }

    /// <summary>小文件上传（≤4MB）：上传到目标路径并立即收敛本地元数据。</summary>
    public async Task<OneDriveWriteResult> UploadAsync(
        string destinationFolderPath,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/'))
        {
            throw new DomainException(5309, "文件名不能为空且不能包含 /");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (bytes.Length > OneDriveContentService.MaxSaveBytes)
        {
            throw new DomainException(5331, $"上传文件超过 {OneDriveContentService.MaxSaveBytes / 1024 / 1024}MB，请使用 OneDrive 客户端");
        }

        var (provider, folder) = await LoadConnectedProviderAsync(ct);
        var folderPath = await EnsureFolderExistsAsync(provider, destinationFolderPath, ct);
        var itemPath = (folderPath == "/" ? string.Empty : folderPath) + "/" + fileName;

        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var driveItemId = await _client.PutNewFileByPathAsync(token, itemPath, bytes, contentType, ct);

        var now = _clock.GetUtcNow();
        var item = await _db.Set<FileItemEntity>()
            .SingleOrDefaultAsync(row => row.ProviderId == provider.Id && row.ExternalFileId == driveItemId, ct);
        if (item is null)
        {
            item = new FileItemEntity
            {
                ProviderId = provider.Id,
                ExternalFileId = driveItemId,
                CreatedAt = now,
            };
            _db.Set<FileItemEntity>().Add(item);
        }

        item.ParentExternalFileId = folder.ExternalFileId;
        item.Path = itemPath;
        item.Name = fileName;
        item.ItemType = "file";
        item.MimeType = contentType;
        item.Size = bytes.Length;
        item.IsDeleted = false;
        item.DeletedAt = null;
        item.LastSeenAt = now;
        item.ModifiedAt = now;
        item.SyncedAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.onedrive.upload", item.Id, ct);
        return new OneDriveWriteResult(item.Id, itemPath);
    }

    /// <summary>OneDrive 网页地址（替代 v1 的 BuildOpenLink）。</summary>
    public async Task<string> GetWebUrlAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var webUrl = await _client.GetItemWebUrlAsync(token, item.ExternalFileId, ct);
        return webUrl ?? throw new DomainException(5333, "OneDrive 暂未返回网页地址，请稍后重试");
    }

    private async Task<(FileItemEntity Item, FileProviderEntity Provider)> LoadConnectedItemAsync(Guid itemId, CancellationToken ct)
    {
        var (item, provider) = await LoadItemIncludingDeletedAsync(itemId, ct);
        if (item.IsDeleted)
        {
            throw new DomainException(5104, "文件不存在");
        }

        return (item, provider);
    }

    private async Task<(FileItemEntity Item, FileProviderEntity Provider)> LoadItemIncludingDeletedAsync(Guid itemId, CancellationToken ct)
    {
        var item = await _db.Set<FileItemEntity>()
            .Include(row => row.Provider)
            .SingleOrDefaultAsync(row => row.Id == itemId, ct)
            ?? throw new DomainException(5104, "文件不存在");
        if (item.Provider is null
            || item.Provider.UserId != UserId
            || item.Provider.Provider != "onedrive"
            || item.Provider.Status != "connected")
        {
            throw new DomainException(5104, "文件不存在");
        }

        return (item, item.Provider);
    }

    private async Task<(FileProviderEntity Provider, FileItemEntity Folder)> LoadConnectedProviderAsync(CancellationToken ct)
    {
        var provider = await _db.Set<FileProviderEntity>()
            .Include(row => row.Items)
            .SingleOrDefaultAsync(row => row.UserId == UserId && row.Provider == "onedrive", ct)
            ?? throw new DomainException(5320, "OneDrive 绑定不存在");
        if (provider.Status != "connected")
        {
            throw new DomainException(5321, "OneDrive 尚未完成绑定");
        }

        var root = provider.Items.FirstOrDefault(item => item.ItemType == "folder" && item.Path == "/")
            ?? new FileItemEntity
            {
                ProviderId = provider.Id,
                ExternalFileId = "root",
                Path = "/",
                Name = "root",
                ItemType = "folder",
            };
        return (provider, root);
    }

    private async Task<string> EnsureFolderExistsAsync(FileProviderEntity provider, string folderPath, CancellationToken ct)
    {
        var normalized = "/" + folderPath.Trim().Trim('/');
        if (normalized == "/")
        {
            return "/";
        }

        var existing = await _db.Set<FileItemEntity>()
            .SingleOrDefaultAsync(item => item.ProviderId == provider.Id
                && item.ItemType == "folder"
                && (item.Path == normalized || item.Path == normalized.TrimEnd('/')), ct);
        return existing?.Path
            ?? throw new DomainException(5304, "目标文件夹不存在（未同步或已删除）");
    }

    private async Task<FileItemEntity?> ResolveFolderAsync(Guid providerId, string folderPath, CancellationToken ct)
    {
        var normalized = "/" + folderPath.Trim().Trim('/');
        if (normalized == "/")
        {
            return await _db.Set<FileItemEntity>()
                .FirstOrDefaultAsync(item => item.ProviderId == providerId && item.Path == "/", ct);
        }

        return await _db.Set<FileItemEntity>()
            .FirstOrDefaultAsync(item => item.ProviderId == providerId
                && item.ItemType == "folder"
                && item.Path == normalized, ct);
    }

    private async Task RecordAuditAsync(string action, Guid itemId, CancellationToken ct)
    {
        await _auditLog.RecordAsync(new CreateAuditLogRequest(
            UserId,
            AuditActorType.User,
            action,
            "file_item",
            itemId.ToString(),
            "files",
            AuditResult.Success,
            null,
            null,
            null,
            null,
            null,
            null), ct);
    }
}
