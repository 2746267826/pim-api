using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pim.Core.Notifications;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notifications;

public sealed record NotificationEventDraft(
    string EventType,
    string DedupeKey,
    string Title,
    string Body,
    string? DetailUrl);

public enum NotificationEnqueueOutcome
{
    Enqueued,
    Duplicate,
    Disabled,
    Failed
}

public static class NotificationEventDrafts
{
    public static NotificationEventDraft TaskCompleted(
        Guid taskId,
        DateTimeOffset completedAt,
        string title) =>
        new(
            NotificationEventTypes.TaskCompleted,
            $"calendar-task-completed:{taskId:D}:{completedAt.UtcTicks}",
            $"任务已完成：{title}",
            "任务已标记为完成。",
            "/tasks");

    public static NotificationEventDraft ReminderDue(
        Guid reminderId,
        DateTimeOffset scheduledAt,
        string title,
        string? body,
        string relatedObjectType) =>
        new(
            NotificationEventTypes.CalendarReminderDue,
            $"calendar-reminder:{reminderId:D}:{scheduledAt.UtcTicks}",
            $"提醒：{title}",
            string.IsNullOrWhiteSpace(body) ? "提醒时间已到。" : body,
            relatedObjectType.Equals("confirmation", StringComparison.OrdinalIgnoreCase)
                ? "/confirmations"
                : "/reminders");

    public static NotificationEventDraft NoteTranscriptionCompleted(
        Guid taskId,
        Guid noteId,
        Guid versionId) =>
        new(
            NotificationEventTypes.NoteTranscriptionCompleted,
            $"note-transcription-completed:{taskId:D}",
            "转写已完成",
            "笔记材料的语音转写已完成，可查看逐字稿与后续整理状态。",
            $"/native-notes/processing?noteId={noteId:D}");

    public static NotificationEventDraft NoteTranscriptionAttention(
        Guid taskId,
        Guid noteId,
        Guid revision) =>
        new(
            NotificationEventTypes.NoteTranscriptionAttention,
            $"note-transcription-attention:{taskId:D}:{revision:D}",
            "转写需要处理",
            "笔记材料有转写片段失败或停止，请打开处理页检查并重试。",
            $"/native-notes/processing?noteId={noteId:D}");

    public static NotificationEventDraft NoteDerivedCompleted(
        Guid taskId,
        Guid noteId,
        Guid versionId) =>
        new(
            NotificationEventTypes.NoteDerivedCompleted,
            $"note-derived-completed:{taskId:D}",
            "整理已完成",
            "笔记材料的完整记录与独立分析已生成，可打开处理页查看。",
            $"/native-notes/processing?noteId={noteId:D}");

    public static NotificationEventDraft NoteDerivedAttention(
        Guid taskId,
        Guid noteId,
        Guid revision) =>
        new(
            NotificationEventTypes.NoteDerivedAttention,
            $"note-derived-attention:{taskId:D}:{revision:D}",
            "整理需要复核",
            "笔记整理结果不完整或需要人工复核，请打开处理页查看。",
            $"/native-notes/processing?noteId={noteId:D}");
}

public sealed class NotificationEventWriter(
    PimDbContext db,
    TimeProvider clock,
    ILogger<NotificationEventWriter> logger)
{
    public async Task<NotificationEnqueueOutcome> TryEnqueueAsync(
        Guid userId,
        NotificationEventDraft draft,
        CancellationToken ct)
    {
        if (userId == Guid.Empty) return NotificationEnqueueOutcome.Disabled;
        if (!IsKnownEventType(draft.EventType))
            throw new ArgumentException("Unknown notification event type", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.DedupeKey) || draft.DedupeKey.Length > 200)
            throw new ArgumentException("Notification dedupe key is invalid", nameof(draft));

        var preferences = await EffectivePreferencesAsync(userId, ct);
        var categoryEnabled = NotificationPreferencePolicy.IsCategoryKeyEnabled(
            NotificationCategories.ForEventType(draft.EventType),
            preferences.TaskCompleted,
            preferences.NoteTranscriptionCompleted,
            preferences.NoteDerivedCompleted,
            preferences.CalendarReminder);
        var inAppVisible = preferences.InAppEnabled && categoryEnabled;
        var pushEligible = preferences.PushEnabled && categoryEnabled;
        var eligibleSubscriptionIds = pushEligible
            ? await EligibleSubscriptionIdsAsync(userId, draft.EventType, preferences, ct)
            : [];
        var shouldFanout = pushEligible && eligibleSubscriptionIds.Count > 0;

        var now = clock.GetUtcNow();
        var entity = new NotificationEventEntity
        {
            UserId = userId,
            EventType = draft.EventType,
            DedupeKey = draft.DedupeKey.Trim(),
            Title = NormalizeText(draft.Title, 255, "PIM 通知"),
            Body = NormalizeText(draft.Body, 1000, string.Empty),
            DetailUrl = NormalizeDetailUrl(draft.DetailUrl),
            InAppVisible = inAppVisible,
            PushEligible = pushEligible,
            EligibleSubscriptionIdsJson = NotificationEventSnapshot.Write(eligibleSubscriptionIds),
            FanoutStatus = shouldFanout ? "Pending" : "Completed",
            NextFanoutAt = now,
            CreatedAt = now,
            FanoutCompletedAt = shouldFanout ? null : now
        };

        db.NotificationEvents.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            return inAppVisible || pushEligible
                ? NotificationEnqueueOutcome.Enqueued
                : NotificationEnqueueOutcome.Disabled;
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            db.Entry(entity).State = EntityState.Detached;
            return NotificationEnqueueOutcome.Duplicate;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            db.Entry(entity).State = EntityState.Detached;
            logger.LogWarning(
                "Notification event could not be persisted for user {UserId} ({ErrorType})",
                userId, error.GetType().Name);
            return NotificationEnqueueOutcome.Failed;
        }
    }

    private async Task<List<Guid>> EligibleSubscriptionIdsAsync(
        Guid userId,
        string eventType,
        NotificationPreferenceEntity preferences,
        CancellationToken ct)
    {
        var overrides = NotificationDevicePreferences.Read(preferences.DeviceOverridesJson);
        var subscriptions = await db.NotificationSubscriptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.UserId == userId &&
                        s.Channel == NotificationChannels.WebPush &&
                        s.Status == "Active")
            .Select(s => new { s.Id, s.DeviceId })
            .ToListAsync(ct);
        return subscriptions
            .Where(s => NotificationDevicePreferences.AllowsPush(
                eventType, s.DeviceId, preferences, overrides))
            .Select(s => s.Id)
            .ToList();
    }

    private async Task<NotificationPreferenceEntity> EffectivePreferencesAsync(
        Guid userId,
        CancellationToken ct) =>
        await db.NotificationPreferences
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == userId, ct)
        ?? new NotificationPreferenceEntity { UserId = userId };

    private static bool IsKnownEventType(string eventType) =>
        NotificationEventTypes.All.Contains(eventType, StringComparer.Ordinal);

    private static string NormalizeText(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var lastWasNewline = false;
        foreach (var character in value.Trim())
        {
            if (character == '\r') continue;
            if (character == '\n')
            {
                if (!lastWasNewline) builder.Append('\n');
                lastWasNewline = true;
                continue;
            }
            if (char.IsControl(character)) continue;
            builder.Append(character);
            lastWasNewline = false;
            if (builder.Length >= maxLength) break;
        }
        var result = builder.ToString().Trim();
        if (result.Length == 0) return fallback;
        if (result.Length <= maxLength) return result;
        return result[..(maxLength - 1)] + "…";
    }

    private static string? NormalizeDetailUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 500 ||
            !trimmed.StartsWith('/') ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.Any(char.IsControl))
            return null;
        return trimmed;
    }

    private static bool IsUniqueViolation(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "23505" }) return true;
            if (current.GetType().Name == "SqliteException" &&
                current.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
