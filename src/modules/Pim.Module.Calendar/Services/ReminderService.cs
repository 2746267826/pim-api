using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class ReminderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> HighRiskLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "L2PimFactChange",
        "L3ExternalSourceOrWriteback",
        "L4BatchOrDestructiveGovernance"
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public ReminderService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<ReminderResponse> CreateAsync(
        CreateReminderRequest request,
        CancellationToken ct = default)
    {
        ValidateRequired(request.Title, "Reminder title", 255);
        if (request.RelatedObjectId == Guid.Empty)
            throw new DomainException(02043, "RelatedObjectId must be a valid GUID.");
        if (request.ScheduledAt is null)
            throw new DomainException(02044, "ScheduledAt is required.");
        var entity = new ReminderEntity
        {
            UserId = UserId,
            RelatedObjectType = Normalize(request.RelatedObjectType, "object"),
            RelatedObjectId = request.RelatedObjectId,
            Title = request.Title.Trim(),
            Body = NormalizeText(request.Body),
            TriggerReason = NormalizeText(request.TriggerReason),
            RiskLevel = Normalize(request.RiskLevel, "L1LowRiskAction"),
            ChannelsJson = JsonSerializer.Serialize(NormalizeChannels(request.Channels), JsonOptions),
            DoNotDisturbStart = request.DoNotDisturbStart,
            DoNotDisturbEnd = request.DoNotDisturbEnd,
            ScheduledAt = request.ScheduledAt!.Value.ToUniversalTime(),
            Status = "Open",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Set<ReminderEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<IReadOnlyList<ReminderResponse>> ListAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var reminders = await _db.Set<ReminderEntity>()
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.ScheduledAt)
            .ToListAsync(ct);
        return reminders.Select(Map).ToList();
    }

    public async Task<ReminderResponse> SnoozeAsync(Guid id, DateTimeOffset scheduledAt, CancellationToken ct = default)
    {
        var reminder = await LoadAsync(id, ct);
        reminder.ScheduledAt = scheduledAt.ToUniversalTime();
        reminder.Status = "Snoozed";
        reminder.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(reminder);
    }

    public async Task<ReminderResponse> DismissAsync(Guid id, CancellationToken ct = default)
    {
        var reminder = await LoadAsync(id, ct);
        reminder.Status = "Dismissed";
        reminder.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(reminder);
    }

    public async Task<ReminderActionResponse> HandleActionAsync(
        Guid id,
        string action,
        CancellationToken ct = default)
    {
        var reminder = await LoadAsync(id, ct);
        var normalizedAction = Normalize(action, "open");
        if (HighRiskLevels.Contains(reminder.RiskLevel)
            && normalizedAction is not "open" and not "snooze" and not "dismiss")
        {
            await RecordDeliveryAsync(reminder, "Web", "OpenDetailRequired", normalizedAction, ct);
            return new ReminderActionResponse("OpenDetailRequired", reminder.Status, DetailUrl(reminder));
        }

        if (normalizedAction == "dismiss")
        {
            reminder.Status = "Dismissed";
        }
        else if (normalizedAction == "snooze")
        {
            reminder.Status = "Snoozed";
            reminder.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(15);
        }

        reminder.UpdatedAt = DateTimeOffset.UtcNow;
        await RecordDeliveryAsync(reminder, "Web", "Executed", normalizedAction, ct);
        await _db.SaveChangesAsync(ct);
        return new ReminderActionResponse("Executed", reminder.Status, DetailUrl(reminder));
    }

    public async Task<ReminderNotificationPayloadDto> BuildNotificationPayloadAsync(
        Guid id,
        string channel,
        CancellationToken ct = default)
    {
        var reminder = await LoadAsync(id, ct);
        var payload = new ReminderNotificationPayloadDto(
            reminder.Id,
            reminder.Title,
            reminder.Body,
            reminder.RiskLevel,
            reminder.RelatedObjectType,
            reminder.RelatedObjectId,
            DetailUrl(reminder),
            ["open", "snooze", "dismiss"]);
        await RecordDeliveryAsync(reminder, channel, "Created", null, ct, payload);
        await _db.SaveChangesAsync(ct);
        return payload;
    }

    public async Task<IReadOnlyList<ReminderDeliveryDto>> GetDeliveryLogAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var deliveries = await _db.Set<ReminderDeliveryEntity>()
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        return deliveries.Select(d => new ReminderDeliveryDto(
            d.Id,
            d.ReminderId,
            d.Channel,
            d.Status,
            d.PayloadJson,
            d.CreatedAt,
            d.RespondedAt)).ToList();
    }

    private async Task<ReminderEntity> LoadAsync(Guid id, CancellationToken ct)
        => await _db.Set<ReminderEntity>()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct)
            ?? throw new DomainException(02041, "Reminder does not exist.");

    private async Task RecordDeliveryAsync(
        ReminderEntity reminder,
        string channel,
        string status,
        string? action,
        CancellationToken ct,
        ReminderNotificationPayloadDto? payload = null)
    {
        payload ??= new ReminderNotificationPayloadDto(
            reminder.Id,
            reminder.Title,
            reminder.Body,
            reminder.RiskLevel,
            reminder.RelatedObjectType,
            reminder.RelatedObjectId,
            DetailUrl(reminder),
            ["open", "snooze", "dismiss"]);
        _db.Set<ReminderDeliveryEntity>().Add(new ReminderDeliveryEntity
        {
            ReminderId = reminder.Id,
            UserId = reminder.UserId,
            Channel = Normalize(channel, "Web"),
            Status = status,
            Action = action,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow,
            RespondedAt = action is null ? null : DateTimeOffset.UtcNow
        });
        await Task.CompletedTask;
    }

    private static ReminderResponse Map(ReminderEntity entity)
        => new(
            entity.Id,
            entity.RelatedObjectType,
            entity.RelatedObjectId,
            entity.Title,
            entity.Body,
            entity.TriggerReason,
            entity.RiskLevel,
            ReadChannels(entity.ChannelsJson),
            entity.DoNotDisturbStart,
            entity.DoNotDisturbEnd,
            entity.ScheduledAt,
            entity.Status);

    private static IReadOnlyList<string> ReadChannels(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string[] NormalizeChannels(IReadOnlyList<string>? channels)
        => channels is null
            ? []
            : channels
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string DetailUrl(ReminderEntity reminder)
        => reminder.RelatedObjectType.Equals("confirmation", StringComparison.OrdinalIgnoreCase)
            ? $"/confirmations/{reminder.RelatedObjectId}"
            : $"/reminders/{reminder.Id}";

    private static void ValidateRequired(string? value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new DomainException(02042, $"{fieldName} must be 1-{maxLength} characters.");
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
