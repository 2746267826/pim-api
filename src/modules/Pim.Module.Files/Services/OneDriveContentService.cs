using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

public sealed record OneDriveTextContent(string Content, string? MimeType, long Size, bool Truncated);

public sealed record FileTextSnapshotDto(
    Guid Id,
    string Path,
    string Name,
    string Content,
    int ByteSize,
    string Reason,
    DateTimeOffset CreatedAt);

/// <summary>
/// OneDrive 内容出口（设计文档 §7/§8）：稳定直链解析（302 目标）、缩略图、预览地址、
/// 小文本瞬态读写与编辑前快照。所有出口统一过：登录 → 归属 → 敏感路径 三道闸。
/// 直链与文件内容不落盘、不落日志。
/// </summary>
public sealed class OneDriveContentService
{
    /// <summary>文本瞬态代理上限（设计文档 §8）。</summary>
    public const long MaxTextBytes = 2 * 1024 * 1024;

    /// <summary>简单上传上限（Graph 单请求 PUT 限制 4MB）。</summary>
    public const long MaxSaveBytes = 4 * 1024 * 1024;

    /// <summary>每个文件保留的快照上限。</summary>
    public const int KeepSnapshotsPerItem = 10;

    private readonly PimDbContext _db;
    private readonly IOneDriveGraphClient _client;
    private readonly OneDriveTokenService _tokens;
    private readonly ICurrentUserService _currentUser;
    private readonly SensitivePathPolicy _sensitivePolicy;
    private readonly ILogger<OneDriveContentService>? _logger;
    private readonly TimeProvider _clock;

    public OneDriveContentService(
        PimDbContext db,
        IOneDriveGraphClient client,
        OneDriveTokenService tokens,
        ICurrentUserService currentUser,
        SensitivePathPolicy sensitivePolicy,
        ILogger<OneDriveContentService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _currentUser = currentUser;
        _sensitivePolicy = sensitivePolicy;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<string> GetContentLinkAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureDownloadable(item);
        EnsureNotSensitive(item);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var link = await _client.GetDownloadUrlAsync(token, item.ExternalFileId, ct);
        return link ?? throw new DomainException(5333, "OneDrive 暂未返回下载直链，请稍后重试");
    }

    public async Task<string> GetThumbnailLinkAsync(Guid itemId, string size, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureNotSensitive(item);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var link = await _client.GetThumbnailUrlAsync(token, item.ExternalFileId, size, ct);
        return link ?? throw new DomainException(5332, "该文件不支持缩略图");
    }

    public async Task<string> GetPreviewLinkAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureDownloadable(item);
        EnsureNotSensitive(item);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var link = await _client.GetPreviewUrlAsync(token, item.ExternalFileId, ct);
        return link ?? throw new DomainException(5333, "OneDrive 暂未返回预览地址，请稍后重试");
    }

    public async Task<OneDriveTextContent> GetTextAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureDownloadable(item);
        EnsureNotSensitive(item);
        EnsureTextEditable(item);
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var content = await DownloadSmallOrThrowAsync(token, item, MaxTextBytes, ct);
        return new OneDriveTextContent(
            Encoding.UTF8.GetString(content.Bytes),
            NormalizeContentType(content.ContentType) ?? item.MimeType,
            content.Bytes.LongLength,
            Truncated: false);
    }

    public async Task SaveTextAsync(Guid itemId, string? content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureDownloadable(item);
        EnsureNotSensitive(item);
        EnsureTextEditable(item);

        var newBytes = Encoding.UTF8.GetBytes(content);
        if (newBytes.Length > MaxSaveBytes)
        {
            throw new DomainException(5331, $"内容超过 {MaxSaveBytes / 1024 / 1024}MB，请在 OneDrive 中编辑");
        }

        // 编辑前快照当前内容（个人版版本 API 不确定的兜底，见设计文档 §8）。
        // 上限与保存一致（4MB）：只按 2MB 截断会让 2–4MB 的文件永远无法保存
        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var current = await DownloadSmallOrThrowAsync(token, item, MaxSaveBytes, ct);
        await CreateSnapshotAsync(item, current.Bytes, current.ContentType, "pre-edit", ct);

        var mimeType = NormalizeContentType(current.ContentType) ?? item.MimeType ?? "text/plain";
        await _client.PutSmallContentAsync(token, item.ExternalFileId, newBytes, mimeType, ct);
        await PruneSnapshotsAsync(itemId, ct);
        _logger?.LogInformation(
            "OneDrive text saved: item {ItemId}, {OldBytes} -> {NewBytes} bytes",
            itemId, current.Bytes.LongLength, newBytes.LongLength);
    }

    public async Task<IReadOnlyList<FileTextSnapshotDto>> ListSnapshotsAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, _) = await LoadOwnedItemAsync(itemId, ct);
        // 快照含全文，敏感路径必须与其他出口同样拦截（复审 C2）
        EnsureNotSensitive(item);
        return await _db.Set<FileTextSnapshotEntity>()
            .Where(snapshot => snapshot.ItemId == itemId)
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .Select(snapshot => new FileTextSnapshotDto(
                snapshot.Id, snapshot.Path, snapshot.Name,
                snapshot.Content.Length > 64 ? snapshot.Content.Substring(0, 64) : snapshot.Content,
                snapshot.ByteSize, snapshot.Reason, snapshot.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task RestoreSnapshotAsync(Guid itemId, Guid snapshotId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureNotSensitive(item);
        var snapshot = await _db.Set<FileTextSnapshotEntity>()
            .SingleOrDefaultAsync(row => row.Id == snapshotId && row.ItemId == itemId, ct)
            ?? throw new DomainException(5104, "快照不存在");

        var token = await _tokens.GetAccessTokenAsync(provider.Id, ct);
        var current = await DownloadSmallOrThrowAsync(token, item, MaxTextBytes, ct);
        await CreateSnapshotAsync(item, current.Bytes, current.ContentType, "pre-restore", ct);

        await _client.PutSmallContentAsync(token, item.ExternalFileId, Encoding.UTF8.GetBytes(snapshot.Content), snapshot.MimeType ?? "text/plain", ct);
        await PruneSnapshotsAsync(itemId, ct);
        _logger?.LogInformation(
            "OneDrive text restored from snapshot {SnapshotId} for item {ItemId}",
            snapshotId, itemId);
    }

    private async Task<(FileItemEntity Item, FileProviderEntity Provider)> LoadOwnedItemAsync(Guid itemId, CancellationToken ct)
    {
        var item = await _db.Set<FileItemEntity>()
            .Include(row => row.Provider)
            .SingleOrDefaultAsync(row => row.Id == itemId, ct)
            ?? throw new DomainException(5104, "文件不存在");
        if (item.Provider is null
            || item.Provider.UserId != UserId
            || item.Provider.Provider != "onedrive"
            || item.IsDeleted)
        {
            throw new DomainException(5104, "文件不存在");
        }

        return (item, item.Provider);
    }

    private void EnsureDownloadable(FileItemEntity item)
    {
        if (item.ItemType == "folder")
        {
            throw new DomainException(5332, "文件夹没有可下载内容");
        }
    }

    private void EnsureTextEditable(FileItemEntity item)
    {
        var mime = item.MimeType ?? string.Empty;
        var name = item.Name.ToLowerInvariant();
        var textMime = mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("yaml", StringComparison.OrdinalIgnoreCase);
        var textExt = TextEditableExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        if (!textMime && !textExt)
        {
            throw new DomainException(5332, "该文件不是文本类型");
        }
    }

    private static readonly string[] TextEditableExtensions =
    [
        ".txt", ".md", ".markdown", ".json", ".csv", ".log", ".yml", ".yaml", ".xml",
    ];

    private void EnsureNotSensitive(FileItemEntity item)
    {
        if (_sensitivePolicy.IsProtected(item.Path))
        {
            throw new DomainException(40303, "敏感路径受保护，不允许该操作");
        }
    }

    private async Task<OneDriveSmallContent> DownloadSmallOrThrowAsync(
        string token, FileItemEntity item, long maxBytes, CancellationToken ct)
    {
        try
        {
            return await _client.DownloadSmallAsync(token, item.ExternalFileId, maxBytes, ct)
                ?? throw new DomainException(5104, "文件不存在或已被删除");
        }
        catch (OneDriveContentTooLargeException)
        {
            throw new DomainException(5331, "文件超过文本处理上限，请在 OneDrive 中操作");
        }
    }

    /// <summary>每文件只保留最近 KeepSnapshotsPerItem 份快照（设计文档 §8）。</summary>
    private async Task PruneSnapshotsAsync(Guid itemId, CancellationToken ct)
    {
        var stale = await _db.Set<FileTextSnapshotEntity>()
            .Where(snapshot => snapshot.ItemId == itemId)
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ThenByDescending(snapshot => snapshot.Id)
            .Skip(KeepSnapshotsPerItem)
            .ToListAsync(ct);
        if (stale.Count == 0)
        {
            return;
        }

        _db.Set<FileTextSnapshotEntity>().RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    private async Task CreateSnapshotAsync(
        FileItemEntity item,
        byte[] contentBytes,
        string? contentType,
        string reason,
        CancellationToken ct)
    {
        _db.Set<FileTextSnapshotEntity>().Add(new FileTextSnapshotEntity
        {
            UserId = UserId,
            ProviderId = item.ProviderId,
            ItemId = item.Id,
            ExternalFileId = item.ExternalFileId,
            Path = item.Path,
            Name = item.Name,
            MimeType = NormalizeContentType(contentType) ?? item.MimeType,
            Content = Encoding.UTF8.GetString(contentBytes),
            ByteSize = contentBytes.Length,
            Reason = reason,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string? NormalizeContentType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType)
            ? null
            : contentType.Split(';')[0].Trim();
}
