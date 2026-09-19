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
    private readonly SensitivePathPolicy _sensitivePolicy;
    private readonly ILogger<OneDriveWriteService>? _logger;
    private readonly TimeProvider _clock;

    public OneDriveWriteService(
        PimDbContext db,
        IOneDriveGraphClient client,
        OneDriveTokenService tokens,
        ICurrentUserService currentUser,
        IAuditLogService auditLog,
        SensitivePathPolicy? sensitivePolicy = null,
        ILogger<OneDriveWriteService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _currentUser = currentUser;
        _auditLog = auditLog;
        _sensitivePolicy = sensitivePolicy ?? new SensitivePathPolicy(null);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<OneDriveWriteResult> MoveAsync(Guid itemId, string destinationFolderPath, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        // 根项不可移动：Path="/" 会让子孙前缀改写退化成「匹配全部项」（复审 I-12）。
        if (item.Path == "/")
        {
            throw new DomainException(5337, "不能移动 OneDrive 根目录");
        }

        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);

        var folder = await ResolveFolderAsync(provider.Id, destinationFolderPath, ct)
            ?? throw new DomainException(5304, "目标文件夹不存在（未同步或已删除）");
        if (folder.Id == item.Id)
        {
            throw new DomainException(5337, "不能把文件夹移动到自身");
        }
        // 目标是自己子孙时，Graph 会拒绝，但本地已按新前缀改写过子孙 Path，会造成永久错乱；
        // 因此必须在调用 Graph 之前拦下（复审 I-12）。
        if (item.ItemType == "folder"
            && folder.Path.StartsWith(item.Path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(5337, "不能把文件夹移动到自己的子目录");
        }

        var newId = await _client.PatchItemAsync(token, item.ExternalFileId, null, folder.ExternalFileId, ct);
        var newPath = (folder.Path == "/" ? string.Empty : folder.Path) + "/" + item.Name;
        var oldPath = item.Path;
        item.ParentExternalFileId = folder.ExternalFileId;
        item.Path = newPath;
        item.SyncedAt = _clock.GetUtcNow();
        // 目录移动后，子孙的 Path 必须跟着改写，否则树/搜索/敏感路径判断全部失准（复审 I-6）
        await UpdateDescendantPathsAsync(provider.Id, oldPath, newPath, ct);
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
        // 根项（Path="/"）不可重命名：其 Name 与 Path 无关（Path 就是 "/"，不含名字），
        // 用 `Path[..^oldName.Length]` 反推前缀会越界抛 ArgumentOutOfRangeException（复审 I-12）。
        if (item.Path == "/")
        {
            throw new DomainException(5338, "不能重命名 OneDrive 根目录");
        }

        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);

        await _client.PatchItemAsync(token, item.ExternalFileId, newName.Trim(), null, ct);
        var oldName = item.Name;
        var oldPath = item.Path;
        item.Name = newName.Trim();
        item.Path = item.Path[..^oldName.Length] + item.Name;
        item.SyncedAt = _clock.GetUtcNow();
        // 目录改名后子孙 Path 同样要跟着改（复审 I-6）
        await UpdateDescendantPathsAsync(provider.Id, oldPath, item.Path, ct);
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.onedrive.rename", item.Id, ct);
        return new OneDriveWriteResult(item.Id, item.Path);
    }

    public async Task DeleteToTrashAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        // 删除根项会把整盘软删，且远端 DELETE 根没有任何意义（复审 I-12）。
        if (item.Path == "/")
        {
            throw new DomainException(5337, "不能删除 OneDrive 根目录");
        }

        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);

        // Graph DELETE 把文件移入 OneDrive 自身回收站；本地软删提供 PIM 回收站语义
        await _client.DeleteItemAsync(token, item.ExternalFileId, ct);
        var now = _clock.GetUtcNow();
        item.IsDeleted = true;
        item.DeletedAt = now;
        // 目录被删时子孙在远端已随父项一起进回收站，本地必须一并软删，
        // 否则树里会留下「父目录已删、子项仍可见」的悬空节点（复审 I-6）
        await MarkDescendantsDeletedAsync(provider.Id, item.Path, now, ct);
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.onedrive.delete_to_trash", item.Id, ct);
    }

    /// <summary>把 oldPath 前缀下的子孙 Path 前缀替换为 newPath（目录移动/改名后调用）。</summary>
    private async Task UpdateDescendantPathsAsync(
        Guid providerId,
        string oldPath,
        string newPath,
        CancellationToken ct)
    {
        var prefix = oldPath.TrimEnd('/') + "/";
        var descendants = await _db.Set<FileItemEntity>()
            .Where(row => row.ProviderId == providerId
                && row.Path.StartsWith(prefix)
                && !row.IsDeleted)
            .ToListAsync(ct);
        if (descendants.Count == 0)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        foreach (var descendant in descendants)
        {
            descendant.Path = newPath.TrimEnd('/') + descendant.Path[oldPath.TrimEnd('/').Length..];
            descendant.SyncedAt = now;
        }
    }

    /// <summary>软删 folderPath 下的全部子孙（目录删除后调用）。</summary>
    private async Task MarkDescendantsDeletedAsync(
        Guid providerId,
        string folderPath,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var prefix = folderPath.TrimEnd('/') + "/";
        var descendants = await _db.Set<FileItemEntity>()
            .Where(row => row.ProviderId == providerId
                && row.Path.StartsWith(prefix)
                && !row.IsDeleted)
            .ToListAsync(ct);
        foreach (var descendant in descendants)
        {
            descendant.IsDeleted = true;
            descendant.DeletedAt = now;
        }
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

        // 边读边计数：不能先 CopyToAsync 全量读进内存再判上限，
        // 否则超大 multipart 请求会在检查前就把内存吃光（复审 I-11）。
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > OneDriveContentService.MaxSaveBytes)
            {
                throw new DomainException(5331, $"上传文件超过 {OneDriveContentService.MaxSaveBytes / 1024 / 1024}MB，请使用 OneDrive 客户端");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();

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
        // webUrl 是内容出口：敏感路径必须与其他出口（content/thumbnail/preview/text/
        // read_file_text）同样拦截，否则 /Secrets/* 可以借 open-link 拿到直通链接（复审 I-1）。
        EnsureNotSensitive(item);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var webUrl = await _client.GetItemWebUrlAsync(token, item.ExternalFileId, ct);
        return webUrl ?? throw new DomainException(5333, "OneDrive 暂未返回网页地址，请稍后重试");
    }

    private void EnsureNotSensitive(FileItemEntity item)
    {
        if (_sensitivePolicy.IsProtected(item.Path))
        {
            throw new DomainException(40303, "敏感路径受保护，不允许该操作");
        }
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
