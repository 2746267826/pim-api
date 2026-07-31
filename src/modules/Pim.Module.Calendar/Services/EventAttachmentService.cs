using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Files.Entities;

namespace Pim.Module.Calendar.Services;

public sealed record EventAttachmentDownload(Stream Content, string ContentType, string FileName);

public sealed class EventAttachmentService
{
    private readonly PimDbContext _db;
    private readonly GraphCalendarClient? _graph;

    public EventAttachmentService(PimDbContext db)
    {
        _db = db;
    }

    public EventAttachmentService(PimDbContext db, GraphCalendarClient graph)
        : this(db)
    {
        _graph = graph;
    }

    public async Task<IReadOnlyList<EventAttachmentReferenceDto>> GetOutlookAttachmentReferencesAsync(
        Guid connectionId,
        string calendarId,
        string eventId,
        CancellationToken ct)
    {
        var graph = _graph ?? throw new InvalidOperationException(
            "A Graph client is required to load Outlook attachment metadata.");
        var references = new List<EventAttachmentReferenceDto>();

        await foreach (var page in graph.GetEventAttachmentsAsync(connectionId, calendarId, eventId, ct))
        {
            foreach (var item in page.Items)
            {
                if (!IsFileAttachment(item) || ReadBool(item, "isInline"))
                    continue;

                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                references.Add(new EventAttachmentReferenceDto(
                    "outlook",
                    id,
                    ReadString(item, "name") ?? id,
                    ReadString(item, "contentType"),
                    ReadLong(item, "size"),
                    true));
            }
        }

        return references;
    }

    public async Task ValidatePimFileReferenceAsync(
        Guid userId,
        EventAttachmentReferenceDto reference,
        CancellationToken ct)
    {
        if (!string.Equals(reference.Kind, "pimFile", StringComparison.OrdinalIgnoreCase))
            return;

        if (!Guid.TryParse(reference.Id, out var itemId))
            throw new DomainException(02009, "PIM file attachment reference is invalid.");

        var exists = await _db.Set<FileItemEntity>()
            .Include(item => item.Provider)
            .AnyAsync(item => item.Id == itemId
                && !item.IsDeleted
                && item.ItemType == "file"
                && item.Provider != null
                && item.Provider.UserId == userId, ct);

        if (!exists)
            throw new DomainException(02009, "PIM file attachment reference is unavailable.");
    }

    public async Task<EventAttachmentDownload?> DownloadOutlookAttachmentAsync(
        Guid userId,
        Guid eventId,
        string attachmentId,
        CancellationToken ct)
    {
        var graph = _graph ?? throw new InvalidOperationException(
            "A Graph client is required to download Outlook attachments.");
        var eventEntity = await _db.Set<Entities.EventEntity>()
            .Include(item => item.Calendar)
            .FirstOrDefaultAsync(item => item.Id == eventId
                && item.DeletedAt == null
                && item.Calendar.UserId == userId
                && item.OutlookConnectionId != null
                && item.OutlookCalendarBindingId != null
                && item.OutlookEventId != null
                && item.Source.StartsWith("outlook"), ct);

        if (eventEntity is null || string.IsNullOrWhiteSpace(eventEntity.OutlookEventId))
            return null;

        var connection = await _db.Set<Entities.OutlookConnectionEntity>()
            .FirstOrDefaultAsync(item => item.Id == eventEntity.OutlookConnectionId
                && item.UserId == userId
                && item.Status == "connected", ct);
        if (connection is null)
            return null;

        var binding = await _db.Set<Entities.OutlookCalendarBindingEntity>()
            .FirstOrDefaultAsync(item => item.Id == eventEntity.OutlookCalendarBindingId
                && item.ConnectionId == connection.Id
                && item.IsSelected
                && item.RemoteState == "active", ct);
        if (binding is null)
            return null;

        var reference = EventFieldCodec.DeserializeAttachments(eventEntity.AttachmentReferencesJson)
            .FirstOrDefault(item => item.CanDownload
                && string.Equals(item.Kind, "outlook", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Id, attachmentId, StringComparison.Ordinal));
        if (reference is null)
            return null;

        GraphBinaryContent content;
        try
        {
            content = await graph.DownloadEventAttachmentAsync(
                connection.Id, binding.GraphCalendarId, eventEntity.OutlookEventId, attachmentId, ct);
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (OutlookReauthenticationRequiredException)
        {
            // The connection was already verified to belong to this user: record
            // the reauthentication requirement with safe, token-free state, then
            // surface a caller-visible 409 at the HTTP layer.
            await PersistReauthenticationRequiredAsync(connection);
            throw;
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // A transient retry sequence (e.g. 503, 503, 401) can exhaust the
            // read budget on the final 401, surfacing a GraphRequestException
            // instead of OutlookReauthenticationRequiredException. Treat it the
            // same way: safe state persistence plus a caller-visible 409.
            await PersistReauthenticationRequiredAsync(connection);
            throw new OutlookReauthenticationRequiredException("graph-unauthorized", ex);
        }

        return new EventAttachmentDownload(
            content.Content,
            content.ContentType,
            GraphBinaryContent.SanitizeFileName(reference.Name));
    }

    private async Task PersistReauthenticationRequiredAsync(Entities.OutlookConnectionEntity connection)
    {
        connection.Status = "reauth-required";
        connection.TokenHealth = "interaction-required";
        connection.LastError = "Outlook 连接需要重新授权。";
        connection.Version = checked(connection.Version + 1);
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent request already persisted a newer connection state
            // (e.g. reconnected); this stale context must not overwrite it. The
            // reauthentication business exception still propagates unchanged.
        }
    }

    private static bool IsFileAttachment(JsonElement item)
        => string.Equals(
            ReadString(item, "@odata.type"),
            "#microsoft.graph.fileAttachment",
            StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static long? ReadLong(JsonElement item, string property)
        => item.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result)
                ? result
                : null;
}
