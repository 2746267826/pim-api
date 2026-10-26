using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Services;

public sealed record FileWriteResult(Guid ItemId, string Path);

/// <summary>
/// 通用写操作（工单 05 / F1）：移动 / 重命名 / 删除进回收站 / 本地恢复 /
/// 小文件上传 / 上传会话 / 新建目录 / 网页链接。
/// 全部写操作经 provider 抽象执行并写审计；失败时本地元数据不变（远端先行，成功后本地收敛）。
///
/// 与旧 <c>OneDriveWriteService</c> 的关键差别（F2.2）：
/// 需要「新建文件」的入口（上传、上传会话、新建目录）**必须带账号**，
/// 不再有「按用户取唯一 OneDrive」的写法 —— 那是多账号下必然错的行。
/// </summary>
public sealed class FileWriteService
{
    private readonly PimDbContext _db;
    private readonly FileProviderSessionFactory _sessions;
    private readonly IFileProviderFactory _providers;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogService _auditLog;
    private readonly SensitivePathPolicy _sensitivePolicy;
    private readonly ILogger<FileWriteService>? _logger;
    private readonly TimeProvider _clock;

    public FileWriteService(
        PimDbContext db,
        FileProviderSessionFactory sessions,
        IFileProviderFactory providers,
        ICurrentUserService currentUser,
        IAuditLogService auditLog,
        SensitivePathPolicy? sensitivePolicy = null,
        ILogger<FileWriteService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _sessions = sessions;
        _providers = providers;
        _currentUser = currentUser;
        _auditLog = auditLog;
        _sensitivePolicy = sensitivePolicy ?? new SensitivePathPolicy(null);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<FileWriteResult> MoveAsync(Guid itemId, string destinationFolderPath, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        // 根项不可移动：Path="/" 会让子孙前缀改写退化成「匹配全部项」。
        if (item.Path == "/")
        {
            throw new DomainException(5337, "不能移动网盘根目录");
        }

        var session = await _sessions.OpenAsync(provider, ct);

        var folder = await ResolveFolderAsync(provider.Id, destinationFolderPath, ct)
            ?? throw new DomainException(5304, "目标文件夹不存在（未同步或已删除）");
        if (folder.Id == item.Id)
        {
            throw new DomainException(5337, "不能把文件夹移动到自身");
        }

        // 目标是自己子孙时，远端会拒绝，但本地已按新前缀改写过子孙 Path，会造成永久错乱；
        // 因此必须在调用远端之前拦下。
        if (item.ItemType == "folder"
            && folder.Path.StartsWith(item.Path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(5337, "不能把文件夹移动到自己的子目录");
        }

        await session.Writer.PatchItemAsync(item.ExternalFileId, null, folder.ExternalFileId, ct);
        var newPath = (folder.Path == "/" ? string.Empty : folder.Path) + "/" + item.Name;
        var oldPath = item.Path;
        item.ParentExternalFileId = folder.ExternalFileId;
        item.Path = newPath;
        item.SyncedAt = _clock.GetUtcNow();
        // 目录移动后，子孙的 Path 必须跟着改写，否则树/搜索/敏感路径判断全部失准
        await UpdateDescendantPathsAsync(provider.Id, oldPath, newPath, ct);
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.move", item.Id, ct);
        return new FileWriteResult(item.Id, newPath);
    }

    public async Task<FileWriteResult> RenameAsync(Guid itemId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/'))
        {
            throw new DomainException(5338, "新名称不能为空且不能包含 /");
        }

        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        // 根项（Path="/"）不可重命名：用 `Path[..^oldName.Length]` 反推前缀会越界
        if (item.Path == "/")
        {
            throw new DomainException(5338, "不能重命名网盘根目录");
        }

        var session = await _sessions.OpenAsync(provider, ct);
        await session.Writer.PatchItemAsync(item.ExternalFileId, newName.Trim(), null, ct);
        var oldName = item.Name;
        var oldPath = item.Path;
        item.Name = newName.Trim();
        item.Path = item.Path[..^oldName.Length] + item.Name;
        item.SyncedAt = _clock.GetUtcNow();
        // 目录改名后子孙 Path 同样要跟着改
        await UpdateDescendantPathsAsync(provider.Id, oldPath, item.Path, ct);
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.rename", item.Id, ct);
        return new FileWriteResult(item.Id, item.Path);
    }

    public async Task DeleteToTrashAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        // 删除根项会把整盘软删，且远端 DELETE 根没有任何意义
        if (item.Path == "/")
        {
            throw new DomainException(5337, "不能删除网盘根目录");
        }

        var session = await _sessions.OpenAsync(provider, ct);
        // 远端 DELETE 把文件移入网盘自身回收站；本地软删提供 PIM 回收站语义
        await session.Writer.DeleteItemAsync(item.ExternalFileId, ct);
        var now = _clock.GetUtcNow();
        item.IsDeleted = true;
        item.DeletedAt = now;
        // 目录被删时子孙在远端已随父项一起进回收站，本地必须一并软删，
        // 否则树里会留下「父目录已删、子项仍可见」的悬空节点
        await MarkDescendantsDeletedAsync(provider.Id, item.Path, now, ct);
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.delete_to_trash", item.Id, ct);
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
    /// 本地恢复：增量同步语义下「软删即远端已删」，
    /// 因此只有「本地软删但远端仍存在」的短暂窗口可恢复，其余返回明确错误。
    /// </summary>
    public async Task<FileWriteResult> RestoreAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadItemIncludingDeletedAsync(itemId, ct);
        if (!item.IsDeleted)
        {
            throw new DomainException(5339, "该文件不在回收站中");
        }

        var session = await _sessions.OpenAsync(provider, ct);
        if (session.Writer is IRestorableFileItemWriter restorable) await restorable.RestoreItemAsync(item.ExternalFileId, ct);
        if (!await RemoteItemExistsAsync(session, item.ExternalFileId, ct))
        {
            throw new DomainException(5340, "文件已在网盘端删除，无法恢复");
        }

        // 目录的子孙是随父目录一起级联软删的，恢复时必须整树恢复，
        // 否则会出现「父目录可见、子项仍是删除态」的残缺树。
        // 只恢复 DeletedAt 与父目录**完全相同**的子孙：那是同一次级联的标记，
        // 早先被单独删除的子项（DeletedAt 不同）不应被顺带复活。
        // 父目录 DeletedAt 为 null（历史数据）时退回「整棵子树一起恢复」，
        // 而不是静默什么都不做。
        var cascadeDeletedAt = item.DeletedAt;
        var restoredCount = 1;
        if (item.ItemType == "folder")
        {
            restoredCount += await RestoreCascadeDescendantsAsync(
                provider.Id, item.Path, cascadeDeletedAt, session, ct);
        }

        item.IsDeleted = false;
        item.DeletedAt = null;
        item.SyncedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.restore", item.Id, ct);
        _logger?.LogInformation(
            "Files restore: item {ItemId} restored with {Descendants} cascade descendant(s)",
            item.Id, restoredCount - 1);
        return new FileWriteResult(item.Id, item.Path);
    }

    /// <summary>
    /// 恢复随 <paramref name="folderPath"/> 同一次级联删除的子孙。
    ///
    /// 两条规则：
    /// 1. <paramref name="cascadeDeletedAt"/> 有值时只恢复 DeletedAt 精确匹配的子孙；为 null
    ///    （历史数据）时退回整棵子树，避免静默不恢复。
    /// 2. 恢复前逐个校验子孙在远端仍在：远端已删除的项保持删除态，
    ///    否则本地会「复活」一批访问即 404 的幽灵行。
    /// </summary>
    private async Task<int> RestoreCascadeDescendantsAsync(
        Guid providerId,
        string folderPath,
        DateTimeOffset? cascadeDeletedAt,
        IFileProviderSession session,
        CancellationToken ct)
    {
        var prefix = folderPath.TrimEnd('/') + "/";
        var query = _db.Set<FileItemEntity>()
            .Where(row => row.ProviderId == providerId
                && row.Path.StartsWith(prefix)
                && row.IsDeleted);
        if (cascadeDeletedAt is { } cascadeAt)
        {
            query = query.Where(row => row.DeletedAt == cascadeAt);
        }

        var descendants = await query.ToListAsync(ct);
        if (descendants.Count == 0)
        {
            return 0;
        }

        // 目录内的项数量可观时会放大远端调用，但仍以正确性优先：
        // 只有确认远端存在的项才恢复，其余保持删除态。
        var missingRemote = new List<FileItemEntity>();
        foreach (var descendant in descendants)
        {
            if (!await RemoteItemExistsAsync(session, descendant.ExternalFileId, ct))
            {
                missingRemote.Add(descendant);
            }
        }

        var restorable = descendants.Except(missingRemote).ToList();
        var now = _clock.GetUtcNow();
        foreach (var descendant in restorable)
        {
            descendant.IsDeleted = false;
            descendant.DeletedAt = null;
            descendant.SyncedAt = now;
        }

        if (missingRemote.Count > 0)
        {
            _logger?.LogInformation(
                "Files restore: {Missing} of {Total} descendants are gone remotely and stay deleted",
                missingRemote.Count, descendants.Count);
        }

        return restorable.Count;
    }

    /// <summary>远端项是否仍存在。</summary>
    private static async Task<bool> RemoteItemExistsAsync(
        IFileProviderSession session,
        string externalFileId,
        CancellationToken ct)
    {
        try
        {
            if (session.Writer is IRestorableFileItemWriter) return await session.Content.GetItemAsync(externalFileId, ct) is not null;
            return await session.Content.GetDownloadUrlAsync(externalFileId, ct) is not null;
        }
        catch (ProviderItemNotFoundException)
        {
            return false;
        }
    }

    /// <summary>小文件上传：上传到目标路径并立即收敛本地元数据。</summary>
    public async Task<FileWriteResult> UploadAsync(
        Guid providerId,
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
        // 否则超大 multipart 请求会在检查前就把内存吃光。
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > FileContentService.MaxSaveBytes)
            {
                throw new DomainException(
                    5331, $"上传文件超过 {FileContentService.MaxSaveBytes / 1024 / 1024}MB，请使用网盘客户端");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();

        var (provider, session) = await LoadConnectedAccountAsync(providerId, ct);
        var folder = await ResolveFolderRefAsync(provider, session, destinationFolderPath, ct);
        var itemPath = JoinPath(folder.Path, fileName);

        var driveItemId = await session.Writer.CreateFileAsync(folder, fileName, bytes, contentType, ct);

        var now = _clock.GetUtcNow();
        var item = await UpsertItemRowAsync(provider.Id, driveItemId, now, ct);
        item.ParentExternalFileId = folder.ExternalId;
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
        await RecordAuditAsync("files.upload", item.Id, ct);
        return new FileWriteResult(item.Id, itemPath);
    }

    /// <summary>
    /// 创建上传会话（REQ-14）：只向 provider 要一个预授权 uploadUrl，**不接收也不转发任何字节**。
    /// 浏览器拿到后直接分片上传；完成后由 <see cref="RegisterUploadedFileAsync"/> 登记元数据。
    /// </summary>
    public async Task<RemoteUploadSession> CreateUploadSessionAsync(
        Guid providerId,
        string destinationFolderPath,
        string fileName,
        CancellationToken ct = default)
    {
        FileNameValidator.EnsureValidName(fileName);
        var (provider, session) = await LoadConnectedAccountAsync(providerId, ct);
        var folder = await ResolveFolderRefAsync(provider, session, destinationFolderPath, ct);
        return await session.Writer.CreateUploadSessionAsync(folder, fileName, ct);
    }

    /// <summary>
    /// 上传完成后登记元数据（REQ-14）：内容已在远端，这里只把结果落到本地元数据并写审计。
    /// 通过回读真实条目来确认存在与最终名称（重名时 provider 会按自己的策略改名）。
    /// </summary>
    public async Task<FileWriteResult> RegisterUploadedFileAsync(
        Guid providerId,
        string destinationFolderPath,
        string fileName,
        string? uploadedItemId = null,
        CancellationToken ct = default)
    {
        FileNameValidator.EnsureValidName(fileName);
        var (provider, session) = await LoadConnectedAccountAsync(providerId, ct);
        var folder = await ResolveFolderRefAsync(provider, session, destinationFolderPath, ct);

        // 优先按**上传完成响应里的真实条目 id** 回读：重名时 provider 会把文件改名，
        // 此时按原路径回读会读到**本来就存在的那一个旧文件**，把它的元数据登记成本次上传的结果。
        RemoteItem? item;
        if (!string.IsNullOrWhiteSpace(uploadedItemId))
        {
            item = await session.Content.GetItemAsync(uploadedItemId!, ct);
        }
        else
        {
            try
            {
                item = await session.Content.GetItemByPathAsync(JoinPath(folder.Path, fileName), ct);
            }
            catch (ProviderCapabilityNotSupportedException)
            {
                // 按父 id 寻址的 provider（Google Drive）没有「按路径回读」这个动作。
                // 浏览器分片上传的完成响应里带得回条目 id，因此这是调用方的契约问题，
                // 明确报错而不是静默登记错条目。
                throw new DomainException(
                    5300,
                    "上传已完成，但请求未带上条目 id，且该网盘不支持按路径回读，请重新同步后再试");
            }
        }

        if (item is null)
        {
            throw new DomainException(5300, "上传已完成，但未能从网盘读回该文件，请稍后重新同步");
        }

        // 客户端给的 id 必须与服务端回读到的**同一条**：否则可以拿一个已有条目的 id
        // 冒充本次上传结果，把它登记到任意目录（复审 Important）。
        if (!string.IsNullOrWhiteSpace(uploadedItemId)
            && !string.Equals(item.Id, uploadedItemId, StringComparison.Ordinal))
        {
            throw new DomainException(5300, "上传结果与服务端回读不一致，请重新同步后再试");
        }

        // 回读到的父目录必须就是声明的目标目录：两边都知道时才核对（provider 未必都给得出来）。
        // 不核对的话，提交「已有敏感文件的 id + 公开目录路径」就能把敏感文件登记成公开路径，
        // 之后的敏感路径闸门会按伪造路径放行。
        if (!IsInsideFolder(folder, item, session.Account.RootExternalId))
        {
            throw new DomainException(5300, "上传结果不在目标目录内，已拒绝登记");
        }

        // 用服务端返回的真实路径/名称（重名时已自动改名），不沿用客户端提交的名字
        var resolvedPath = item.Path is { Length: > 0 } ? item.Path : JoinPath(folder.Path, item.Name);

        var now = _clock.GetUtcNow();
        var row = await UpsertItemRowAsync(provider.Id, item.Id, now, ct);
        row.ParentExternalFileId = folder.ExternalId;
        row.Path = resolvedPath;
        row.Name = item.Name;
        row.ItemType = item.IsFolder ? "folder" : "file";
        row.MimeType = item.MimeType;
        row.Size = item.Size;
        row.IsDeleted = false;
        row.DeletedAt = null;
        row.LastSeenAt = now;
        row.ModifiedAt = now;
        row.SyncedAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.upload_session_complete", row.Id, ct);
        return new FileWriteResult(row.Id, resolvedPath);
    }

    /// <summary>
    /// 在当前目录内新建文件夹（REQ-15）。先校验名称，再经 provider 创建，
    /// 成功后按**服务端回读结果**收敛本地元数据（保证远端与 PIM 元数据一致）。
    ///
    /// 为什么必须回读而不是直接用输入名：重名时 provider 会自动改名，
    /// 输入名与实际创建的条目**不是同一个东西**；沿用输入名会让 PIM 显示的名字与远端不一致，
    /// 直到下一轮同步才纠正。这与 <see cref="RegisterUploadedFileAsync"/> 是同一模式。
    /// </summary>
    public async Task<FileWriteResult> CreateFolderAsync(
        Guid providerId,
        string path,
        CancellationToken ct = default)
    {
        var (folderPath, name) = FileNameValidator.SplitTargetPath(path);
        var (provider, session) = await LoadConnectedAccountAsync(providerId, ct);
        var folder = await ResolveFolderRefAsync(provider, session, folderPath, ct);

        var createdId = await session.Writer.CreateFolderAsync(folder, name, ct);
        var created = await session.Content.GetItemAsync(createdId, ct)
            ?? throw new DomainException(5300, "文件夹已创建，但未能从网盘读回该文件夹，请稍后重新同步");

        if (!IsInsideFolder(folder, created, session.Account.RootExternalId))
        {
            throw new DomainException(5300, "新建目录不在目标目录内，已拒绝登记");
        }

        var resolvedPath = created.Path is { Length: > 0 } ? created.Path : JoinPath(folder.Path, created.Name);

        var now = _clock.GetUtcNow();
        var item = await UpsertItemRowAsync(provider.Id, created.Id, now, ct);
        item.ParentExternalFileId = folder.ExternalId;
        item.Path = resolvedPath;
        item.Name = created.Name;
        item.ItemType = "folder";
        item.MimeType = null;
        item.Size = null;
        item.IsDeleted = false;
        item.DeletedAt = null;
        item.LastSeenAt = now;
        item.ModifiedAt = now;
        item.SyncedAt = now;
        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("files.create_folder", item.Id, ct);
        return new FileWriteResult(item.Id, resolvedPath);
    }

    /// <summary>条目在 provider 网页端的地址。</summary>
    public async Task<string> GetWebUrlAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadConnectedItemAsync(itemId, ct);
        // webUrl 是内容出口：敏感路径必须与其他出口（content/thumbnail/preview/text/
        // read_file_text）同样拦截，否则 /Secrets/* 可以借 open-link 拿到直通链接。
        EnsureNotSensitive(item);
        var session = await _sessions.OpenAsync(provider, ct);
        var webUrl = await session.Content.GetWebUrlAsync(item.ExternalFileId, ct);
        return webUrl ?? throw new DomainException(5333, "网盘暂未返回网页地址，请稍后重试");
    }

    private void EnsureNotSensitive(FileItemEntity item)
    {
        if (_sensitivePolicy.IsProtected(item.Path))
        {
            throw new DomainException(40303, "敏感路径受保护，不允许该操作");
        }
    }

    /// <summary>按账号 id 打开一个已连接的账号会话（写入口用）。</summary>
    private async Task<(FileProviderEntity Provider, IFileProviderSession Session)> LoadConnectedAccountAsync(
        Guid providerId,
        CancellationToken ct)
    {
        // 不属于当前用户（或不存在）时按 5104「文件来源不存在」处理 → 404。
        // 这里**不能**用 5320：它映射成 400，会把「这个 id 存在但不属于你」与
        // 「你给的参数有问题」混为一谈（既有 E2E 用例 Upload_WithForeignProviderId_IsRejected
        // 锁定的就是 404，避免泄漏他人资源是否存在）。
        var provider = await _db.Set<FileProviderEntity>()
            .Include(row => row.Items)
            .SingleOrDefaultAsync(row => row.Id == providerId && row.UserId == UserId, ct)
            ?? throw new DomainException(5104, "文件来源不存在");
        if (!_providers.IsSupported(provider.Provider))
        {
            throw new DomainException(5323, $"不支持的文件来源类型：{provider.Provider}");
        }

        EnsureUsable(provider);

        var session = await _sessions.OpenAsync(provider, ct);
        return (provider, session);
    }

    /// <summary>
    /// 账号存在但不可用时给出**能自愈**的错误：授权失效要说「请重新绑定」，
    /// 而不是笼统的「文件不存在」——后者会让用户完全找不到恢复路径（复审 Minor）。
    /// </summary>
    private static void EnsureUsable(FileProviderEntity provider)
    {
        if (provider.Status == "connected")
        {
            return;
        }

        throw provider.Status == "expired"
            ? new DomainException(5322, "网盘授权已失效，请到设置页重新连接该账号")
            : new DomainException(5321, "文件来源尚未完成绑定");
    }

    private static string JoinPath(string folderPath, string name)
        => folderPath == "/" ? "/" + name : folderPath.TrimEnd('/') + "/" + name;

    /// <summary>
    /// 回读到的条目是否真的在 <paramref name="folder"/> 之下。
    ///
    /// 非根目录：父 id 两边都知道时要求相等。
    /// **根目录不能直接跳过检查**（那会重新打开伪造入口）：盘根的 id 是别名
    /// （Graph 认 <c>"root"</c>、回读给的却是真实盘根 id），严格比较会误伤正常上传，
    /// 所以改用两条可验证的等价判据 ——
    /// (a) 回读到的父 id 就是该账号的盘根 id；或
    /// (b) 回读到的**自身路径**恰好是 <c>/&lt;它自己的名字&gt;</c>（说明它确实在根下）。
    /// 两条都不成立就拒绝：否则可以拿任意已有（可能是敏感）文件的 id 谎称「刚上传到根目录」，
    /// 把它登记成公开路径，之后所有按路径判断的敏感保护都会放行（复审 Important）。
    /// </summary>
    private static bool IsInsideFolder(RemoteFolderRef folder, RemoteItem item, string rootExternalId)
    {
        var itemParent = item.ParentExternalId;
        if (folder.Path != "/")
        {
            return folder.ExternalId is not { Length: > 0 }
                || itemParent is not { Length: > 0 }
                || string.Equals(itemParent, folder.ExternalId, StringComparison.Ordinal);
        }

        if (itemParent is { Length: > 0 }
            && string.Equals(itemParent, rootExternalId, StringComparison.Ordinal))
        {
            return true;
        }

        return item.Path is { Length: > 0 } path
            && string.Equals(path, "/" + item.Name, StringComparison.Ordinal);
    }

    private async Task<FileItemEntity> UpsertItemRowAsync(
        Guid providerId,
        string externalFileId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var item = await _db.Set<FileItemEntity>()
            .SingleOrDefaultAsync(row => row.ProviderId == providerId && row.ExternalFileId == externalFileId, ct);
        if (item is not null)
        {
            return item;
        }

        item = new FileItemEntity
        {
            ProviderId = providerId,
            ExternalFileId = externalFileId,
            CreatedAt = now,
        };
        _db.Set<FileItemEntity>().Add(item);
        return item;
    }

    private async Task<(FileItemEntity Item, FileProviderEntity Provider)> LoadConnectedItemAsync(
        Guid itemId,
        CancellationToken ct)
    {
        var (item, provider) = await LoadItemIncludingDeletedAsync(itemId, ct);
        if (item.IsDeleted)
        {
            throw new DomainException(5104, "文件不存在");
        }

        return (item, provider);
    }

    private async Task<(FileItemEntity Item, FileProviderEntity Provider)> LoadItemIncludingDeletedAsync(
        Guid itemId,
        CancellationToken ct)
    {
        var item = await _db.Set<FileItemEntity>()
            .Include(row => row.Provider)
            .SingleOrDefaultAsync(row => row.Id == itemId, ct)
            ?? throw new DomainException(5104, "文件不存在");
        if (item.Provider is null
            || item.Provider.UserId != UserId
            || !_providers.IsSupported(item.Provider.Provider))
        {
            throw new DomainException(5104, "文件不存在");
        }

        if (item.Provider.Status != "connected")
        {
            throw item.Provider.Status == "expired"
                ? new DomainException(5322, "网盘授权已失效，请到设置页重新连接该账号")
                : new DomainException(5321, "文件来源尚未完成绑定");
        }

        return (item, item.Provider);
    }

    /// <summary>
    /// 把目标目录解析成 <see cref="RemoteFolderRef"/>（外部 id + PIM 路径两套坐标）。
    /// 目录必须已同步过：写操作不做「隐式建目录」，否则用户看到的目录树与实际不一致。
    /// 索引里还没有根行时用 provider 会话给出的根 id 兜底（不硬编码 Graph 的 <c>"root"</c>）。
    /// </summary>
    private async Task<RemoteFolderRef> ResolveFolderRefAsync(
        FileProviderEntity provider,
        IFileProviderSession session,
        string folderPath,
        CancellationToken ct)
    {
        var normalized = "/" + folderPath.Trim().Trim('/');
        if (normalized == "/")
        {
            var rootRow = await _db.Set<FileItemEntity>()
                .AsNoTracking()
                .Where(item => item.ProviderId == provider.Id && item.Path == "/")
                .Select(item => item.ExternalFileId)
                .FirstOrDefaultAsync(ct);
            return new RemoteFolderRef(rootRow ?? session.Account.RootExternalId, "/");
        }

        var existing = await _db.Set<FileItemEntity>()
            .AsNoTracking()
            .Where(item => item.ProviderId == provider.Id
                && item.ItemType == "folder"
                && (item.Path == normalized || item.Path == normalized.TrimEnd('/')))
            .Select(item => new { item.ExternalFileId, item.Path })
            .FirstOrDefaultAsync(ct);
        return existing is null
            ? throw new DomainException(5304, "目标文件夹不存在（未同步或已删除）")
            : new RemoteFolderRef(existing.ExternalFileId, existing.Path);
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
