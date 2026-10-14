using Microsoft.Extensions.DependencyInjection;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Providers;

/// <summary>
/// 把既有 Graph 语义翻译成通用抽象的口径（工单 05 / F1）。
///
/// 这里是「抽象必须容纳 provider 差异」的落点之一：Graph 的
/// <c>/drive</c> / <c>/drive/root:/A/B</c> 前缀路径、没有父目录的根条目、
/// <c>@removed</c> 标记、ctag、410 游标失效、429 Retry-After，
/// 全部在这一层被吃掉，消费方只看到 <see cref="FileChange"/> 与归一化异常。
/// </summary>
public static class OneDriveGraphSemantics
{
    /// <summary>Graph 上「盘根」的固定 id 别名。</summary>
    public const string RootExternalId = "root";

    private const string RootParentPath = "/drive";
    private const string RootParentPathMarker = "/drive/root:";

    /// <summary>
    /// 把 Graph 的 <c>parentReference.path</c> 归一化成 PIM 绝对路径。
    /// 返回值语义（与既有 <c>OneDriveSyncService.DerivePath</c> 完全一致，不得回归）：
    /// <list type="bullet">
    /// <item><c>null</c> / <c>/drive</c> → 该条目**就是根目录自身**（调用方据此把 Path 记为 <c>/</c>）。</item>
    /// <item><c>/drive/root:</c> → 挂在根下的父目录，归一化为 <c>/</c>。</item>
    /// <item><c>/drive/root:/A/B</c> → <c>/A/B</c>。</item>
    /// </list>
    /// </summary>
    public static string? NormalizeParentPath(string? graphParentPath)
    {
        if (graphParentPath is null || graphParentPath == RootParentPath)
        {
            return null;
        }

        if (graphParentPath.StartsWith(RootParentPathMarker, StringComparison.Ordinal))
        {
            var relative = graphParentPath[RootParentPathMarker.Length..];
            return relative.Length == 0 ? "/" : relative;
        }

        return graphParentPath;
    }

    /// <summary>归一化一条 delta 条目。</summary>
    public static FileChange ToFileChange(OneDriveDeltaItem item)
    {
        var parentPath = NormalizeParentPath(item.ParentPath);
        // parentPath 为 null 只发生在根条目自身；此时 ParentExternalId 也必须为 null，
        // 否则同步引擎会去本地找「id 为 null 的父目录」。Graph 的根条目本来就没有 parentReference。
        var parentId = parentPath is null ? null : item.ParentId;
        return new FileChange(
            item.Id,
            parentId,
            parentPath,
            item.Name,
            item.IsFolder,
            item.IsRemoved,
            item.Size,
            item.MimeType,
            item.Ctag,
            item.ModifiedAt);
    }
}

/// <summary>把 Graph 客户端异常翻译成通用抽象异常。</summary>
public static class OneDriveExceptionMapper
{
    /// <summary>
    /// 统一的异常翻译。<paramref name="notFoundAsNull"/> 为 true 时把 404 翻译成
    /// <see cref="ProviderItemNotFoundException"/>（调用方按「不存在」处理）；否则 404 也走同一异常，
    /// 由调用方决定是否吞掉。
    /// </summary>
    public static Exception Translate(OneDriveGraphException exception)
        => exception.StatusCode switch
        {
            (int)System.Net.HttpStatusCode.Gone => new ProviderCursorExpiredException(exception.Message),
            (int)System.Net.HttpStatusCode.Unauthorized => new ProviderUnauthorizedException(exception.Message),
            (int)System.Net.HttpStatusCode.NotFound => new ProviderItemNotFoundException(exception.Message),
            (int)System.Net.HttpStatusCode.TooManyRequests =>
                new ProviderTransientException(exception.StatusCode, exception.RetryAfterSeconds, exception.Message),
            >= 500 => new ProviderTransientException(exception.StatusCode, exception.RetryAfterSeconds, exception.Message),
            _ => exception,
        };

    /// <summary>把可能抛 <see cref="OneDriveGraphException"/> 的调用包成归一化语义。</summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (OneDriveGraphException exception)
        {
            throw Translate(exception);
        }
        catch (OneDriveContentTooLargeException exception)
        {
            throw new ProviderContentTooLargeException(exception.ActualBytes);
        }
    }
}

/// <summary>
/// OneDrive 的通用抽象实现：**只做翻译，不复制业务**。
/// 底层仍是既有、已被大量测试与真机验证过的 <see cref="IOneDriveGraphClient"/>，
/// 这样 F1.4「OneDrive 既有功能不得回退」的回归面最小。
/// </summary>
public sealed class OneDriveFileProvider(IServiceProvider services) : IFileProvider
{
    public const string Key = "onedrive";

    public string ProviderKey => Key;

    public FileProviderAuthorizationMode AuthorizationMode => FileProviderAuthorizationMode.DeviceCode;

    public bool SupportsIncrementalSync => true;

    public bool SupportsSharing => true;

    public Task<IFileProviderSession> OpenSessionAsync(
        FileProviderContext context,
        CancellationToken ct = default)
    {
        IFileProviderSession session = new OneDriveSession(services, context);
        return Task.FromResult(session);
    }

    private sealed class OneDriveSession(IServiceProvider services, FileProviderContext context) : IFileProviderSession
    {
        public FileProviderAccount Account { get; } = new(
            Key,
            context.AccountId ?? string.Empty,
            null,
            context.DriveId,
            OneDriveGraphSemantics.RootExternalId);

        public IFileContentReader Content { get; } = new OneDriveContentReader(services, context);

        public IFileItemWriter Writer { get; } = new OneDriveItemWriter(services, context);

        public IFileShareManager? Shares { get; } = new OneDriveShareManager(services, context);

        public IIncrementalChangeSource? Changes { get; } = new OneDriveChangeSource(services, context);
    }
}

/// <summary>OneDrive 增量变更流：游标即 Graph 的 deltaLink / nextLink 完整 URL。</summary>
public sealed class OneDriveChangeSource(IServiceProvider services, FileProviderContext context) : IIncrementalChangeSource
{
    /// <summary>首次同步的起点（<c>$top=200</c> 与既有实现一致）。</summary>
    public const string DefaultCursor = "https://graph.microsoft.com/v1.0/me/drive/root/delta?$top=200";

    private readonly IOneDriveGraphClient _client =
        services.GetRequiredService<IOneDriveGraphClient>();

    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    public async Task<FileChangePage> GetChangesPageAsync(string? cursor, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(cursor) ? DefaultCursor : cursor!;
        var token = await _accessToken(ct);
        var page = await OneDriveExceptionMapper.GuardAsync(
            () => _client.GetDeltaPageAsync(token, url, ct));

        var changes = page.Items.Select(OneDriveGraphSemantics.ToFileChange).ToList();
        if (page.DeltaLink is not null)
        {
            // 本轮走完：deltaLink 是**要持久化**的游标
            return new FileChangePage(changes, page.DeltaLink, HasMore: false);
        }

        if (page.NextLink is not null)
        {
            // 还有下一页：nextLink 只是本轮的分页游标
            return new FileChangePage(changes, page.NextLink, HasMore: true);
        }

        return new FileChangePage(changes, null, HasMore: false);
    }
}

/// <summary>OneDrive 读取能力。</summary>
public sealed class OneDriveContentReader(IServiceProvider services, FileProviderContext context) : IFileContentReader
{
    private readonly IOneDriveGraphClient _client = services.GetRequiredService<IOneDriveGraphClient>();

    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    public async Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        try
        {
            return await _client.GetDownloadUrlAsync(token, itemId, ct);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<string?> GetThumbnailUrlAsync(string itemId, string size, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        try
        {
            return await _client.GetThumbnailUrlAsync(token, itemId, size, ct);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<string?> GetPreviewUrlAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        try
        {
            return await _client.GetPreviewUrlAsync(token, itemId, ct);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<string?> GetWebUrlAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        try
        {
            return await _client.GetItemWebUrlAsync(token, itemId, ct);
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<RemoteContent?> DownloadSmallAsync(
        string itemId,
        long maxBytes,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var content = await OneDriveExceptionMapper.GuardAsync(
            () => _client.DownloadSmallAsync(token, itemId, maxBytes, ct));
        return content is null ? null : new RemoteContent(content.Bytes, content.ContentType);
    }

    public async Task<RemoteItem?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var item = await OneDriveExceptionMapper.GuardAsync(() => _client.GetItemByIdAsync(token, itemId, ct));
        return item is null ? null : ToRemoteItem(item);
    }

    public async Task<RemoteItem?> GetItemByPathAsync(string path, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var item = await OneDriveExceptionMapper.GuardAsync(() => _client.GetItemByPathAsync(token, path, ct));
        return item is null ? null : ToRemoteItem(item);
    }

    private static RemoteItem ToRemoteItem(OneDrivePathItem item)
    {
        var parentPath = OneDriveGraphSemantics.NormalizeParentPath(item.ParentPath);
        // parentPath 为 null 表示这里回读到的就是盘根自身（Graph 的盘根没有 parentReference）
        var path = parentPath is null
            ? "/"
            : parentPath == "/" ? "/" + item.Name : parentPath + "/" + item.Name;
        return new RemoteItem(
            item.Id,
            item.Name,
            item.Size,
            item.MimeType,
            item.IsFolder,
            path);
    }
}

/// <summary>OneDrive 写入能力。</summary>
public sealed class OneDriveItemWriter(IServiceProvider services, FileProviderContext context) : IFileItemWriter
{
    private readonly IOneDriveGraphClient _client = services.GetRequiredService<IOneDriveGraphClient>();

    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    public async Task PutContentAsync(
        string itemId,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        await OneDriveExceptionMapper.GuardAsync(async () =>
        {
            await _client.PutSmallContentAsync(token, itemId, bytes, contentType, ct);
            return true;
        });
    }

    /// <summary>
    /// Graph 的写入端点按**路径**寻址（<c>/drive/root:/A/B:/content</c>），
    /// 因此这里用 <see cref="RemoteFolderRef.Path"/>；父 id 在 Graph 上等价可用，
    /// 但沿用既有路径写法可以保证「重名自动改名」等行为与既有实现逐字一致（F1.4 不回退）。
    /// </summary>
    public async Task<string> CreateFileAsync(
        RemoteFolderRef parent,
        string name,
        byte[] bytes,
        string contentType,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var path = JoinPath(parent.Path, name);
        return await OneDriveExceptionMapper.GuardAsync(
            () => _client.PutNewFileByPathAsync(token, path, bytes, contentType, ct));
    }

    private static string JoinPath(string folderPath, string name)
        => folderPath == "/" ? "/" + name : folderPath.TrimEnd('/') + "/" + name;

    public async Task<string> PatchItemAsync(
        string itemId,
        string? newName,
        string? newParentExternalId,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        return await OneDriveExceptionMapper.GuardAsync(
            () => _client.PatchItemAsync(token, itemId, newName, newParentExternalId, ct));
    }

    public async Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        // Graph 客户端对 404 已经按「已删除」处理，不报错
        await OneDriveExceptionMapper.GuardAsync(async () =>
        {
            await _client.DeleteItemAsync(token, itemId, ct);
            return true;
        });
    }

    public async Task<string> CreateFolderAsync(
        RemoteFolderRef parent,
        string name,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        return await OneDriveExceptionMapper.GuardAsync(
            () => _client.CreateFolderAsync(token, parent.Path, name, ct));
    }

    public async Task<RemoteUploadSession> CreateUploadSessionAsync(
        RemoteFolderRef parent,
        string fileName,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var targetPath = JoinPath(parent.Path, fileName);
        var session = await OneDriveExceptionMapper.GuardAsync(
            () => _client.CreateUploadSessionAsync(token, targetPath, fileName, ct));
        return new RemoteUploadSession(session.UploadUrl, session.ExpirationDateTime);
    }
}

/// <summary>OneDrive 分享能力。</summary>
public sealed class OneDriveShareManager(IServiceProvider services, FileProviderContext context) : IFileShareManager
{
    private readonly IOneDriveGraphClient _client = services.GetRequiredService<IOneDriveGraphClient>();

    private readonly Func<CancellationToken, Task<string>> _accessToken = context.AccessToken;

    public async Task<RemoteShareLink> CreateShareLinkAsync(
        string itemId,
        FileSharePermission permission,
        DateTimeOffset? expiration,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var link = await OneDriveExceptionMapper.GuardAsync(() => _client.CreateShareLinkAsync(
            token,
            itemId,
            permission == FileSharePermission.Edit ? OneDriveSharePermission.Edit : OneDriveSharePermission.View,
            expiration,
            ct));
        return new RemoteShareLink(link.WebUrl, link.PermissionType, link.PermissionId, link.ExpiresAt);
    }

    public async Task RevokeSharePermissionAsync(
        string itemId,
        string permissionId,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        await OneDriveExceptionMapper.GuardAsync(async () =>
        {
            await _client.RevokeSharePermissionAsync(token, itemId, permissionId, ct);
            return true;
        });
    }

    public async Task<IReadOnlyList<RemoteShareLink>> ListSharePermissionsAsync(
        string itemId,
        CancellationToken ct = default)
    {
        var token = await _accessToken(ct);
        var links = await OneDriveExceptionMapper.GuardAsync(
            () => _client.ListSharePermissionsAsync(token, itemId, ct));
        return links
            .Select(link => new RemoteShareLink(link.WebUrl, link.PermissionType, link.PermissionId, link.ExpiresAt))
            .ToList();
    }
}
