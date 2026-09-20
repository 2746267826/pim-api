using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.QuickNotes.DTOs;
using Pim.Module.QuickNotes.Entities;

namespace Pim.Module.QuickNotes.Services;

public sealed class QuickNoteAttachmentService(
    PimDbContext db,
    ICurrentUserService currentUser,
    IQuickNoteObjectStorage storage)
{
    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "未登录");

    public async Task<QuickNoteAttachmentUploadDto> UploadAsync(
        Stream content,
        string fileName,
        string? contentType,
        long sizeBytes,
        CancellationToken ct = default)
    {
        var userId = UserId;
        if (string.IsNullOrWhiteSpace(fileName))
            throw new DomainException(4007, "附件文件名不能为空");

        if (sizeBytes < 0)
            throw new DomainException(4008, "附件大小不能为负数");

        var id = Guid.NewGuid();
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new DomainException(4007, "附件文件名不能为空");

        var normalizedContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();
        var objectKey = $"quick-notes/{userId:N}/{id:N}/{safeName}";
        var storedObjectKey = await storage.StoreAsync(userId, objectKey, content, normalizedContentType, sizeBytes, ct);

        var attachment = new QuickNoteAttachmentEntity
        {
            Id = id,
            QuickNoteId = null,
            UserId = userId,
            StorageProvider = ResolveProviderName(),
            ObjectKey = storedObjectKey,
            FileName = safeName,
            ContentType = normalizedContentType,
            SizeBytes = sizeBytes,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Set<QuickNoteAttachmentEntity>().Add(attachment);
        await db.SaveChangesAsync(ct);

        return MapUpload(attachment);
    }

    public async Task<(Stream Content, string ContentType, string FileName)> DownloadAsync(
        Guid id,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var attachment = await db.Set<QuickNoteAttachmentEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new DomainException(4006, "附件不存在");
        if (attachment.UserId != userId)
            throw new DomainException(40301, "无权访问该附件");

        var content = await storage.OpenReadAsync(userId, attachment.ObjectKey, ct);
        return (content, attachment.ContentType, attachment.FileName);
    }

    /// <summary>
    /// 预授权直链（设计文档 §10）：存储后端支持时可让内容端点 302，避免服务器代理流量。
    /// 不支持时返回 null，由端点回退到代理下载。
    /// </summary>
    public async Task<string?> GetDirectLinkAsync(Guid id, CancellationToken ct = default)
    {
        var userId = UserId;
        var attachment = await db.Set<QuickNoteAttachmentEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new DomainException(4006, "附件不存在");
        if (attachment.UserId != userId)
            throw new DomainException(40301, "无权访问该附件");

        if (storage is not IQuickNoteDirectLinkStorage directLink)
        {
            return null;
        }

        return await directLink.GetDirectLinkAsync(userId, attachment.ObjectKey, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var userId = UserId;
        var attachment = await db.Set<QuickNoteAttachmentEntity>()
            .FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new DomainException(4006, "附件不存在");
        if (attachment.UserId != userId)
            throw new DomainException(40301, "无权访问该附件");

        // 先删远端再改本地：附件实体是**用户自己的** OneDrive 对象，
        // 只软删元数据会把文件永久留在对方网盘里（用户删了附件却在 OneDrive 里还能看到）。
        // 与文件模块一致：远端成功（或远端已不存在）后才收敛本地状态。
        await storage.DeleteAsync(userId, attachment.ObjectKey, ct);

        attachment.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<QuickNoteAttachmentEntity>> LoadBindableAttachmentsAsync(
        IEnumerable<Guid> attachmentIds,
        Guid? targetNoteId,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var ids = attachmentIds
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return Array.Empty<QuickNoteAttachmentEntity>();

        var attachments = await db.Set<QuickNoteAttachmentEntity>()
            .Where(a => ids.Contains(a.Id) && a.UserId == userId)
            .ToListAsync(ct);

        if (attachments.Count != ids.Count)
            throw new DomainException(4005, "附件不能绑定到这条快速记录");

        foreach (var attachment in attachments)
        {
            if (attachment.QuickNoteId.HasValue && attachment.QuickNoteId != targetNoteId)
                throw new DomainException(4005, "附件不能绑定到这条快速记录");
        }

        return ids
            .Select(id => attachments.Single(a => a.Id == id))
            .ToList();
    }

    private static QuickNoteAttachmentUploadDto MapUpload(QuickNoteAttachmentEntity attachment)
    {
        var downloadUrl = BuildDownloadUrl(attachment.Id);
        var previewUrl = attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? downloadUrl
            : null;

        return new QuickNoteAttachmentUploadDto(
            attachment.Id,
            attachment.FileName,
            attachment.ContentType,
            attachment.SizeBytes,
            downloadUrl,
            previewUrl);
    }

    private static string BuildDownloadUrl(Guid id)
        => $"/api/v1/quick-notes/attachments/{id}/download";

    /// <summary>
    /// 记录附件落在哪个后端。v2 起附件只有 OneDrive 一条线（MinIO 随 P4 退役），
    /// 因此实现方是 OneDrive 适配器时记 "onedrive"，其余（测试替身等）记类型名。
    /// </summary>
    private string ResolveProviderName()
        => storage is OneDriveQuickNoteObjectStorage
            ? OneDriveQuickNoteObjectStorage.ProviderName
            : storage.GetType().Name;
}
