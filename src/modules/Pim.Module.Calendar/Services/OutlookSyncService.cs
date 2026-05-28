using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class OutlookSyncService
{
    private readonly PimDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    public OutlookSyncService(PimDbContext db, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
    }

    public async Task SyncAsync(Guid userId, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook 未连接");

        var client = CreateGraphClient(connection);
        var events = await FetchOutlookEventsAsync(client, ct);

        foreach (var outlookEvent in events)
        {
            var existing = await _db.Set<EventEntity>()
                .FirstOrDefaultAsync(e =>
                    e.OutlookEventId == outlookEvent.Id &&
                    e.Calendar.UserId == userId, ct);

            if (existing is null)
            {
                _db.Set<EventEntity>().Add(await MapOutlookEventAsync(outlookEvent, userId, ct));
            }
            else if (outlookEvent.LastModifiedDateTime > existing.UpdatedAt)
            {
                UpdateFromOutlookEvent(existing, outlookEvent);
            }
        }

        connection.LastSyncedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task CreateOutlookSubscriptionAsync(Guid userId, string notificationUrl, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook 未连接");

        var client = CreateGraphClient(connection);

        var response = await client.PostAsJsonAsync("/subscriptions", new
        {
            changeType = "created,updated,deleted",
            notificationUrl,
            resource = "me/events",
            expirationDateTime = DateTimeOffset.UtcNow.AddDays(3).ToString("o"),
            clientState = userId.ToString()
        }, ct);

        response.EnsureSuccessStatusCode();
        var subscription = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        connection.SubscriptionId = subscription.GetProperty("id").GetString();
        connection.SubscriptionExpiresAt =
            DateTimeOffset.Parse(subscription.GetProperty("expirationDateTime").GetString()!);
        await _db.SaveChangesAsync(ct);
    }

    public async Task WriteToOutlookAsync(Guid userId, EventEntity evt, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook 未连接");

        // Create pending confirmation for write operation
        _db.Set<PendingConfirmationEntity>().Add(new PendingConfirmationEntity
        {
            UserId = userId,
            Type = "outlook_write",
            Summary = $"将日程“{evt.Title}”写入 Outlook？",
            Payload = JsonSerializer.Serialize(new { eventId = evt.Id, action = "write_to_outlook" })
        });

        await _db.SaveChangesAsync(ct);
    }

    public async Task ExecuteConfirmedWriteAsync(Guid confirmationId, CancellationToken ct)
    {
        var confirmation = await _db.Set<PendingConfirmationEntity>()
            .FindAsync(new object[] { confirmationId }, ct)
            ?? throw new DomainException(02006, "操作确认不存在");

        if (confirmation.Status != "confirmed")
            throw new DomainException(02007, "操作尚未确认");

        var payload = JsonSerializer.Deserialize<JsonElement>(confirmation.Payload);
        var eventId = payload.GetProperty("eventId").GetGuid();
        var action = payload.GetProperty("action").GetString();

        if (action == "write_to_outlook")
        {
            var connection = await _db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == confirmation.UserId, ct)!;
            var client = CreateGraphClient(connection);
            var evt = await _db.Set<EventEntity>().FindAsync(new object[] { eventId }, ct);

            var outlookEvent = new
            {
                subject = evt!.Title,
                body = new { contentType = "text", content = evt.Description ?? "" },
                start = new { dateTime = evt.DtStart.ToString("o"), timeZone = "UTC" },
                end = new { dateTime = evt.DtEnd.ToString("o"), timeZone = "UTC" }
            };

            var response = await client.PostAsJsonAsync("/me/events", outlookEvent, ct);
            response.EnsureSuccessStatusCode();
        }
    }

    private HttpClient CreateGraphClient(OutlookConnectionEntity connection)
    {
        var accessToken = Encoding.UTF8.GetString(
            connection.AccessTokenEncrypted);
        var client = _httpClientFactory.CreateClient("outlook");
        client.BaseAddress = new Uri(GraphBaseUrl);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<List<OutlookEventInfo>> FetchOutlookEventsAsync(
        HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync(
            "/me/calendar/events?$top=100&$orderby=lastModifiedDateTime desc", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var items = json.GetProperty("value");

        var events = new List<OutlookEventInfo>();
        foreach (var item in items.EnumerateArray())
        {
            events.Add(new OutlookEventInfo(
                item.GetProperty("id").GetString()!,
                item.GetProperty("subject").GetString() ?? "",
                item.GetProperty("bodyPreview").GetString(),
                DateTimeOffset.Parse(item.GetProperty("start").GetProperty("dateTime").GetString()!),
                DateTimeOffset.Parse(item.GetProperty("end").GetProperty("dateTime").GetString()!),
                DateTimeOffset.Parse(item.GetProperty("lastModifiedDateTime").GetString()!)
            ));
        }
        return events;
    }

    private async Task<EventEntity> MapOutlookEventAsync(OutlookEventInfo oe, Guid userId, CancellationToken ct)
    {
        var defaultCalendar = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsDefault, ct)
            ?? throw new DomainException(02008, "未找到默认日历，请先创建日历。");

        return new EventEntity
        {
            CalendarId = defaultCalendar.Id,
            Uid = Guid.NewGuid() + "@outlook",
            Title = oe.Subject,
            Description = oe.BodyPreview,
            DtStart = oe.Start,
            DtEnd = oe.End,
            Source = "outlook",
            OutlookEventId = oe.Id
        };
    }

    private void UpdateFromOutlookEvent(EventEntity entity, OutlookEventInfo oe)
    {
        entity.Title = oe.Subject;
        entity.Description = oe.BodyPreview;
        entity.DtStart = oe.Start;
        entity.DtEnd = oe.End;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private record OutlookEventInfo(
        string Id, string Subject, string? BodyPreview,
        DateTimeOffset Start, DateTimeOffset End,
        DateTimeOffset LastModifiedDateTime
    );
}
