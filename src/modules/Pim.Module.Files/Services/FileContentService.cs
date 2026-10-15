using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Services;

public sealed record FileTextContent(string Content, string? MimeType, long Size, bool Truncated);

public sealed record FileTextSnapshotDto(
    Guid Id,
    string Path,
    string Name,
    string Content,
    int ByteSize,
    string Reason,
    DateTimeOffset CreatedAt);

/// <summary>
/// 通用内容出口（工单 05 / F1）：稳定直链解析（302 目标）、缩略图、预览地址、
/// 小文本瞬态读写与编辑前快照。所有出口统一过：登录 → 归属 → 敏感路径 三道闸。
/// 直链与文件内容不落盘、不落日志。
///
/// 与 provider 无关：实际取内容由账号作用域会话的 <see cref="IFileContentReader"/> 完成。
/// </summary>
public sealed class FileContentService
{
    /// <summary>文本瞬态代理上限。</summary>
    public const long MaxTextBytes = 2 * 1024 * 1024;

    /// <summary>简单上传上限（provider 单请求 PUT 的公共安全档）。</summary>
    public const long MaxSaveBytes = 4 * 1024 * 1024;

    /// <summary>每个文件保留的快照上限。</summary>
    public const int KeepSnapshotsPerItem = 10;

    private readonly PimDbContext _db;
    private readonly FileProviderSessionFactory _sessions;
    private readonly IFileProviderFactory _providers;
    private readonly ICurrentUserService _currentUser;
    private readonly SensitivePathPolicy _sensitivePolicy;
    private readonly FileTextExtractor _textExtractor;
    private readonly FileTransientRateLimiter _rateLimiter;
    private readonly IAuditLogService? _auditLog;
    private readonly ILogger<FileContentService>? _logger;
    private readonly TimeProvider _clock;

    public FileContentService(
        PimDbContext db,
        FileProviderSessionFactory sessions,
        IFileProviderFactory providers,
        ICurrentUserService currentUser,
        SensitivePathPolicy sensitivePolicy,
        ILogger<FileContentService>? logger = null,
        TimeProvider? clock = null,
        FileTextExtractor? textExtractor = null,
        FileTransientRateLimiter? rateLimiter = null,
        IAuditLogService? auditLog = null)
    {
        _db = db;
        _sessions = sessions;
        _providers = providers;
        _currentUser = currentUser;
        _sensitivePolicy = sensitivePolicy;
        _textExtractor = textExtractor ?? new FileTextExtractor();
        _rateLimiter = rateLimiter ?? new FileTransientRateLimiter(clock);
        _auditLog = auditLog;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<string> GetContentLinkAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureDownloadable(item);
        EnsureNotSensitive(item);
        var session = await _sessions.OpenAsync(provider, ct);
        var link = await session.Content.GetDownloadUrlAsync(item.ExternalFileId, ct);
        return link ?? throw new DomainException(5333, "网盘暂未返回下载直链，请稍后重试");
    }

    public async Task<string> GetThumbnailLinkAsync(Guid itemId, string size, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureNotSensitive(item);
        var session = await _sessions.OpenAsync(provider, ct);
        var link = await session.Content.GetThumbnailUrlAsync(item.ExternalFileId, size, ct);
        return link ?? throw new DomainException(5332, "该文件不支持缩略图");
    }

    public async Task<string> GetPreviewLinkAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureDownloadable(item);
        EnsureNotSensitive(item);
        var session = await _sessions.OpenAsync(provider, ct);
        var link = await session.Content.GetPreviewUrlAsync(item.ExternalFileId, ct);
        return link ?? throw new DomainException(5333, "网盘暂未返回预览地址，请稍后重试");
    }

    /// <summary>
    /// read_file_text：瞬态下载 + 抽取 + 限流 + 审计。
    /// 与 <see cref="GetTextAsync"/> 的区别：接受任意可抽取类型（docx/pptx/Tika）、支持 maxBytes、
    /// 面向 agent 的高频读取因此有限流。
    /// </summary>
    public async Task<FileTextContent> ReadTextAsync(
        Guid itemId,
        long? maxBytesParam,
        CancellationToken ct = default)
    {
        var maxBytes = maxBytesParam is null or <= 0
            ? FileTextExtractor.DefaultMaxBytes
            : Math.Min(maxBytesParam.Value, FileTextExtractor.HardMaxBytes);
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        if (item.ItemType == "folder")
        {
            throw new DomainException(5332, "文件夹没有可抽取的文本");
        }

        EnsureNotSensitive(item);
        _rateLimiter.AssertAllowed(UserId);

        var session = await _sessions.OpenAsync(provider, ct);
        var downloaded = await DownloadSmallOrThrowAsync(
            session, item.ExternalFileId, FileTextExtractor.HardMaxBytes, ct);
        // 用**下载回来的实际内容类型**做抽取判定：Google 的 Docs 名字没有后缀，
        // 元数据里的 MIME 是 application/vnd.google-apps.document，
        // 只看名字/MIME 会让导出的纯文本被判成「不支持的类型」而报 5336。
        var extracted = await _textExtractor.ExtractAsync(
            downloaded.Bytes, item.Name, downloaded.ContentType ?? item.MimeType, maxBytes, ct);
        await RecordAuditAsync("files.read_text", item.Id, extracted.SourceBytes, ct);
        _logger?.LogInformation(
            "Files read_text: item {ItemId}, {Bytes} bytes, extractor={Extractor}",
            itemId, extracted.SourceBytes, extracted.Extractor);
        return new FileTextContent(extracted.Content, item.MimeType, extracted.SourceBytes, extracted.Truncated);
    }

    private async Task RecordAuditAsync(string action, Guid itemId, long bytes, CancellationToken ct)
    {
        if (_auditLog is null)
        {
            return;
        }

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
            new Dictionary<string, string> { ["bytes"] = bytes.ToString() },
            null,
            null), ct);
    }

    public async Task<FileTextContent> GetTextAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, provider) = await LoadOwnedItemAsync(itemId, ct);
        EnsureDownloadable(item);
        EnsureNotSensitive(item);
        EnsureTextEditable(item);
        var session = await _sessions.OpenAsync(provider, ct);
        // 上限与保存一致（4MB）：否则 2–4MB 文件「能保存不能读」
        var content = await DownloadSmallOrThrowAsync(session, item.ExternalFileId, MaxSaveBytes, ct);
        return new FileTextContent(
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
            throw new DomainException(5331, $"内容超过 {MaxSaveBytes / 1024 / 1024}MB，请在网盘中编辑");
        }

        var session = await _sessions.OpenAsync(provider, ct);
        // 编辑前快照当前内容（版本 API 不确定时的兜底）。
        // 上限与保存一致（4MB）：只按 2MB 截断会让 2–4MB 的文件永远无法保存
        var current = await DownloadSmallOrThrowAsync(session, item.ExternalFileId, MaxSaveBytes, ct);
        await CreateSnapshotAsync(item, current.Bytes, current.ContentType, "pre-edit", ct);

        var mimeType = NormalizeContentType(current.ContentType) ?? item.MimeType ?? "text/plain";
        await session.Writer.PutContentAsync(item.ExternalFileId, newBytes, mimeType, ct);
        await PruneSnapshotsAsync(itemId, ct);
        _logger?.LogInformation(
            "Files text saved: item {ItemId}, {OldBytes} -> {NewBytes} bytes",
            itemId, current.Bytes.LongLength, newBytes.LongLength);
    }

    public async Task<IReadOnlyList<FileTextSnapshotDto>> ListSnapshotsAsync(
        Guid itemId,
        CancellationToken ct = default)
    {
        var (item, _) = await LoadOwnedItemAsync(itemId, ct);
        // 快照含全文，敏感路径必须与其他出口同样拦截
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

        var session = await _sessions.OpenAsync(provider, ct);
        var current = await DownloadSmallOrThrowAsync(session, item.ExternalFileId, MaxSaveBytes, ct);
        await CreateSnapshotAsync(item, current.Bytes, current.ContentType, "pre-restore", ct);

        await session.Writer.PutContentAsync(
            item.ExternalFileId,
            Encoding.UTF8.GetBytes(snapshot.Content),
            snapshot.MimeType ?? "text/plain",
            ct);
        await PruneSnapshotsAsync(itemId, ct);
        _logger?.LogInformation(
            "Files text restored from snapshot {SnapshotId} for item {ItemId}", snapshotId, itemId);
    }

    private async Task<(FileItemEntity Item, FileProviderEntity Provider)> LoadOwnedItemAsync(
        Guid itemId,
        CancellationToken ct)
    {
        var item = await _db.Set<FileItemEntity>()
            .Include(row => row.Provider)
            .SingleOrDefaultAsync(row => row.Id == itemId, ct)
            ?? throw new DomainException(5104, "文件不存在");
        if (item.Provider is null
            || item.Provider.UserId != UserId
            || !_providers.IsSupported(item.Provider.Provider)
            || item.IsDeleted)
        {
            throw new DomainException(5104, "文件不存在");
        }

        if (item.Provider.Status != "connected")
        {
            // 文件确实属于该用户，只是账号用不了：说清原因，用户才知道去重新连接
            throw item.Provider.Status == "expired"
                ? new DomainException(5322, "网盘授权已失效，请到设置页重新连接该账号")
                : new DomainException(5321, "文件来源尚未完成绑定");
        }

        return (item, item.Provider);
    }

    private static void EnsureDownloadable(FileItemEntity item)
    {
        if (item.ItemType == "folder")
        {
            throw new DomainException(5332, "文件夹没有可下载内容");
        }
    }

    private static void EnsureTextEditable(FileItemEntity item)
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

    private static async Task<RemoteContent> DownloadSmallOrThrowAsync(
        IFileProviderSession session,
        string externalFileId,
        long maxBytes,
        CancellationToken ct)
    {
        try
        {
            return await session.Content.DownloadSmallAsync(externalFileId, maxBytes, ct)
                ?? throw new DomainException(5104, "文件不存在或已被删除");
        }
        catch (ProviderContentTooLargeException)
        {
            throw new DomainException(5331, "文件超过文本处理上限，请在网盘中操作");
        }
    }

    /// <summary>每文件只保留最近 KeepSnapshotsPerItem 份快照。</summary>
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
