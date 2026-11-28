using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Notifications;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Notifications;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notifications;

[Trait("DataSource", "RealDb")]
public sealed class NotificationNoteScanTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Watermark_AdvancesAndOldTasksDoNotReplay_WhileNeedsAttentionIsNotifiedOnce()
    {
        var (temp, clock, user) = await NewDatabaseAsync();
        await using var _ = temp;
        var storage = NewStorage();
        try
        {
            var old = NotificationCategoryTests.AsrState(
                user, "Succeeded", clock.UtcNow.AddMinutes(-10));
            await WriteAsrAsync(storage, old);
            var fresh = NotificationCategoryTests.AsrState(user, "Succeeded", clock.UtcNow);
            await WriteAsrAsync(storage, fresh);

            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var scanner = NewScanner(db, storage, clock);
                await scanner.ScanCompletedNotesAsync(CancellationToken.None);
            }

            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var events = await db.NotificationEvents.IgnoreQueryFilters()
                    .Where(e => e.UserId == user)
                    .ToListAsync();
                var notification = Assert.Single(events);
                Assert.Equal(NotificationEventTypes.NoteTranscriptionCompleted, notification.EventType);
                Assert.True(File.Exists(WatermarkPath(storage)));
            }

            clock.Advance(TimeSpan.FromMinutes(1));
            var attention = NotificationCategoryTests.AsrState(
                user, "Failed", clock.UtcNow);
            await WriteAsrAsync(storage, attention);
            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var scanner = NewScanner(db, storage, clock);
                await scanner.ScanCompletedNotesAsync(CancellationToken.None);
                await scanner.ScanCompletedNotesAsync(CancellationToken.None);
            }

            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var events = await db.NotificationEvents.IgnoreQueryFilters()
                    .Where(e => e.UserId == user)
                    .OrderBy(e => e.CreatedAt)
                    .ToListAsync();
                Assert.Equal(2, events.Count);
                var attentionEvent = Assert.Single(events.Where(e =>
                    e.EventType == NotificationEventTypes.NoteTranscriptionAttention));
                Assert.Equal(
                    NotificationCategoryKeys.NotesTranscriptionAttention,
                    NotificationCategories.ForEventType(attentionEvent.EventType));
            }
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [SkippableFact]
    public async Task DerivedSucceededAndNeedsAttention_MapToSummaryReadyAndOrganizingReview()
    {
        var (temp, clock, user) = await NewDatabaseAsync();
        await using var _ = temp;
        var storage = NewStorage();
        try
        {
            await WriteDerivedAsync(
                storage,
                NotificationCategoryTests.DerivedState(user, "Succeeded", clock.UtcNow));
            await WriteDerivedAsync(
                storage,
                NotificationCategoryTests.DerivedState(user, "Failed", clock.UtcNow));

            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var scanner = NewScanner(db, storage, clock);
                await scanner.ScanCompletedNotesAsync(CancellationToken.None);
            }

            await using var verify = NotificationTestSupport.NewDbContext(temp.ConnectionString);
            var events = await verify.NotificationEvents.IgnoreQueryFilters()
                .Where(e => e.UserId == user)
                .ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Contains(events, e =>
                NotificationCategories.ForEventType(e.EventType) ==
                NotificationCategoryKeys.NotesSummaryReady);
            Assert.Contains(events, e =>
                NotificationCategories.ForEventType(e.EventType) ==
                NotificationCategoryKeys.NotesOrganizingReview);
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [SkippableFact]
    public async Task FailedEnqueue_DoesNotAdvanceTheWatermark()
    {
        var (temp, clock, user) = await NewDatabaseAsync();
        await using var _ = temp;
        await temp.ExecuteAsync("DROP TABLE notification_events CASCADE");
        var storage = NewStorage();
        try
        {
            await WriteAsrAsync(
                storage,
                NotificationCategoryTests.AsrState(user, "Succeeded", clock.UtcNow));

            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var scanner = NewScanner(db, storage, clock);
                await scanner.ScanCompletedNotesAsync(CancellationToken.None);
            }

            Assert.False(File.Exists(WatermarkPath(storage)));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData(NoteReadFailure.MalformedJson)]
    [InlineData(NoteReadFailure.IdentityMismatch)]
    public async Task UnreadableTask_DoesNotAdvanceTheWatermarkOrReplayLater(
        NoteReadFailure failure)
    {
        var (temp, clock, user) = await NewDatabaseAsync();
        await using var _ = temp;
        var storage = NewStorage();
        try
        {
            await WriteAsrAsync(
                storage,
                NotificationCategoryTests.AsrState(user, "Succeeded", clock.UtcNow));
            var brokenId = Guid.NewGuid();
            var brokenPath = Path.Combine(
                storage, "asr-tasks", user.ToString("D"), brokenId.ToString("D") + ".json");
            await File.WriteAllTextAsync(
                brokenPath,
                failure == NoteReadFailure.MalformedJson
                    ? "{ not-json"
                    : JsonSerializer.Serialize(
                        NotificationCategoryTests.AsrState(
                            Guid.NewGuid(), "Succeeded", clock.UtcNow),
                        Json));

            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var scanner = NewScanner(db, storage, clock);
                await scanner.ScanCompletedNotesAsync(CancellationToken.None);
            }

            Assert.False(
                File.Exists(WatermarkPath(storage)),
                "读任务失败必须保留水位，否则坏任务会在下一轮被静默跳过");

            await File.Delete(brokenPath);
            clock.Advance(TimeSpan.FromMinutes(1));
            await using (var db = NotificationTestSupport.NewDbContext(temp.ConnectionString))
            {
                var scanner = NewScanner(db, storage, clock);
                await scanner.ScanCompletedNotesAsync(CancellationToken.None);
            }

            await using var verify = NotificationTestSupport.NewDbContext(temp.ConnectionString);
            var events = await verify.NotificationEvents.IgnoreQueryFilters()
                .Where(e => e.UserId == user)
                .ToListAsync();
            var notification = Assert.Single(events);
            Assert.Equal(NotificationEventTypes.NoteTranscriptionCompleted, notification.EventType);
            Assert.True(File.Exists(WatermarkPath(storage)));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    [SkippableFact]
    public async Task TwoScannersInOneProcess_ShareTheFileWatermarkAndDedupeTheEvent()
    {
        var (temp, clock, user) = await NewDatabaseAsync();
        await using var _ = temp;
        var storage = NewStorage();
        try
        {
            await WriteAsrAsync(
                storage,
                NotificationCategoryTests.AsrState(user, "Succeeded", clock.UtcNow));

            await using var firstDb = NotificationTestSupport.NewDbContext(temp.ConnectionString);
            await using var secondDb = NotificationTestSupport.NewDbContext(temp.ConnectionString);
            var first = NewScanner(firstDb, storage, clock);
            var second = NewScanner(secondDb, storage, clock);
            await Task.WhenAll(
                first.ScanCompletedNotesAsync(CancellationToken.None),
                second.ScanCompletedNotesAsync(CancellationToken.None));

            await using var verify = NotificationTestSupport.NewDbContext(temp.ConnectionString);
            Assert.Equal(
                1,
                await verify.NotificationEvents.IgnoreQueryFilters()
                    .CountAsync(e => e.UserId == user));
            Assert.True(File.Exists(WatermarkPath(storage)));
        }
        finally
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    public enum NoteReadFailure
    {
        MalformedJson,
        IdentityMismatch
    }

    private static NotificationEventScanner NewScanner(
        PimDbContext db,
        string storage,
        ManualTimeProvider clock)
    {
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = storage });
        return new NotificationEventScanner(
            db,
            NotificationTestSupport.NewWriter(db, clock),
            new NoteAsrTaskStore(options),
            new NoteDerivedStore(options, clock),
            options,
            clock,
            NullLogger<NotificationEventScanner>.Instance);
    }

    private async Task<(TempMigrationDatabase Temp, ManualTimeProvider Clock, Guid User)>
        NewDatabaseAsync()
    {
        PimDbContext.RegisterModuleAssembly(
            typeof(Pim.Module.Calendar.Entities.ReminderEntity).Assembly);
        var temp = await TempMigrationDatabase.CreateAsync("test_notification_note_scan");
        await temp.MigrateAsync();
        return (
            temp,
            new ManualTimeProvider(DateTimeOffset.Parse("2026-11-28T00:00:00Z")),
            Guid.NewGuid());
    }

    private static string NewStorage()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"pim-notification-note-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WatermarkPath(string storage) =>
        Path.Combine(storage, "notification-note-scan-watermark.txt");

    private static async Task WriteAsrAsync(string storage, NoteAsrState state)
    {
        var folder = Path.Combine(storage, "asr-tasks", state.Owner.ToString("D"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, state.Request.TaskId.ToString("D") + ".json"),
            JsonSerializer.Serialize(state, Json));
    }

    private static async Task WriteDerivedAsync(string storage, NoteDerivedTaskState state)
    {
        var folder = Path.Combine(storage, "derived", state.Owner.ToString("D"), "tasks");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, state.Request.TaskId.ToString("D") + ".json"),
            JsonSerializer.Serialize(state, Json));
    }
}
