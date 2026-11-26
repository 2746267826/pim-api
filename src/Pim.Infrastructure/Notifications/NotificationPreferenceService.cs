using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notifications;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notifications;

public sealed class NotificationPreferenceService(
    PimDbContext db,
    ICurrentUserService currentUser,
    TimeProvider clock)
{
    private Guid UserId => currentUser.UserId ?? throw new DomainException(40101, "请先登录");

    public async Task<NotificationPreferenceDto> GetAsync(
        string? deviceId,
        CancellationToken ct)
    {
        var userId = UserId;
        var normalizedDeviceId = NotificationDeviceId.NormalizeOptional(deviceId);
        var entity = await db.NotificationPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == userId, ct);
        return Map(entity, normalizedDeviceId, clock.GetUtcNow());
    }

    public async Task<NotificationPreferenceDto> UpdateAsync(
        UpdateNotificationPreferencesRequest request,
        string? deviceId,
        CancellationToken ct)
    {
        var userId = UserId;
        var normalizedDeviceId = NotificationDeviceId.NormalizeOptional(deviceId);
        var now = clock.GetUtcNow();
        var entity = await db.NotificationPreferences
            .SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (entity is null)
        {
            entity = new NotificationPreferenceEntity
            {
                UserId = userId,
                DeviceOverridesJson = "{}",
                CreatedAt = now
            };
            db.NotificationPreferences.Add(entity);
        }

        if (normalizedDeviceId.Length == 0)
        {
            ApplyAccountPreferences(entity, request);
        }
        else
        {
            if (request.InAppEnabled.HasValue) entity.InAppEnabled = request.InAppEnabled.Value;
            ApplyDevicePreferences(entity, normalizedDeviceId, request, now);
        }
        entity.Version = Guid.NewGuid();
        entity.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return Map(entity, normalizedDeviceId, now);
    }

    private static void ApplyAccountPreferences(
        NotificationPreferenceEntity entity,
        UpdateNotificationPreferencesRequest request)
    {
        if (request.InAppEnabled.HasValue) entity.InAppEnabled = request.InAppEnabled.Value;
        if (request.PushEnabled.HasValue) entity.PushEnabled = request.PushEnabled.Value;
        if (request.TaskCompleted.HasValue) entity.TaskCompleted = request.TaskCompleted.Value;
        if (request.NoteTranscriptionCompleted.HasValue)
            entity.NoteTranscriptionCompleted = request.NoteTranscriptionCompleted.Value;
        if (request.NoteDerivedCompleted.HasValue)
            entity.NoteDerivedCompleted = request.NoteDerivedCompleted.Value;
        if (request.CalendarReminder.HasValue)
            entity.CalendarReminder = request.CalendarReminder.Value;
    }

    private static void ApplyDevicePreferences(
        NotificationPreferenceEntity entity,
        string deviceId,
        UpdateNotificationPreferencesRequest request,
        DateTimeOffset now)
    {
        var overrides = NotificationDevicePreferences.Read(entity.DeviceOverridesJson);
        if (!NotificationDevicePreferences.CanAddDevice(overrides, deviceId))
            throw new DomainException(40001, "本账号通知设备数量达到上限");

        var current = overrides.GetValueOrDefault(deviceId)
                      ?? new NotificationDevicePreferenceOverride(
                          true, true, true, true, true, now);
        overrides[deviceId] = current with
        {
            PushEnabled = request.PushEnabled ?? current.PushEnabled,
            TaskCompleted = request.TaskCompleted ?? current.TaskCompleted,
            NoteTranscriptionCompleted =
                request.NoteTranscriptionCompleted ?? current.NoteTranscriptionCompleted,
            NoteDerivedCompleted =
                request.NoteDerivedCompleted ?? current.NoteDerivedCompleted,
            CalendarReminder = request.CalendarReminder ?? current.CalendarReminder,
            UpdatedAt = now
        };
        entity.DeviceOverridesJson = NotificationDevicePreferences.Write(overrides);
    }

    private static NotificationPreferenceDto Map(
        NotificationPreferenceEntity? entity,
        string deviceId,
        DateTimeOffset now) =>
        Map(entity, deviceId.Length == 0
            ? null
            : NotificationDevicePreferences.Read(entity?.DeviceOverridesJson)
                .GetValueOrDefault(deviceId), now);

    private static NotificationPreferenceDto Map(
        NotificationPreferenceEntity? entity,
        NotificationDevicePreferenceOverride? device,
        DateTimeOffset now) =>
        new(
            entity?.InAppEnabled ?? true,
            device?.PushEnabled ?? entity?.PushEnabled ?? true,
            device?.TaskCompleted ?? entity?.TaskCompleted ?? true,
            device?.NoteTranscriptionCompleted ?? entity?.NoteTranscriptionCompleted ?? true,
            device?.NoteDerivedCompleted ?? entity?.NoteDerivedCompleted ?? true,
            device?.CalendarReminder ?? entity?.CalendarReminder ?? true,
            device?.UpdatedAt ?? entity?.UpdatedAt ?? now);
}
