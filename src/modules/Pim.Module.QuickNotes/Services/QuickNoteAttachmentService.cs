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
        var storedObjectKey = await storage.StoreAsync(objectKey, content, normalizedContentType, sizeBytes, ct);

        var attachment = new QuickNoteAttachmentEntity
        {
            Id = id,
            QuickNoteId = null,
            UserId = userId,
            StorageProvider = "minio",
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
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct)
            ?? throw new DomainException(4006, "附件不存在");

        var content = await storage.OpenReadAsync(attachment.ObjectKey, ct);
        return (content, attachment.ContentType, attachment.FileName);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var userId = UserId;
        var attachment = await db.Set<QuickNoteAttachmentEntity>()
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct)
            ?? throw new DomainException(4006, "附件不存在");

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
}
