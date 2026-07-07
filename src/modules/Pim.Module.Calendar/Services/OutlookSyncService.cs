using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class OutlookSyncService
{
    private readonly PimDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOperationConfirmationService _confirmationService;
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string Provider = "outlook";
    private const string DefaultTenantId = "common";
    private const string DefaultScopes = "Calendars.ReadWrite offline_access User.Read openid profile";
    private const string StatusNotConnected = "not-connected";
    private const string TokenHealthMissing = "missing";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OutlookSyncService(
        PimDbContext db,
        IHttpClientFactory httpClientFactory,
        IOperationConfirmationService confirmationService)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _confirmationService = confirmationService;
    }

    public async Task<OutlookSettingsResponse> GetSettingsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);

        return MapSettings(connection);
    }

    public async Task<OutlookSettingsResponse> UpdateSettingsAsync(
        Guid userId,
        UpdateOutlookSettingsRequest request,
        CancellationToken ct = default)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);

        if (connection is null)
        {
            connection = new OutlookConnectionEntity
            {
                UserId = userId
            };
            _db.Set<OutlookConnectionEntity>().Add(connection);
        }

        connection.Provider = Provider;
        connection.TenantId = NormalizeTenantId(request.TenantId);
        connection.ClientId = string.IsNullOrWhiteSpace(request.ClientId)
            ? null
            : request.ClientId.Trim();
        connection.Scopes = NormalizeScopes(request.Scopes);
        connection.Status = string.IsNullOrWhiteSpace(connection.Status)
            ? StatusNotConnected
            : connection.Status;
        connection.TokenHealth = string.IsNullOrWhiteSpace(connection.TokenHealth)
            ? TokenHealthMissing
            : connection.TokenHealth;
        connection.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return MapSettings(connection);
    }

    public async Task<OutlookDeviceCodeRequestResponse> CreateDeviceCodeRequestAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(userId, ct);
        var endpoint = BuildDeviceCodeEndpoint(settings.TenantId);

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            return new OutlookDeviceCodeRequestResponse(
                endpoint,
                "https://www.microsoft.com/link",
                "PIM-DEVICE-CODE",
                DateTimeOffset.UtcNow.AddMinutes(15),
                "Open https://www.microsoft.com/link and enter code PIM-DEVICE-CODE to connect Outlook.");
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["scope"] = settings.Scopes
        });

        var client = _httpClientFactory.CreateClient(Provider);
        var response = await client.PostAsync(endpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var expiresInSeconds = json.TryGetProperty("expires_in", out var expiresIn)
            ? expiresIn.GetInt32()
            : 900;

        return new OutlookDeviceCodeRequestResponse(
            endpoint,
            GetStringProperty(json, "verification_uri", "https://www.microsoft.com/link"),
            GetStringProperty(json, "user_code", "PIM-DEVICE-CODE"),
            DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
            GetStringProperty(json, "message", "Open https://www.microsoft.com/link to connect Outlook."));
    }

    public async Task<OutlookSyncBatchResponse> SyncAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var batch = new OutlookSyncBatchEntity
        {
            UserId = userId,
            Provider = Provider,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow
        };
        var steps = new List<OutlookSyncStep>();
        var errors = new List<string>();

        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync(ct);

        try
        {
            AddStep(steps, "Load provider configuration", "started", "Loading Outlook connection settings.");
            await SaveBatchAsync(batch, steps, errors, ct);

            var connection = await _db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct);

            if (connection is null || !HasUsableAccessToken(connection))
            {
                var message = "Outlook connection or token is missing. Configure Outlook settings and connect before syncing.";
                AddStep(steps, "Load provider configuration", "failed", message);
                errors.Add(message);
                batch.Status = "failed";
                batch.FailureCount = 1;
                batch.ErrorSummary = message;
                batch.FinishedAt = DateTimeOffset.UtcNow;
                await SaveBatchAsync(batch, steps, errors, ct);
                return MapBatch(batch);
            }

            AddStep(steps, "Load provider configuration", "completed", "Outlook connection loaded.");
            await SaveBatchAsync(batch, steps, errors, ct);

            var client = CreateGraphClient(connection);
            AddStep(steps, "Fetch Outlook events", "started", "Reading calendar events from Microsoft Graph.");
            await SaveBatchAsync(batch, steps, errors, ct);

            var events = await FetchOutlookEventsAsync(client, ct);
            batch.ReadCount = events.Count;
            AddStep(steps, "Fetch Outlook events", "completed", $"Read {events.Count} event(s) from Microsoft Graph.");
            await SaveBatchAsync(batch, steps, errors, ct);

            AddStep(steps, "Apply local changes", "started", "Applying Outlook changes to the local calendar.");
            foreach (var outlookEvent in events)
            {
                var existing = await _db.Set<EventEntity>()
                    .FirstOrDefaultAsync(e =>
                        e.OutlookEventId == outlookEvent.Id &&
                        e.Calendar.UserId == userId, ct);

                if (existing is null)
                {
                    _db.Set<EventEntity>().Add(await MapOutlookEventAsync(outlookEvent, userId, ct));
                    batch.CreatedCount++;
                }
                else if (outlookEvent.LastModifiedDateTime > existing.UpdatedAt)
                {
                    var confirmation = await CreateOutlookCoreDiffConfirmationAsync(userId, existing, outlookEvent, ct);
                    if (confirmation is not null)
                    {
                        batch.ConfirmationCount++;
                    }
                }
            }

            connection.LastSyncedAt = DateTimeOffset.UtcNow;
            connection.LastError = null;
            connection.TokenHealth = "valid";
            connection.Status = "connected";
            connection.UpdatedAt = DateTimeOffset.UtcNow;

            AddStep(
                steps,
                "Apply local changes",
                "completed",
                $"Created {batch.CreatedCount}, updated {batch.UpdatedCount}, and queued {batch.ConfirmationCount} confirmation(s).");
            batch.Status = "completed";
            batch.FinishedAt = DateTimeOffset.UtcNow;
            await SaveBatchAsync(batch, steps, errors, ct);

            return MapBatch(batch);
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            AddStep(steps, "Sync failed", "failed", message);
            errors.Add(message);
            batch.Status = "failed";
            batch.FailureCount = Math.Max(batch.FailureCount, 1);
            batch.ErrorSummary = message;
            batch.FinishedAt = DateTimeOffset.UtcNow;
            await SaveBatchAsync(batch, steps, errors, ct);
            return MapBatch(batch);
        }
    }

    public async Task<IReadOnlyList<OutlookSyncBatchResponse>> ListBatchesAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var batches = await _db.Set<OutlookSyncBatchEntity>()
            .AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.StartedAt)
            .Take(20)
            .ToListAsync(ct);

        return batches.Select(MapBatch).ToList();
    }

    public async Task<OperationConfirmationDto> CreateOutlookWritebackConfirmationAsync(
        Guid userId,
        EventEntity evt,
        string action,
        CancellationToken ct = default)
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            provider = Provider,
            eventId = evt.Id,
            action
        }, JsonOptions);
        var previewJson = JsonSerializer.Serialize(new
        {
            eventId = evt.Id,
            evt.Title,
            evt.DtStart,
            evt.DtEnd,
            action
        }, JsonOptions);

        var confirmation = await _confirmationService.CreateAsync(
            new CreateOperationConfirmationRequest(
                userId,
                "calendar.outlook.writeback",
                $"Write calendar event \"{evt.Title}\" to Outlook.",
                OperationRiskLevel.L3ExternalSourceOrWriteback,
                Provider,
                payloadJson,
                previewJson,
                DateTimeOffset.UtcNow.AddHours(2),
                evt.Id.ToString("N"),
                ["title", "dtStart", "dtEnd"],
                ["review", "write_to_outlook", "skip"],
                "event",
                evt.Id,
                true),
            ct);

        return confirmation;
    }

    public async Task CreateOutlookSubscriptionAsync(Guid userId, string notificationUrl, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook is not connected.");

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

    public Task<OperationConfirmationDto> WriteToOutlookAsync(Guid userId, EventEntity evt, CancellationToken ct)
    {
        return CreateOutlookWritebackConfirmationAsync(userId, evt, "write_to_outlook", ct);
    }

    public async Task ExecuteConfirmedWriteAsync(Guid confirmationId, CancellationToken ct)
    {
        var confirmation = await _confirmationService.GetAsync(confirmationId, ct)
            ?? throw new DomainException(02006, "Confirmation does not exist.");

        if (confirmation.Status != OperationConfirmationStatus.Confirmed)
            throw new DomainException(02007, "Operation has not been confirmed.");

        var payload = JsonSerializer.Deserialize<JsonElement>(confirmation.PayloadJson);
        var eventId = payload.GetProperty("eventId").GetGuid();
        var action = payload.GetProperty("action").GetString();

        if (action == "write_to_outlook")
        {
            var userId = confirmation.RequestedByUserId
                ?? throw new DomainException(02005, "Outlook confirmation is not assigned to a user.");
            var connection = await _db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct)
                ?? throw new DomainException(02005, "Outlook is not connected.");
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
            await _confirmationService.MarkExecutedAsync(
                confirmationId,
                JsonSerializer.Serialize(new { status = "written", provider = Provider }, JsonOptions),
                ct);
        }
    }

    private HttpClient CreateGraphClient(OutlookConnectionEntity connection)
    {
        var accessToken = Encoding.UTF8.GetString(connection.AccessTokenEncrypted);
        var client = _httpClientFactory.CreateClient(Provider);
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
            ?? throw new DomainException(
                02008,
                "Default calendar was not found. Create a calendar before syncing Outlook.");

        return new EventEntity
        {
            CalendarId = defaultCalendar.Id,
            Uid = Guid.NewGuid() + "@outlook",
            Title = oe.Subject,
            Description = oe.BodyPreview,
            DtStart = oe.Start,
            DtEnd = oe.End,
            Source = Provider,
            OutlookEventId = oe.Id
        };
    }

    private async Task<OperationConfirmationDto?> CreateOutlookCoreDiffConfirmationAsync(
        Guid userId,
        EventEntity entity,
        OutlookEventInfo outlookEvent,
        CancellationToken ct)
    {
        var changedFields = new List<string>();
        if (!string.Equals(entity.Title, outlookEvent.Subject, StringComparison.Ordinal))
            changedFields.Add("title");
        if (!string.Equals(entity.Description ?? string.Empty, outlookEvent.BodyPreview ?? string.Empty, StringComparison.Ordinal))
            changedFields.Add("description");
        if (entity.DtStart != outlookEvent.Start)
            changedFields.Add("dtStart");
        if (entity.DtEnd != outlookEvent.End)
            changedFields.Add("dtEnd");

        if (changedFields.Count == 0)
        {
            return null;
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            provider = Provider,
            action = "outlook_core_diff",
            eventId = entity.Id,
            graphEventId = outlookEvent.Id
        }, JsonOptions);
        var previewJson = JsonSerializer.Serialize(new
        {
            eventId = entity.Id,
            graphEventId = outlookEvent.Id,
            before = new
            {
                entity.Title,
                entity.Description,
                entity.DtStart,
                entity.DtEnd
            },
            after = new
            {
                title = outlookEvent.Subject,
                description = outlookEvent.BodyPreview,
                dtStart = outlookEvent.Start,
                dtEnd = outlookEvent.End
            }
        }, JsonOptions);

        return await _confirmationService.CreateAsync(
            new CreateOperationConfirmationRequest(
                userId,
                "calendar.outlook.core_diff",
                $"Review Outlook changes for \"{entity.Title}\".",
                OperationRiskLevel.L3ExternalSourceOrWriteback,
                Provider,
                payloadJson,
                previewJson,
                DateTimeOffset.UtcNow.AddHours(2),
                entity.Id.ToString("N"),
                changedFields,
                ["keep_pim", "keep_outlook", "merge_by_field", "skip"],
                "event",
                entity.Id,
                true),
            ct);
    }

    private static OutlookSettingsResponse MapSettings(OutlookConnectionEntity? connection)
    {
        return new OutlookSettingsResponse(
            string.IsNullOrWhiteSpace(connection?.Provider) ? Provider : connection.Provider,
            string.IsNullOrWhiteSpace(connection?.TenantId) ? DefaultTenantId : connection.TenantId,
            connection?.ClientId,
            string.IsNullOrWhiteSpace(connection?.Scopes) ? DefaultScopes : connection.Scopes,
            string.IsNullOrWhiteSpace(connection?.Status) ? StatusNotConnected : connection.Status,
            string.IsNullOrWhiteSpace(connection?.TokenHealth) ? TokenHealthMissing : connection.TokenHealth,
            connection?.LastSyncedAt,
            connection?.LastError);
    }

    private static OutlookSyncBatchResponse MapBatch(OutlookSyncBatchEntity batch)
    {
        return new OutlookSyncBatchResponse(
            batch.Id,
            batch.Provider,
            batch.Status,
            batch.ReadCount,
            batch.CreatedCount,
            batch.UpdatedCount,
            batch.ConflictCount,
            batch.ConfirmationCount,
            batch.FailureCount,
            DeserializeSteps(batch.StepsJson),
            batch.ErrorSummary,
            batch.StartedAt,
            batch.FinishedAt);
    }

    private async Task SaveBatchAsync(
        OutlookSyncBatchEntity batch,
        IReadOnlyList<OutlookSyncStep> steps,
        IReadOnlyList<string> errors,
        CancellationToken ct)
    {
        batch.StepsJson = JsonSerializer.Serialize(steps, JsonOptions);
        batch.ErrorsJson = JsonSerializer.Serialize(errors, JsonOptions);
        await _db.SaveChangesAsync(ct);
    }

    private static void AddStep(
        ICollection<OutlookSyncStep> steps,
        string name,
        string status,
        string detail)
    {
        steps.Add(new OutlookSyncStep(name, status, detail, DateTimeOffset.UtcNow));
    }

    private static IReadOnlyList<OutlookSyncStep> DeserializeSteps(string stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
        {
            return Array.Empty<OutlookSyncStep>();
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<OutlookSyncStep>>(stepsJson, JsonOptions)
                ?? Array.Empty<OutlookSyncStep>();
        }
        catch (JsonException)
        {
            return Array.Empty<OutlookSyncStep>();
        }
    }

    private static bool HasUsableAccessToken(OutlookConnectionEntity connection)
    {
        return connection.AccessTokenEncrypted is { Length: > 0 };
    }

    private static string NormalizeTenantId(string? tenantId)
    {
        return string.IsNullOrWhiteSpace(tenantId)
            ? DefaultTenantId
            : tenantId.Trim();
    }

    private static string NormalizeScopes(string? scopes)
    {
        if (string.IsNullOrWhiteSpace(scopes))
        {
            return DefaultScopes;
        }

        return string.Join(
            " ",
            scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildDeviceCodeEndpoint(string tenantId)
    {
        return $"https://login.microsoftonline.com/{Uri.EscapeDataString(NormalizeTenantId(tenantId))}/oauth2/v2.0/devicecode";
    }

    private static string GetStringProperty(JsonElement json, string name, string fallback)
    {
        return json.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private record OutlookEventInfo(
        string Id,
        string Subject,
        string? BodyPreview,
        DateTimeOffset Start,
        DateTimeOffset End,
        DateTimeOffset LastModifiedDateTime
    );
}
