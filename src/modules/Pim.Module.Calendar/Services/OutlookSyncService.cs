using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public class OutlookSyncService
{
    private readonly PimDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOperationConfirmationService _confirmationService;
    private readonly OutlookTokenService? _tokenService;
    private readonly IMicrosoftGraphClient? _graphClient;
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
        : this(db, httpClientFactory, confirmationService, null, null)
    {
    }

    public OutlookSyncService(
        PimDbContext db,
        IHttpClientFactory httpClientFactory,
        IOperationConfirmationService confirmationService,
        OutlookTokenService? tokenService,
        IMicrosoftGraphClient? graphClient)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _confirmationService = confirmationService;
        _tokenService = tokenService;
        _graphClient = graphClient;
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
                "Open https://www.microsoft.com/link and enter code PIM-DEVICE-CODE to connect Outlook.",
                "PIM-DEVICE-CODE");
        }

        var result = await GraphClient.RequestDeviceCodeAsync(
            settings.TenantId,
            settings.ClientId,
            settings.Scopes,
            ct);

        return new OutlookDeviceCodeRequestResponse(
            endpoint,
            result.VerificationUri,
            result.UserCode,
            DateTimeOffset.UtcNow.AddSeconds(result.ExpiresInSeconds),
            result.Message,
            result.DeviceCode);
    }

    public async Task<OutlookSettingsResponse> PollDeviceCodeAsync(
        Guid userId,
        string deviceCode,
        CancellationToken ct = default)
    {
        if (_tokenService is null)
        {
            throw new DomainException(02036, "Outlook token service is not configured.");
        }

        var connection = await GetOrCreateConnectionAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(connection.ClientId))
        {
            throw new DomainException(02037, "Outlook client id is required before polling device code.");
        }

        var token = await GraphClient.PollDeviceCodeAsync(
            connection.TenantId,
            connection.ClientId,
            deviceCode,
            ct);
        _tokenService.StoreTokens(connection, token, DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);
        return MapSettings(connection);
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

            var accessToken = connection is null
                ? null
                : await GetAccessTokenAsync(connection, ct);

            if (connection is null || string.IsNullOrWhiteSpace(accessToken))
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

            AddStep(steps, "Validate token", "completed", $"Token health: {connection.TokenHealth}.");
            AddStep(steps, "Read calendar delta", "started", "Reading calendar changes from Microsoft Graph.");
            await SaveBatchAsync(batch, steps, errors, ct);

            var nextUrl = string.IsNullOrWhiteSpace(connection.DeltaLink)
                ? BuildInitialDeltaUrl()
                : connection.DeltaLink;
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                var page = await GraphClient.GetDeltaPageAsync(accessToken, nextUrl, ct);
                batch.ReadCount += page.Events.Count;

                foreach (var outlookEvent in page.Events)
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
                    else
                    {
                        var confirmation = await CreateOutlookCoreDiffConfirmationAsync(userId, existing, outlookEvent, ct);
                        if (confirmation is not null)
                        {
                            batch.ConfirmationCount++;
                            batch.ConflictCount++;
                        }
                        else
                        {
                            existing.OutlookChangeKey = outlookEvent.ChangeKey;
                            existing.OutlookEtag = outlookEvent.ETag;
                            existing.UpdatedAt = DateTimeOffset.UtcNow;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(page.NextLink))
                {
                    AddStep(steps, "Follow nextLink", "completed", page.NextLink);
                    nextUrl = page.NextLink;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(page.DeltaLink))
                {
                    connection.DeltaLink = page.DeltaLink;
                    AddStep(steps, "Store deltaLink", "completed", page.DeltaLink);
                }

                nextUrl = null;
            }

            connection.LastSyncedAt = DateTimeOffset.UtcNow;
            connection.LastError = null;
            connection.TokenHealth = string.IsNullOrWhiteSpace(connection.TokenHealth)
                ? "healthy"
                : connection.TokenHealth;
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
            graphEventId = evt.OutlookEventId,
            changeKey = evt.OutlookChangeKey,
            action
        }, JsonOptions);
        var previewJson = JsonSerializer.Serialize(new
        {
            eventId = evt.Id,
            evt.Title,
            evt.Location,
            evt.DtStart,
            evt.DtEnd,
            action
        }, JsonOptions);

        var confirmation = await _confirmationService.CreateAsync(
            new CreateOperationConfirmationRequest(
                userId,
                "outlook.writeback",
                $"Write calendar event \"{evt.Title}\" to Outlook.",
                OperationRiskLevel.L3ExternalSourceOrWriteback,
                Provider,
                payloadJson,
                previewJson,
                DateTimeOffset.UtcNow.AddHours(2),
                evt.Id.ToString("N"),
                ["title", "location", "dtStart", "dtEnd"],
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

    public async Task ExecuteConfirmedWriteAsync(Guid confirmationId, CancellationToken ct = default)
    {
        var confirmation = await _confirmationService.GetAsync(confirmationId, ct)
            ?? throw new DomainException(02006, "Confirmation does not exist.");

        if (confirmation.Status != OperationConfirmationStatus.Confirmed)
            throw new DomainException(02007, "Operation has not been confirmed.");

        var payload = JsonSerializer.Deserialize<JsonElement>(confirmation.PayloadJson);
        var eventId = payload.GetProperty("eventId").GetGuid();
        var action = payload.GetProperty("action").GetString();

        if (action == "write_to_outlook"
            || confirmation.OperationType is "outlook.writeback" or "outlook.conflict.keep_pim" or "outlook.conflict.merge")
        {
            var userId = confirmation.RequestedByUserId
                ?? throw new DomainException(02005, "Outlook confirmation is not assigned to a user.");
            var connection = await _db.Set<OutlookConnectionEntity>()
                .FirstOrDefaultAsync(c => c.UserId == userId, ct)
                ?? throw new DomainException(02005, "Outlook is not connected.");
            var accessToken = await GetAccessTokenAsync(connection, ct)
                ?? throw new DomainException(02005, "Outlook token is not available.");
            var evt = await _db.Set<EventEntity>().FindAsync(new object[] { eventId }, ct)
                ?? throw new DomainException(02001, "Event does not exist.");
            if (string.IsNullOrWhiteSpace(evt.OutlookEventId))
                throw new DomainException(02038, "Event is not linked to an Outlook event.");

            var before = new
            {
                evt.Title,
                evt.Description,
                evt.Location,
                evt.DtStart,
                evt.DtEnd,
                evt.OutlookChangeKey
            };
            var patch = new
            {
                subject = evt!.Title,
                body = new { contentType = "text", content = evt.Description ?? "" },
                location = new { displayName = evt.Location ?? "" },
                start = new { dateTime = evt.DtStart.ToString("o"), timeZone = "UTC" },
                end = new { dateTime = evt.DtEnd.ToString("o"), timeZone = "UTC" }
            };

            var patched = await GraphClient.PatchEventAsync(
                accessToken,
                evt.OutlookEventId,
                evt.OutlookChangeKey ?? "*",
                patch,
                ct);
            evt.OutlookChangeKey = patched.ChangeKey ?? evt.OutlookChangeKey;
            evt.OutlookEtag = patched.ETag ?? evt.OutlookEtag;
            evt.UpdatedAt = DateTimeOffset.UtcNow;
            await new AuditVersionService(_db).RecordAsync(
                "event",
                evt.Id,
                before,
                new
                {
                    evt.Title,
                    evt.Description,
                    evt.Location,
                    evt.DtStart,
                    evt.DtEnd,
                    evt.OutlookChangeKey
                },
                ["title", "location", "dtStart", "dtEnd"],
                confirmationId,
                Provider,
                userId,
                ct);
            await _confirmationService.MarkExecutedAsync(
                confirmationId,
                JsonSerializer.Serialize(new { status = "written", provider = Provider }, JsonOptions),
                ct);
        }
    }

    private HttpClient CreateGraphClient(OutlookConnectionEntity connection)
    {
        var accessToken = _tokenService is null
            ? Encoding.UTF8.GetString(connection.AccessTokenEncrypted)
            : _tokenService.Unprotect(connection.AccessTokenEncrypted);
        var client = _httpClientFactory.CreateClient(Provider);
        client.BaseAddress = new Uri(GraphBaseUrl);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<EventEntity> MapOutlookEventAsync(GraphEvent oe, Guid userId, CancellationToken ct)
    {
        var defaultCalendar = await _db.Set<CalendarEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.IsDefault, ct)
            ?? throw new DomainException(
                02008,
                "Default calendar was not found. Create a calendar before syncing Outlook.");

        return new EventEntity
        {
            CalendarId = defaultCalendar.Id,
            Uid = string.IsNullOrWhiteSpace(oe.ICalUId) ? Guid.NewGuid() + "@outlook" : oe.ICalUId,
            Title = oe.Subject,
            Description = oe.BodyPreview,
            Location = oe.Location,
            DtStart = ParseGraphDateTime(oe.Start),
            DtEnd = ParseGraphDateTime(oe.End),
            Source = Provider,
            OutlookEventId = oe.Id,
            OutlookChangeKey = oe.ChangeKey,
            OutlookEtag = oe.ETag,
            SourceUid = oe.ICalUId
        };
    }

    private async Task<OperationConfirmationDto?> CreateOutlookCoreDiffConfirmationAsync(
        Guid userId,
        EventEntity entity,
        GraphEvent outlookEvent,
        CancellationToken ct)
    {
        var startsAt = ParseGraphDateTime(outlookEvent.Start);
        var endsAt = ParseGraphDateTime(outlookEvent.End);
        var changedFields = new List<string>();
        if (!string.Equals(entity.Title, outlookEvent.Subject, StringComparison.Ordinal))
            changedFields.Add("title");
        if (!string.Equals(entity.Description ?? string.Empty, outlookEvent.BodyPreview ?? string.Empty, StringComparison.Ordinal))
            changedFields.Add("description");
        if (!string.Equals(entity.Location ?? string.Empty, outlookEvent.Location ?? string.Empty, StringComparison.Ordinal))
            changedFields.Add("location");
        if (entity.DtStart != startsAt)
            changedFields.Add("dtStart");
        if (entity.DtEnd != endsAt)
            changedFields.Add("dtEnd");

        if (changedFields.Count == 0)
        {
            return null;
        }

        var pimSnapshotJson = JsonSerializer.Serialize(new
        {
            entity.Title,
            entity.Description,
            entity.Location,
            entity.DtStart,
            entity.DtEnd
        }, JsonOptions);
        var externalSnapshotJson = JsonSerializer.Serialize(new
        {
            title = outlookEvent.Subject,
            description = outlookEvent.BodyPreview,
            location = outlookEvent.Location,
            dtStart = startsAt,
            dtEnd = endsAt,
            changeKey = outlookEvent.ChangeKey
        }, JsonOptions);
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
            before = JsonSerializer.Deserialize<JsonElement>(pimSnapshotJson, JsonOptions),
            after = JsonSerializer.Deserialize<JsonElement>(externalSnapshotJson, JsonOptions)
        }, JsonOptions);

        var confirmation = await _confirmationService.CreateAsync(
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
                true,
                BeforeJson: pimSnapshotJson,
                AfterJson: externalSnapshotJson,
                ExternalEffect: string.IsNullOrWhiteSpace(outlookEvent.Id) ? null : $"GraphEventId={outlookEvent.Id}",
                RecoveryPath: "Review the Outlook conflict queue before applying external changes."),
            ct);

        await UpsertCoreDiffConflictAsync(
            userId,
            entity.Id,
            outlookEvent.Id,
            pimSnapshotJson,
            externalSnapshotJson,
            confirmation.Id,
            ct);

        return confirmation;
    }

    private async Task UpsertCoreDiffConflictAsync(
        Guid userId,
        Guid eventId,
        string? graphEventId,
        string pimSnapshotJson,
        string externalSnapshotJson,
        Guid confirmationId,
        CancellationToken ct)
    {
        var conflict = await _db.Set<SyncConflictEntity>()
            .FirstOrDefaultAsync(c =>
                c.UserId == userId
                && c.Provider == Provider
                && c.ObjectType == "event"
                && c.ObjectId == eventId
                && c.GraphEventId == graphEventId
                && c.ConflictKind == "core-diff"
                && c.Status != "resolved", ct);

        if (conflict is null)
        {
            conflict = new SyncConflictEntity
            {
                UserId = userId,
                Provider = Provider,
                ObjectType = "event",
                ObjectId = eventId,
                GraphEventId = graphEventId,
                ConflictKind = "core-diff",
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.Set<SyncConflictEntity>().Add(conflict);
        }

        conflict.Status = "pending-confirmation";
        conflict.PimSnapshotJson = pimSnapshotJson;
        conflict.ExternalSnapshotJson = externalSnapshotJson;
        conflict.ResolvedConfirmationId = confirmationId;
        conflict.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private IMicrosoftGraphClient GraphClient
        => _graphClient ?? new MicrosoftGraphDeviceCodeClient(_httpClientFactory);

    private async Task<OutlookConnectionEntity> GetOrCreateConnectionAsync(
        Guid userId,
        CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (connection is not null)
        {
            return connection;
        }

        connection = new OutlookConnectionEntity
        {
            UserId = userId,
            Provider = Provider,
            TenantId = DefaultTenantId,
            Scopes = DefaultScopes,
            Status = StatusNotConnected,
            TokenHealth = TokenHealthMissing
        };
        _db.Set<OutlookConnectionEntity>().Add(connection);
        await _db.SaveChangesAsync(ct);
        return connection;
    }

    private async Task<string?> GetAccessTokenAsync(
        OutlookConnectionEntity connection,
        CancellationToken ct)
    {
        if (_tokenService is not null)
        {
            return await _tokenService.GetValidAccessTokenAsync(connection, GraphClient, ct);
        }

        return HasUsableAccessToken(connection)
            ? Encoding.UTF8.GetString(connection.AccessTokenEncrypted)
            : null;
    }

    private static string BuildInitialDeltaUrl()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-30).ToString("o");
        var end = DateTimeOffset.UtcNow.AddDays(180).ToString("o");
        return $"{GraphBaseUrl}/me/calendarView/delta?startDateTime={Uri.EscapeDataString(start)}&endDateTime={Uri.EscapeDataString(end)}";
    }

    private static DateTimeOffset ParseGraphDateTime(GraphDateTimeTimeZone value)
    {
        if (DateTimeOffset.TryParse(value.DateTime, out var offset))
        {
            return offset.ToUniversalTime();
        }

        return DateTime.SpecifyKind(DateTime.Parse(value.DateTime), DateTimeKind.Utc);
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

}
