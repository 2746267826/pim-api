using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;

namespace Pim.Infrastructure.Notifications;

public sealed class NotificationEventScanner(
    PimDbContext db,
    NotificationEventWriter writer,
    NoteAsrTaskStore asrTasks,
    NoteDerivedStore derivedTasks,
    IOptions<NoteMaterialUploadOptions> noteOptions,
    TimeProvider clock,
    ILogger<NotificationEventScanner> logger)
{
    private const int ReminderBatchSize = 200;
    private const int NoteCatchUpDays = 7;
    private static readonly TimeSpan FirstRunLookback = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ScanOverlap = TimeSpan.FromSeconds(30);
    private string WatermarkPath =>
        Path.Combine(
            Path.GetFullPath(noteOptions.Value.StorageRoot),
            "notification-note-scan-watermark.txt");

    public async Task ScanDueRemindersAsync(CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        var now = clock.GetUtcNow();
        var due = new List<ReminderDueRow>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using (var command = new NpgsqlCommand("""
            SELECT id, user_id, title, body, related_object_type, scheduled_at
            FROM reminders
            WHERE deleted_at IS NULL
              AND status IN ('Open', 'Snoozed')
              AND scheduled_at <= @now
              AND (notified_scheduled_at IS NULL OR notified_scheduled_at <> scheduled_at)
            ORDER BY scheduled_at
            LIMIT @limit
            """, (NpgsqlConnection)connection))
        {
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("limit", ReminderBatchSize);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                due.Add(new ReminderDueRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        foreach (var reminder in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await writer.TryEnqueueAsync(
                    reminder.UserId,
                    NotificationEventDrafts.ReminderDue(
                        reminder.Id,
                        reminder.ScheduledAt,
                        reminder.Title,
                        reminder.Body,
                        reminder.RelatedObjectType),
                    ct);
                if (outcome == NotificationEnqueueOutcome.Failed)
                {
                    logger.LogWarning(
                        "Reminder notification could not be persisted; it will be retried ({ReminderId})",
                        reminder.Id);
                    continue;
                }
                await using var mark = new NpgsqlCommand("""
                    UPDATE reminders
                    SET notified_scheduled_at = @scheduled_at
                    WHERE id = @id AND scheduled_at = @scheduled_at
                    """, (NpgsqlConnection)connection);
                mark.Parameters.AddWithValue("id", reminder.Id);
                mark.Parameters.AddWithValue("scheduled_at", reminder.ScheduledAt);
                await mark.ExecuteNonQueryAsync(ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Reminder notification scan failed for {ReminderId} ({ErrorType})",
                    reminder.Id, error.GetType().Name);
            }
        }
    }

    public async Task ScanCompletedNotesAsync(CancellationToken ct)
    {
        var scanStartedAt = clock.GetUtcNow();
        var watermark = await ReadWatermarkAsync(scanStartedAt, ct);
        var catchUpFloor = scanStartedAt.AddDays(-NoteCatchUpDays);
        if (watermark < catchUpFloor) watermark = catchUpFloor;
        var failed = false;

        foreach (var (owner, taskId) in asrTasks.Enumerate())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await asrTasks.ReadAsync(owner, taskId, ct);
                if (state.UpdatedAt <= watermark ||
                    NoteAsrTaskStore.Describe(state).Phase != "Succeeded") continue;
                var outcome = await writer.TryEnqueueAsync(
                    owner,
                    NotificationEventDrafts.NoteTranscriptionCompleted(
                        taskId, state.NoteId, state.Request.VersionId),
                    ct);
                failed |= outcome == NotificationEnqueueOutcome.Failed;
            }
            catch (Exception error)
                when (error is IOException or InvalidDataException
                    or UnauthorizedAccessException or DomainException or JsonException)
            {
                logger.LogWarning(
                    "Transcription completion scan skipped task {TaskId} ({ErrorType})",
                    taskId, error.GetType().Name);
            }
        }

        foreach (var (owner, taskId) in derivedTasks.EnumerateTasks())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await derivedTasks.ReadTaskAsync(owner, taskId, ct);
                if (state.UpdatedAt <= watermark ||
                    NoteDerivedStore.DescribePhase(state) != "Succeeded") continue;
                var outcome = await writer.TryEnqueueAsync(
                    owner,
                    NotificationEventDrafts.NoteDerivedCompleted(
                        taskId, state.NoteId, state.Request.VersionId),
                    ct);
                failed |= outcome == NotificationEnqueueOutcome.Failed;
            }
            catch (Exception error)
                when (error is IOException or InvalidDataException
                    or UnauthorizedAccessException or DomainException or JsonException)
            {
                logger.LogWarning(
                    "Derived completion scan skipped task {TaskId} ({ErrorType})",
                    taskId, error.GetType().Name);
            }
        }

        if (!failed)
            await WriteWatermarkAsync(scanStartedAt.Subtract(ScanOverlap), ct);
    }

    private async Task<DateTimeOffset> ReadWatermarkAsync(
        DateTimeOffset scanStartedAt,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(WatermarkPath))
                return scanStartedAt.Subtract(FirstRunLookback);
            var raw = await File.ReadAllTextAsync(WatermarkPath, ct);
            return DateTimeOffset.TryParse(
                raw.Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed.ToUniversalTime()
                : scanStartedAt.Subtract(FirstRunLookback);
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            logger.LogWarning(
                "Note notification scan watermark could not be read ({ErrorType})",
                error.GetType().Name);
            return scanStartedAt.Subtract(FirstRunLookback);
        }
    }

    private async Task WriteWatermarkAsync(DateTimeOffset watermark, CancellationToken ct)
    {
        var temp = WatermarkPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(WatermarkPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                temp,
                watermark.ToUniversalTime().ToString("O"),
                ct);
            File.Move(temp, WatermarkPath, overwrite: true);
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(
                "Note notification scan watermark could not be saved ({ErrorType})",
                error.GetType().Name);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception error)
                when (error is IOException or UnauthorizedAccessException)
            {
                // 临时水位文件残留不影响事件去重。
            }
        }
    }

    private sealed record ReminderDueRow(
        Guid Id,
        Guid UserId,
        string Title,
        string Body,
        string RelatedObjectType,
        DateTimeOffset ScheduledAt);
}
