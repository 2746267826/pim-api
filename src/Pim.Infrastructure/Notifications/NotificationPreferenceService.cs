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
        if (db.Database.IsNpgsql())
        {
            return await UserSessionLock.RunAsync(db, userId, async () =>
            {
                // Two first writes can both observe "no row". Insert-or-ignore,
                // then lock the single row, serializes both first-write creation
                // and subsequent device-override JSON updates.
                var emptyDeviceOverrides = "{}";
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO notification_preferences
                        (user_id, in_app_enabled, push_enabled, task_completed,
                         note_transcription_completed, note_derived_completed,
                         calendar_reminder, device_overrides_json, version,
                         created_at, updated_at)
                    VALUES
                        ({userId}, true, true, true, true, true, true, {emptyDeviceOverrides},
                         {Guid.NewGuid()}, {now}, {now})
                    ON CONFLICT (user_id) DO NOTHING
                    """, ct);
                var entity = await db.NotificationPreferences
                    .FromSqlInterpolated($"""
                        SELECT * FROM notification_preferences
                        WHERE user_id = {userId}
                        FOR UPDATE
                        """)
                    .IgnoreQueryFilters()
                    .SingleAsync(ct);
                ApplyUpdate(entity, request, normalizedDeviceId, now);
                await db.SaveChangesAsync(ct);
                return Map(entity, normalizedDeviceId, now);
            }, ct);
        }

        var unlockedEntity = await db.NotificationPreferences
            .SingleOrDefaultAsync(p => p.UserId == userId, ct)
            ?? AddPreferenceEntity(userId, now);
        ApplyUpdate(unlockedEntity, request, normalizedDeviceId, now);
        await db.SaveChangesAsync(ct);
        return Map(unlockedEntity, normalizedDeviceId, now);
    }

    private NotificationPreferenceEntity AddPreferenceEntity(
        Guid userId,
        DateTimeOffset now)
    {
        var entity = new NotificationPreferenceEntity
        {
            UserId = userId,
            DeviceOverridesJson = "{}",
            CreatedAt = now
        };
        db.NotificationPreferences.Add(entity);
        return entity;
    }

    private static void ApplyUpdate(
        NotificationPreferenceEntity entity,
        UpdateNotificationPreferencesRequest request,
        string normalizedDeviceId,
        DateTimeOffset now)
    {
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
                          entity.PushEnabled,
                          entity.TaskCompleted,
                          entity.NoteTranscriptionCompleted,
                          entity.NoteDerivedCompleted,
                          entity.CalendarReminder,
                          now);
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
