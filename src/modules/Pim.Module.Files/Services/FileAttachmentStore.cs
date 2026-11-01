using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Services;

/// <summary>
/// <see cref="IFileAttachmentStore"/> 的通用实现（工单 05 / F1、F2）：
/// 附件按约定目录 <c>/PIM/{objectKey}</c> 存入用户的**默认账号**（≤4MB 简单上传），
/// 返回远端条目 id + 账号句柄作为后续读/删的定位；内容读取瞬态经过，不落盘。
///
/// 多账号（F2）：写入时选定并记录账号；读取时优先用记录的账号，
/// 老数据（没记账号）在该用户的其余已连接账号里逐一回退查找 ——
/// 远端条目 id 在各自账号内唯一，因此回退是确定的，而不是「猜」。
/// </summary>
public sealed class FileAttachmentStore(
    PimDbContext db,
    FileProviderSessionFactory sessions,
    ILogger<FileAttachmentStore>? logger = null) : IFileAttachmentStore, IBoundedFileAttachmentStore
{
    /// <summary>附件在网盘中的根目录（与用户文件树区分开）。</summary>
    public const string AttachmentRoot = "/PIM";

    public async Task<FileAttachmentRef> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default) =>
        await StoreAsync(userId, objectKey, content, contentType, sizeBytes,
            FileContentService.MaxSaveBytes, ct);

    public async Task<FileAttachmentRef> StoreAsync(
        Guid userId,
        string objectKey,
        Stream content,
        string contentType,
        long sizeBytes,
        long maxBytes,
        CancellationToken ct = default)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var provider = await LoadDefaultProviderAsync(userId, ct);
        var session = await sessions.OpenAsync(provider, ct);

        // 边读边计数：不能先全量读进内存再判上限，否则超大请求会在检查前吃光内存。
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new DomainException(
                    5331,
                    $"附件超过 {maxBytes / 1024 / 1024}MB，请使用网盘客户端");
            }

            buffer.Write(chunk, 0, read);
        }

        // 附件目录（/PIM/...）不属于用户同步的文件树，因此这里只有路径、没有目录 id：
        // provider 自行决定用哪套坐标（Graph 按路径上传并自动补建目录，
        // Drive 按路径逐段解析/补建后再按父 id 上传）。
        var drivePath = AttachmentPath(objectKey);
        var lastSlash = drivePath.LastIndexOf('/');
        var fileName = lastSlash < 0 ? drivePath : drivePath[(lastSlash + 1)..];
        var directoryPath = lastSlash <= 0 ? "/" : drivePath[..lastSlash];
        var externalId = await session.Writer.CreateFileAsync(
            new RemoteFolderRef(null, directoryPath), fileName, buffer.ToArray(), contentType, ct);
        logger?.LogInformation(
            "Attachment stored: user {UserId}, account {ProviderId}, bytes {Bytes}",
            userId, provider.Id, buffer.Length);
        return new FileAttachmentRef(externalId, provider.Provider, provider.Id);
    }

    public async Task<Stream> OpenReadAsync(
        Guid userId,
        FileAttachmentRef reference,
        CancellationToken ct = default) =>
        await OpenReadAsync(userId, reference, FileContentService.MaxSaveBytes, ct);

    public async Task<Stream> OpenReadAsync(
        Guid userId,
        FileAttachmentRef reference,
        long maxBytes,
        CancellationToken ct = default)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        foreach (var provider in await CandidateProvidersAsync(userId, reference, ct))
        {
            var session = await sessions.OpenAsync(provider, ct);
            try
            {
                var downloaded = await session.Content.DownloadSmallAsync(
                    reference.ObjectKey, maxBytes, ct);
                if (downloaded is not null)
                {
                    return new MemoryStream(downloaded.Bytes);
                }
            }
            catch (ProviderItemNotFoundException)
            {
                // 这个账号里没有该句柄，继续试下一个
            }
            catch (ProviderContentTooLargeException)
            {
                throw new DomainException(4006, "附件超过可读取大小上限");
            }
        }

        throw new DomainException(4006, "附件不存在");
    }

    /// <summary>
    /// 预授权直链。只对**真正免密**的直链返回 URL：
    /// Google 的 <c>webContentLink</c> 依赖浏览器持有对应 Google 账号的 Cookie，
    /// 在 <c>&lt;img&gt;</c> 这类内联场景会拿到登录页/403 —— 附件宁可走服务端代理
    /// （≤4MB，可靠），也不给一个时灵时不灵的链接。
    /// </summary>
    public async Task<string?> GetDirectLinkAsync(
        Guid userId,
        FileAttachmentRef reference,
        CancellationToken ct = default)
    {
        foreach (var provider in (await CandidateProvidersAsync(userId, reference, ct))
                     .Where(row => !string.Equals(row.Provider, "google", StringComparison.OrdinalIgnoreCase)))
        {
            var session = await sessions.OpenAsync(provider, ct);
            var link = await session.Content.GetDownloadUrlAsync(reference.ObjectKey, ct);
            if (link is not null)
            {
                return link;
            }
        }

        return null;
    }

    public async Task DeleteAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default)
    {
        var deleted = false;
        foreach (var provider in await CandidateProvidersAsync(userId, reference, ct))
        {
            var session = await sessions.OpenAsync(provider, ct);
            try
            {
                // provider 实现把「远端已不存在」视为成功，不报错
                await session.Writer.DeleteItemAsync(reference.ObjectKey, ct);
                deleted = true;
            }
            catch (ProviderItemNotFoundException)
            {
                continue;
            }
        }

        if (deleted)
        {
            logger?.LogInformation("Attachment deleted: user {UserId}, object {ObjectKey}", userId, reference.ObjectKey);
        }
    }

    /// <summary>objectKey（<c>quick-notes/{userId}/{id}/{name}</c>）映射为网盘路径 <c>/PIM/{objectKey}</c>。</summary>
    public static string AttachmentPath(string objectKey)
        => AttachmentRoot + "/" + objectKey.TrimStart('/');

    private async Task<FileProviderEntity> LoadDefaultProviderAsync(Guid userId, CancellationToken ct)
        => await FileProviderLookup.FindDefaultAsync(db, userId, ct: ct)
           ?? throw new DomainException(5320, "尚未绑定网盘账号，附件功能不可用");

    /// <summary>
    /// 候选账号：句柄里记了账号就只用那个（确定性）；
    /// 没记账号（本列引入之前写入的老数据）则在该用户的全部已连接账号里回退查找。
    /// </summary>
    private async Task<IReadOnlyList<FileProviderEntity>> CandidateProvidersAsync(
        Guid userId,
        FileAttachmentRef reference,
        CancellationToken ct)
    {
        var connected = await db.Set<FileProviderEntity>()
            .Where(row => row.UserId == userId && row.Status == "connected")
            .OrderByDescending(row => row.IsDefault)
            .ThenBy(row => row.CreatedAt)
            .ToListAsync(ct);

        var sameProvider = connected
            .Where(row => string.IsNullOrWhiteSpace(reference.ProviderKey)
                || string.Equals(row.Provider, reference.ProviderKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (reference.ProviderId is { } providerId)
        {
            var exact = connected.FirstOrDefault(row => row.Id == providerId);
            if (exact is not null)
            {
                return [exact];
            }

            // 句柄记的账号已经不存在（断开后重绑会产生新的账号行）：回退到该用户同 provider 的
            // 其余已连接账号继续找，而不是直接判定「附件不存在」——
            // 远端文件还在，用户不该因为重绑过就永远读不回自己的附件（复审 Important）。
            return sameProvider;
        }

        return sameProvider;
    }
}
