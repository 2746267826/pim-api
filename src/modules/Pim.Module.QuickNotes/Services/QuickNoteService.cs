using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.QuickNotes.DTOs;
using Pim.Module.QuickNotes.Entities;

namespace Pim.Module.QuickNotes.Services;

public class QuickNoteService
{
    private const string ResourceType = "quick_note";
    private const string AuditSource = "quick-notes";

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditLogService _auditLog;
    private readonly QuickNoteAttachmentService _attachments;

    public QuickNoteService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IAuditLogService auditLog,
        QuickNoteAttachmentService attachments)
    {
        _db = db;
        _currentUser = currentUser;
        _auditLog = auditLog;
        _attachments = attachments;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(1002, "Not authenticated");

    public async Task<PagedResult<QuickNoteListItemDto>> ListAsync(
        QuickNoteListQuery query,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var notes = _db.Set<QuickNoteEntity>()
            .Where(note => note.UserId == userId);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            ValidateStatus(status);
            notes = notes.Where(note => note.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            notes = notes.Where(note => note.ContentMarkdown.Contains(search));
        }

        var totalCount = await notes.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var entities = await notes
            .AsNoTracking()
            .OrderByDescending(note => note.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(note => new
            {
                note.Id,
                note.ContentMarkdown,
                note.Status,
                note.Source,
                AttachmentCount = note.Attachments.Count(attachment => attachment.DeletedAt == null),
                note.CreatedAt,
                note.UpdatedAt,
                note.ArchivedAt
            })
            .ToListAsync(ct);

        var items = entities
            .Select(note => new QuickNoteListItemDto(
                note.Id,
                BuildPreview(note.ContentMarkdown),
                note.Status,
                note.Source,
                note.AttachmentCount,
                note.CreatedAt,
                note.UpdatedAt,
                note.ArchivedAt))
            .ToList();

        return new PagedResult<QuickNoteListItemDto>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<QuickNoteDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(id, ct);
        return MapDetail(note);
    }

    public async Task<QuickNoteDetailDto> CreateAsync(
        CreateQuickNoteRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var now = DateTimeOffset.UtcNow;
        var attachmentIds = MergeAttachmentIds(request.AttachmentIds, request.ContentMarkdown);
        var bindableAttachments = await _attachments.LoadBindableAttachmentsAsync(attachmentIds, null, ct);
        var note = new QuickNoteEntity
        {
            UserId = userId,
            ContentMarkdown = request.ContentMarkdown ?? string.Empty,
            Status = QuickNoteStatuses.Inbox,
            Source = NormalizeSource(request.Source),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Set<QuickNoteEntity>().Add(note);

        foreach (var attachment in bindableAttachments)
        {
            attachment.QuickNoteId = note.Id;
            note.Attachments.Add(attachment);
        }

        await _db.SaveChangesAsync(ct);

        await RecordAuditAsync("quick_notes.create", note.Id, userId, ct);

        return MapDetail(note);
    }

    public async Task<QuickNoteDetailDto> UpdateAsync(
        Guid id,
        UpdateQuickNoteRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var note = await LoadNoteAsync(id, ct);

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var status = request.Status.Trim();
            ValidateStatus(status);
            note.Status = status;
            note.ArchivedAt = status == QuickNoteStatuses.Archived ? DateTimeOffset.UtcNow : null;
        }

        note.ContentMarkdown = request.ContentMarkdown ?? string.Empty;
        note.UpdatedAt = DateTimeOffset.UtcNow;

        var attachmentIds = MergeAttachmentIds(request.AttachmentIds, request.ContentMarkdown);
        var bindableAttachments = await _attachments.LoadBindableAttachmentsAsync(attachmentIds, note.Id, ct);
        var attachmentIdSet = attachmentIds.ToHashSet();
        var now = DateTimeOffset.UtcNow;

        foreach (var attachment in note.Attachments.Where(attachment => !attachmentIdSet.Contains(attachment.Id)))
            attachment.DeletedAt = now;

        foreach (var attachment in bindableAttachments)
        {
            attachment.QuickNoteId = note.Id;
            attachment.DeletedAt = null;
            if (!note.Attachments.Any(existing => existing.Id == attachment.Id))
                note.Attachments.Add(attachment);
        }

        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("quick_notes.update", note.Id, userId, ct);

        return MapDetail(note);
    }

    public async Task<QuickNoteDetailDto> ProcessAsync(Guid id, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(id, ct);
        note.Status = QuickNoteStatuses.Processed;
        note.ArchivedAt = null;
        note.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("quick_notes.process", note.Id, note.UserId, ct);

        return MapDetail(note);
    }

    public async Task<QuickNoteDetailDto> ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(id, ct);
        var now = DateTimeOffset.UtcNow;
        note.Status = QuickNoteStatuses.Archived;
        note.ArchivedAt = now;
        note.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("quick_notes.archive", note.Id, note.UserId, ct);

        return MapDetail(note);
    }

    public Task<QuickNoteDetailDto> RestoreAsync(
        Guid id,
        RestoreQuickNoteRequest request,
        CancellationToken ct = default)
        => RestoreAsync(id, request.Status, ct);

    public async Task<QuickNoteDetailDto> RestoreAsync(
        Guid id,
        string status,
        CancellationToken ct = default)
    {
        var restoredStatus = string.IsNullOrWhiteSpace(status) ? QuickNoteStatuses.Inbox : status.Trim();
        ValidateStatus(restoredStatus);

        var note = await LoadNoteAsync(id, ct);
        note.Status = restoredStatus;
        note.ArchivedAt = restoredStatus == QuickNoteStatuses.Archived ? DateTimeOffset.UtcNow : null;
        note.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("quick_notes.restore", note.Id, note.UserId, ct);

        return MapDetail(note);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var note = await LoadNoteAsync(id, ct);
        var now = DateTimeOffset.UtcNow;
        note.DeletedAt = now;
        note.UpdatedAt = now;

        foreach (var attachment in note.Attachments.Where(attachment => attachment.DeletedAt == null))
            attachment.DeletedAt = now;

        await _db.SaveChangesAsync(ct);
        await RecordAuditAsync("quick_notes.delete", note.Id, note.UserId, ct);
    }

    private async Task<QuickNoteEntity> LoadNoteAsync(Guid id, CancellationToken ct)
    {
        var userId = UserId;
        return await _db.Set<QuickNoteEntity>()
            .Include(note => note.Attachments)
            .FirstOrDefaultAsync(note => note.Id == id && note.UserId == userId, ct)
            ?? throw new DomainException(4004, "Quick note not found");
    }

    private async Task RecordAuditAsync(string action, Guid noteId, Guid userId, CancellationToken ct)
    {
        await _auditLog.RecordAsync(new CreateAuditLogRequest(
            userId,
            AuditActorType.User,
            action,
            ResourceType,
            noteId.ToString(),
            AuditSource,
            AuditResult.Success,
            null,
            null,
            null,
            null,
            null,
            null), ct);
    }

    private static void ValidateStatus(string status)
    {
        if (!QuickNoteStatuses.IsValid(status))
            throw new DomainException(4003, "Invalid quick note status");
    }

    private static string NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? QuickNoteSources.WebPage : source.Trim();

    private static string BuildPreview(string content)
    {
        var preview = content
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("#", string.Empty)
            .Replace("*", string.Empty)
            .Replace("_", string.Empty)
            .Replace("`", string.Empty)
            .Trim();

        return preview.Length <= 140 ? preview : preview[..140];
    }

    private static IReadOnlyList<Guid> MergeAttachmentIds(IReadOnlyList<Guid>? explicitIds, string? markdown)
    {
        var merged = new List<Guid>();
        var seen = new HashSet<Guid>();

        if (explicitIds is not null)
        {
            foreach (var id in explicitIds)
            {
                if (seen.Add(id))
                    merged.Add(id);
            }
        }

        foreach (var id in QuickNoteMarkdownReferences.ExtractAttachmentIds(markdown))
        {
            if (seen.Add(id))
                merged.Add(id);
        }

        return merged;
    }

    private static QuickNoteDetailDto MapDetail(QuickNoteEntity note)
    {
        var attachments = note.Attachments
            .Where(attachment => attachment.DeletedAt == null)
            .OrderBy(attachment => attachment.CreatedAt)
            .ThenBy(attachment => attachment.Id)
            .Select(MapAttachment)
            .ToList();

        return new QuickNoteDetailDto(
            note.Id,
            note.ContentMarkdown,
            note.Status,
            note.Source,
            attachments,
            note.MetadataJson,
            note.CreatedAt,
            note.UpdatedAt,
            note.ArchivedAt);
    }

    private static QuickNoteAttachmentDto MapAttachment(QuickNoteAttachmentEntity attachment)
    {
        var downloadUrl = $"/api/v1/quick-notes/attachments/{attachment.Id}/download";
        var previewUrl = attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? downloadUrl
            : null;

        return new QuickNoteAttachmentDto(
            attachment.Id,
            attachment.FileName,
            attachment.ContentType,
            attachment.SizeBytes,
            downloadUrl,
            previewUrl,
            attachment.CreatedAt);
    }
}
