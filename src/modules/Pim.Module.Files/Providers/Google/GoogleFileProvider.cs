using System.Collections.Concurrent;
using Pim.Core.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Providers.Google;

/// <summary>
/// 盘根真实 id 的进程内缓存（单例）。
///
/// 为什么需要：Drive v3 里 <c>"root"</c> 只是个别名，而 <c>parents</c> 数组里给的是
/// **真实 id**。同步引擎要靠「父 id == 根 id」把顶层条目落成 <c>/name</c>，
/// 因此必须知道真实 id；而每次会话都去查一次是纯浪费，这里按账号缓存。
/// </summary>
public sealed class GoogleRootIdCache
{
    private readonly ConcurrentDictionary<Guid, string> _rootIds = new();

    public bool TryGet(Guid providerRowId, out string rootId)
    {
        if (_rootIds.TryGetValue(providerRowId, out var cached))
        {
            rootId = cached;
            return true;
        }

        rootId = string.Empty;
        return false;
    }

    public void Set(Guid providerRowId, string rootId) => _rootIds[providerRowId] = rootId;

    public void Invalidate(Guid providerRowId) => _rootIds.TryRemove(providerRowId, out _);
}

/// <summary>
/// Google Drive 的通用抽象实现（工单 05 / F4）。
/// </summary>
public sealed class GoogleFileProvider(IServiceProvider services) : IFileProvider
{
    public const string Key = "google";

    /// <summary>Drive 里根目录的固定别名。</summary>
    public const string RootId = "root";

    public string ProviderKey => Key;

    public FileProviderAuthorizationMode AuthorizationMode => FileProviderAuthorizationMode.BrowserRedirect;

    public bool SupportsIncrementalSync => true;

    public bool SupportsSharing => true;

    public async Task<IFileProviderSession> OpenSessionAsync(
        FileProviderContext context,
        CancellationToken ct = default)
    {
        var client = services.GetRequiredService<GoogleDriveClient>();
        var rootCache = services.GetRequiredService<GoogleRootIdCache>();

        // 解析真实根 id（缓存优先）。失败时退回别名 "root"：
        // 后续按别名寻址的调用仍然可用，只是顶层条目的父 id 匹配会退化到「按索引解析」。
        if (!rootCache.TryGet(context.ProviderRowId, out var rootId))
        {
            var token = await context.AccessToken(ct);
            var root = await client.GetFileAsync(token, RootId, ct);
            rootId = root?.Id ?? RootId;
            rootCache.Set(context.ProviderRowId, rootId);
        }

        return new GoogleSession(services, context, rootId);
    }

    private sealed class GoogleSession(
        IServiceProvider services,
        FileProviderContext context,
        string rootId) : IFileProviderSession
    {
        public FileProviderAccount Account { get; } = new(
            Key,
            context.AccountId ?? string.Empty,
            null,
            context.DriveId,
            rootId);

        public IFileContentReader Content { get; } = new GoogleContentReader(services, context, rootId);

        public IFileItemWriter Writer { get; } = new GoogleItemWriter(services, context, rootId);

        public IFileShareManager? Shares { get; } = new GoogleShareManager(services, context);

        public IIncrementalChangeSource? Changes { get; } = new GoogleChangeSource(services, context, rootId);
    }
}

/// <summary>
/// Google 的增量变更流（对标 OneDrive 的 delta，工单 F4.3）。
///
/// 游标是不透明字符串，三种形态：
/// <list type="bullet">
/// <item><c>full:&lt;startToken&gt;:&lt;pageToken&gt;</c> —— 首次/重置后的**全量**扫描中
/// （Drive 的 changes 流没有「从头开始」，首次必须用 files.list 把整棵树走一遍）；</item>
/// <item><c>full:&lt;startToken&gt;:</c> —— 整棵树已列完，转入增量；</item>
/// <item><c>delta:&lt;pageToken&gt;</c> —— 增量中，走 changes.list。</item>
/// </list>
///
/// <b>startToken 必须在开始遍历文件树之前取</b>：如果等遍历完再取，扫描期间发生的
/// 改名/删除/更新落在 token 之前，本地留下的是旧状态，而后续增量也不会再推送它们
/// —— 这是**永久**漏变更（复审 Important）。先冻结 token，则扫描期间的变化会在
/// 下一轮增量里重新送达（重复投递是幂等的）。
///
/// pageToken 失效（过期 / invalid_page_token）抛 <see cref="ProviderCursorExpiredException"/>，
/// 由同步引擎重置为全量重扫 —— 这与「refresh token 失效」是两回事（F4.5 明确要求分开）。
/// </summary>
public sealed class GoogleChangeSource(
    IServiceProvider services,
    FileProviderContext context,
    string rootId) : IIncrementalChangeSource
{
    private const string FullPrefix = "full:";
    private const string DeltaPrefix = "delta:";

    /// <summary>全量游标：<c>full:&lt;开始遍历前冻结的 startToken&gt;:&lt;当前 files.list 分页 token&gt;</c>。</summary>
    private static string FullCursor(string startToken, string? pageToken)
        => $"{FullPrefix}{startToken}:{pageToken ?? string.Empty}";

    private static (string StartToken, string? PageToken) ParseFullCursor(string cursor)
    {
        var body = cursor[FullPrefix.Length..];
        var separator = body.IndexOf(':');
        if (separator < 0)
        {
            // 老格式（没有冻结 token）：按「无 startToken」处理，走一次冻结
            return (string.Empty, body.Length == 0 ? null : body);
        }

        var startToken = body[..separator];
        var page = body[(separator + 1)..];
        return (startToken, page.Length == 0 ? null : page);
    }

    private readonly GoogleDriveClient _client = services.GetRequiredService<GoogleDriveClient>();
    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    public async Task<FileChangePage> GetChangesPageAsync(string? cursor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.StartsWith(FullPrefix, StringComparison.Ordinal))
        {
            var (startToken, pageToken) = string.IsNullOrEmpty(cursor)
                ? (string.Empty, (string?)null)
                : ParseFullCursor(cursor);
            return await FullCrawlPageAsync(startToken, pageToken, ct);
        }

        if (cursor.StartsWith(DeltaPrefix, StringComparison.Ordinal))
        {
            return await DeltaPageAsync(cursor[DeltaPrefix.Length..], ct);
        }

        // 认不出的游标（例如手改过数据库）：按失效处理，让引擎重扫，而不是拿它去请求
        throw new ProviderCursorExpiredException($"无法识别的同步游标: {cursor}");
    }

    /// <summary>
    /// 全量扫描的一页：列出全部未删除条目。
    /// 第一页会**先**冻结 startToken（见类型注释），最后一页转入 <c>delta:&lt;startToken&gt;</c>。
    /// </summary>
    private async Task<FileChangePage> FullCrawlPageAsync(
        string startToken,
        string? pageToken,
        CancellationToken ct)
    {
        var token = await _accessToken(ct);
        if (startToken.Length == 0)
        {
            startToken = await _client.GetStartPageTokenAsync(token, ct);
        }

        var page = await _client.ListFilesPageAsync(token, pageToken, "trashed=false", ct);
        var changes = page.Files.Select(ToFileChange).ToList();

        if (page.NextPageToken is not null)
        {
            return new FileChangePage(changes, FullCursor(startToken, page.NextPageToken), HasMore: true);
        }

        // 整棵树已列完：交付**遍历前**冻结的那个 token。
        // 下一轮增量会重放遍历期间的变化（幂等），因此不会永久漏项。
        return new FileChangePage(changes, DeltaPrefix + startToken, HasMore: false);
    }

    private async Task<FileChangePage> DeltaPageAsync(string pageToken, CancellationToken ct)
    {
        var token = await _accessToken(ct);
        var page = await _client.ListChangesPageAsync(token, pageToken, ct);
        var changes = page.Changes.Select(ToFileChange).ToList();

        if (page.NextPageToken is not null)
        {
            return new FileChangePage(changes, DeltaPrefix + page.NextPageToken, HasMore: true);
        }

        // 最后一页必须换成 newStartPageToken：nextPageToken 是一次性的分页游标，
        // 下次拿它请求会被拒（这是 Drive 的既定语义，不是可选项）。
        var next = page.NewStartPageToken;
        return new FileChangePage(changes, next is null ? null : DeltaPrefix + next, HasMore: false);
    }

    private FileChange ToFileChange(GoogleChangeEntry entry)
    {
        if (entry.Removed || entry.File is null)
        {
            // 删除标记只带 fileId；名字/父/大小信息一律没有
            return new FileChange(
                entry.FileId, null, null, string.Empty, false, true, null, null, null, DateTimeOffset.UnixEpoch);
        }

        return ToFileChange(entry.File);
    }

    private FileChange ToFileChange(GoogleDriveFile file)
    {
        var parent = file.Parents.Count > 0 ? file.Parents[0] : null;
        if (file.Trashed)
        {
            return new FileChange(
                file.Id, null, null, file.Name, false, true, null, null, null, DateTimeOffset.UnixEpoch);
        }

        // Drive 只给父 id，不给父路径 —— ParentPath 留 null，由同步引擎按索引解析。
        // 这正是 <see cref="FileChange.ParentPath"/> 被设计成可选的原因。
        return new FileChange(
            file.Id,
            parent,
            null,
            file.Name,
            string.Equals(file.MimeType, GoogleDriveClient.FolderMimeType, StringComparison.Ordinal),
            IsRemoved: false,
            file.Size,
            file.MimeType,
            file.Md5,
            file.ModifiedAt ?? DateTimeOffset.UnixEpoch);
    }
}

/// <summary>Google 读取能力。</summary>
public sealed class GoogleContentReader(
    IServiceProvider services,
    FileProviderContext context,
    string rootId) : IFileContentReader
{
    private readonly GoogleDriveClient _client = services.GetRequiredService<GoogleDriveClient>();
    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    /// <summary>
    /// 直链下载。二进制文件用 Drive 的 <c>webContentLink</c>（已预授权）；
    /// **Google 原生文档（Docs/Sheets/Slides）没有直链**（Drive 自身的设计：必须导出），
    /// 此时返回 null，调用方给出「暂未返回直链」的明确错误。
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var file = await _client.GetFileAsync(token, itemId, ct);
        if (file is null || GoogleDriveClient.IsGoogleNativeDocument(file.MimeType))
        {
            return null;
        }

        return file.WebContentLink;
    }

    /// <summary>缩略图。Drive 的 thumbnailLink 是短时效链接；<paramref name="size"/> 由 Drive 决定，不透传。</summary>
    public async Task<string?> GetThumbnailUrlAsync(string itemId, string size, CancellationToken ct = default)
    {
        _ = size;
        var token = await _accessToken(ct);
        var file = await _client.GetFileAsync(token, itemId, ct);
        return file?.ThumbnailLink;
    }

    /// <summary>网页预览：Drive 官方 embed 地址。</summary>
    public async Task<string?> GetPreviewUrlAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var file = await _client.GetFileAsync(token, itemId, ct);
        if (file is null)
        {
            return null;
        }

        return $"https://drive.google.com/file/d/{Uri.EscapeDataString(file.Id)}/preview";
    }

    public async Task<string?> GetWebUrlAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var file = await _client.GetFileAsync(token, itemId, ct);
        return file?.WebViewLink;
    }

    public async Task<RemoteContent?> DownloadSmallAsync(
        string itemId,
        long maxBytes,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var file = await _client.GetFileAsync(token, itemId, ct);
        if (file is null)
        {
            return null;
        }

        // 原生文档走 export（text/plain），其余走 alt=media —— 分流在客户端里
        var bytes = await _client.DownloadAsync(token, file.Id, file.MimeType, maxBytes, ct);
        var contentType = GoogleDriveClient.IsGoogleNativeDocument(file.MimeType)
            ? "text/plain"
            : file.MimeType;
        return new RemoteContent(bytes, contentType);
    }

    public async Task<RemoteItem?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var file = await _client.GetFileAsync(token, itemId, ct);
        if (file is null)
        {
            return null;
        }

        // Drive 给不出路径，只有父 id；Path 留 null 让引擎按索引解析。
        // 根目录例外：它就是 "/"。
        var path = string.Equals(file.Id, rootId, StringComparison.Ordinal) ? "/" : null;
        return new RemoteItem(
            file.Id,
            file.Name,
            file.Size,
            file.MimeType,
            string.Equals(file.MimeType, GoogleDriveClient.FolderMimeType, StringComparison.Ordinal),
            path,
            file.Md5,
            file.Parents.Count > 0 ? file.Parents[0] : null);
    }

    /// <summary>
    /// Drive v3 按父 id 寻址，没有「按路径取条目」这个动作；
    /// 要做就得自己把路径逐段解析成 id，那是调用方（有索引）该干的事。
    /// </summary>
    public Task<RemoteItem?> GetItemByPathAsync(string path, CancellationToken ct = default)
        => throw new ProviderCapabilityNotSupportedException("lookup by path", GoogleFileProvider.Key);
}

/// <summary>Google 写入能力。</summary>
public sealed class GoogleItemWriter(
    IServiceProvider services,
    FileProviderContext context,
    string rootId) : IFileItemWriter
{
    private readonly GoogleDriveClient _client = services.GetRequiredService<GoogleDriveClient>();
    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    public async Task PutContentAsync(
        string itemId,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        await _client.UpdateFileContentAsync(token, itemId, bytes, contentType, ct);
    }

    public async Task<string> CreateFileAsync(
        RemoteFolderRef parent,
        string name,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var parentId = await ResolveParentIdAsync(token, parent, ct);
        var created = await _client.UploadNewFileAsync(token, parentId, name, bytes, contentType, ct);
        return created.Id;
    }

    /// <summary>
    /// 改名/移动。Drive 的 <c>addParents</c> **不会自动移除旧父**：只加不删会让文件同时挂在
    /// 两个目录下（Drive 允许多父），表现为「移动后旧位置还在」。因此必须先读当前父再 remove。
    /// </summary>
    public async Task<string> PatchItemAsync(
        string itemId,
        string? newName,
        string? newParentExternalId,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        string? removeParentId = null;
        if (newParentExternalId is not null)
        {
            var current = await _client.GetFileAsync(token, itemId, ct);
            removeParentId = current?.Parents.FirstOrDefault();
        }

        var updated = await _client.PatchFileAsync(
            token, itemId, newName, newParentExternalId, removeParentId, ct);
        return updated.Id;
    }

    public async Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        await _client.DeleteFileAsync(token, itemId, ct);
    }

    public async Task<string> CreateFolderAsync(
        RemoteFolderRef parent,
        string name,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var parentId = await ResolveParentIdAsync(token, parent, ct);
        await _client.CreateFolderAsync(token, parentId, name, ct);

        // Drive 的 files.create 不回条目体（我们只请求了 fields=id 之外的东西也未必可靠）：
        // 直接按「父 + 名字」回查刚建的那个目录。重名时 Drive 允许同名，
        // 因此取最新修改的那个（同一毫秒内建两个同名目录是理论极端，这里退化为任意一个，
        // 下一轮同步会以服务端事实为准纠正）。
        var page = await _client.ListFilesPageAsync(
            token,
            null,
            $"'{EscapeQueryValue(parentId ?? rootId)}' in parents and name = '{EscapeQueryValue(name)}' and mimeType = '{GoogleDriveClient.FolderMimeType}' and trashed = false",
            ct);
        var match = page.Files.OrderByDescending(file => file.ModifiedAt ?? DateTimeOffset.MinValue).FirstOrDefault()
            ?? throw new Pim.Core.Exceptions.DomainException(
                5300, "文件夹已创建，但未能从网盘读回该文件夹，请稍后重新同步");
        return match.Id;
    }

    public async Task<RemoteUploadSession> CreateUploadSessionAsync(
        RemoteFolderRef parent,
        string fileName,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var parentId = await ResolveParentIdAsync(token, parent, ct);
        var sessionUri = await _client.CreateResumableUploadSessionAsync(
            token, parentId, fileName, "application/octet-stream", ct);

        // Drive 的 resumable session 有效期约一周且不随响应返回，因此这里如实给 null，
        // 不编一个假时间。
        return new RemoteUploadSession(sessionUri, null);
    }

    /// <summary>
    /// 把父目录坐标解析成 Drive 的父 id。
    ///
    /// 文件树内的写入永远带 id（调用方从本地索引取）；**只有附件这类「不在同步树里」的路径**
    /// 是「有路径、无 id」（例如 <c>/PIM/quick-notes/...</c>，它不属于用户同步的文件树，
    /// 因此索引里没有对应行）。这种情形下按路径逐段解析，缺目录就建 ——
    /// 与 Graph 按路径上传时自动补建父目录的行为对齐。
    /// </summary>
    private async Task<string?> ResolveParentIdAsync(
        string token,
        RemoteFolderRef parent,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(parent.ExternalId))
        {
            return parent.ExternalId;
        }

        var segments = parent.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentId = rootId;
        foreach (var segment in segments)
        {
            currentId = await ResolveOrCreateChildFolderAsync(token, currentId, segment, ct);
        }

        return currentId;
    }

    private async Task<string> ResolveOrCreateChildFolderAsync(
        string token,
        string parentId,
        string name,
        CancellationToken ct)
    {
        var cacheKey = $"{parentId}/{name}";
        if (_folderIdCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var page = await _client.ListFilesPageAsync(
            token,
            null,
            $"'{EscapeQueryValue(parentId)}' in parents and name = '{EscapeQueryValue(name)}' "
            + $"and mimeType = '{GoogleDriveClient.FolderMimeType}' and trashed = false",
            ct);
        var existing = page.Files.FirstOrDefault();
        if (existing is not null)
        {
            _folderIdCache[cacheKey] = existing.Id;
            return existing.Id;
        }

        await _client.CreateFolderAsync(token, parentId, name, ct);
        var created = await _client.ListFilesPageAsync(
            token,
            null,
            $"'{EscapeQueryValue(parentId)}' in parents and name = '{EscapeQueryValue(name)}' "
            + $"and mimeType = '{GoogleDriveClient.FolderMimeType}' and trashed = false",
            ct);
        var match = created.Files
            .OrderByDescending(file => file.ModifiedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault()
            ?? throw new Pim.Core.Exceptions.DomainException(
                5300, $"网盘上无法建立目录 {name}，请稍后重试");

        _folderIdCache[cacheKey] = match.Id;
        return match.Id;
    }

    private readonly Dictionary<string, string> _folderIdCache = new(StringComparer.Ordinal);

    /// <summary>Drive 查询语法里的单引号转义（名字里带 <c>'</c> 会破坏 q 表达式）。</summary>
    private static string EscapeQueryValue(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'");
}

/// <summary>Google 分享能力（anyone 链接分享）。</summary>
public sealed class GoogleShareManager(
    IServiceProvider services,
    FileProviderContext context) : IFileShareManager
{
    private readonly GoogleDriveClient _client = services.GetRequiredService<GoogleDriveClient>();
    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    /// <summary>
    /// 建「任何人可访问」的链接分享。
    ///
    /// **不支持有效期**：Drive 的 anyone 权限没有过期时间。调用方要求有效期时这里
    /// 明确报错，而不是回一个「看起来设了过期」的链接（静默失效比报错更糟）。
    /// </summary>
    public async Task<RemoteShareLink> CreateShareLinkAsync(
        string itemId,
        FileSharePermission permission,
        DateTimeOffset? expiration,
        CancellationToken ct = default)
    {
        if (expiration is not null)
        {
            throw new DomainException(
                5344, "Google Drive 的链接分享不支持有效期，请选择「不过期」；OneDrive 账号仍可选 7/30 天");
        }

        var token = await _accessToken(ct);
        var role = permission == FileSharePermission.Edit ? "writer" : "reader";
        var created = await _client.CreateAnyonePermissionAsync(token, itemId, role, ct);
        var file = await _client.GetFileAsync(token, itemId, ct);

        return new RemoteShareLink(
            file?.WebViewLink ?? string.Empty,
            created.Type,
            created.Id,
            null);
    }

    public async Task RevokeSharePermissionAsync(
        string itemId,
        string permissionId,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        await _client.RevokePermissionAsync(token, itemId, permissionId, ct);
    }

    public async Task<IReadOnlyList<RemoteShareLink>> ListSharePermissionsAsync(
        string itemId,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var permissions = await _client.ListPermissionsAsync(token, itemId, ct);
        var file = await _client.GetFileAsync(token, itemId, ct);

        return permissions
            .Select(permission => new RemoteShareLink(
                // 只有 anyone 权限才对应「链接分享」的地址；具名用户的权限没有链接可给，
                // 返回空串而不是拿 webViewLink 冒充。
                string.Equals(permission.Type, "anyone", StringComparison.OrdinalIgnoreCase)
                    ? file?.WebViewLink ?? string.Empty
                    : string.Empty,
                permission.Type,
                permission.Id,
                null))
            .ToList();
    }
}
