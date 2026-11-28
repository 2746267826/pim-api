using Pim.Core.Notes;
using Pim.Core.Notifications;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Notifications;
using Xunit;

namespace Pim.UnitTests.Notifications;

public sealed class NotificationCategoryTests
{
    [Fact]
    public void Q06CategoryKeys_AreStableExactStrings()
    {
        Assert.Equal("notes.summary_ready", NotificationCategoryKeys.NotesSummaryReady);
        Assert.Equal(
            "notes.transcription_attention",
            NotificationCategoryKeys.NotesTranscriptionAttention);
        Assert.Equal("notes.organizing_review", NotificationCategoryKeys.NotesOrganizingReview);
        Assert.Equal(
            "uploads.attention_digest",
            NotificationCategoryKeys.UploadsAttentionDigest);
        Assert.Equal(
            "notes.summary_ready,notes.transcription_attention," +
            "notes.organizing_review,uploads.attention_digest",
            string.Join(",", NotificationCategoryKeys.Required));
    }

    [Theory]
    [InlineData(
        NotificationEventTypes.NoteDerivedCompleted,
        NotificationCategoryKeys.NotesSummaryReady)]
    [InlineData(
        NotificationEventTypes.NoteTranscriptionAttention,
        NotificationCategoryKeys.NotesTranscriptionAttention)]
    [InlineData(
        NotificationEventTypes.NoteDerivedAttention,
        NotificationCategoryKeys.NotesOrganizingReview)]
    [InlineData(
        NotificationEventTypes.UploadAttentionDigest,
        NotificationCategoryKeys.UploadsAttentionDigest)]
    public void EventTypes_MapToStableCategoryKeys(string eventType, string expected)
    {
        Assert.Equal(expected, NotificationCategories.ForEventType(eventType));
    }

    [Fact]
    public void TaskCalendarAndTestEvents_KeepTheirEventTypeAsCategoryKey()
    {
        Assert.Equal(
            NotificationEventTypes.TaskCompleted,
            NotificationCategories.ForEventType(NotificationEventTypes.TaskCompleted));
        Assert.Equal(
            NotificationEventTypes.CalendarReminderDue,
            NotificationCategories.ForEventType(NotificationEventTypes.CalendarReminderDue));
        Assert.Equal(
            NotificationEventTypes.Test,
            NotificationCategories.ForEventType(NotificationEventTypes.Test));
    }

    [Fact]
    public void UploadAttentionDigest_IsReservedWithoutAProducerInThisSlice()
    {
        Assert.DoesNotContain(
            NotificationEventTypes.UploadAttentionDigest,
            NotificationEventTypes.All);
        Assert.DoesNotContain(
            NotificationCategoryKeys.UploadsAttentionDigest,
            NotificationCategories.ProducibleKeys);
        Assert.Contains(
            NotificationCategoryKeys.UploadsAttentionDigest,
            NotificationCategories.AllKeys);
    }

    [Fact]
    public void Q06AttentionCategories_ReuseExistingDevicePreferenceToggles()
    {
        Assert.False(NotificationPreferencePolicy.IsCategoryEnabled(
            NotificationEventTypes.NoteTranscriptionAttention,
            taskCompleted: true,
            noteTranscriptionCompleted: false,
            noteDerivedCompleted: true,
            calendarReminder: true));
        Assert.True(NotificationPreferencePolicy.IsCategoryEnabled(
            NotificationEventTypes.NoteTranscriptionAttention,
            taskCompleted: false,
            noteTranscriptionCompleted: true,
            noteDerivedCompleted: false,
            calendarReminder: false));

        Assert.False(NotificationPreferencePolicy.IsCategoryEnabled(
            NotificationEventTypes.NoteDerivedAttention,
            taskCompleted: true,
            noteTranscriptionCompleted: true,
            noteDerivedCompleted: false,
            calendarReminder: true));
        Assert.True(NotificationPreferencePolicy.IsCategoryEnabled(
            NotificationEventTypes.NoteDerivedCompleted,
            taskCompleted: false,
            noteTranscriptionCompleted: false,
            noteDerivedCompleted: true,
            calendarReminder: false));
    }

    [Fact]
    public void ScanPolicy_EmitsSucceededAndAttentionCandidatesWithQ06Categories()
    {
        var now = DateTimeOffset.Parse("2026-11-28T00:00:00Z");
        var owner = Guid.NewGuid();
        var succeeded = AsrState(owner, "Succeeded", now);
        var needsAttention = AsrState(owner, "Failed", now);
        var derivedSucceeded = DerivedState(owner, "Succeeded", now);
        var derivedAttention = DerivedState(owner, "Failed", now);
        var watermark = now.AddMinutes(-1);

        var completed = Assert.IsType<NotificationEventDraft>(
            NotificationScanPolicy.ForTranscription(succeeded, watermark));
        Assert.Equal(NotificationEventTypes.NoteTranscriptionCompleted, completed.EventType);
        Assert.Equal(
            NotificationCategories.ForEventType(completed.EventType),
            NotificationCategories.ForEventType(NotificationEventTypes.NoteTranscriptionCompleted));

        var attention = Assert.IsType<NotificationEventDraft>(
            NotificationScanPolicy.ForTranscription(needsAttention, watermark));
        Assert.Equal(NotificationEventTypes.NoteTranscriptionAttention, attention.EventType);
        Assert.Equal(
            NotificationCategoryKeys.NotesTranscriptionAttention,
            NotificationCategories.ForEventType(attention.EventType));

        var summary = Assert.IsType<NotificationEventDraft>(
            NotificationScanPolicy.ForDerived(derivedSucceeded, watermark));
        Assert.Equal(NotificationEventTypes.NoteDerivedCompleted, summary.EventType);
        Assert.Equal(
            NotificationCategoryKeys.NotesSummaryReady,
            NotificationCategories.ForEventType(summary.EventType));

        var review = Assert.IsType<NotificationEventDraft>(
            NotificationScanPolicy.ForDerived(derivedAttention, watermark));
        Assert.Equal(NotificationEventTypes.NoteDerivedAttention, review.EventType);
        Assert.Equal(
            NotificationCategoryKeys.NotesOrganizingReview,
            NotificationCategories.ForEventType(review.EventType));
    }

    [Fact]
    public void ScanPolicy_DoesNotReplayTasksAtOrBeforeTheWatermark()
    {
        var now = DateTimeOffset.Parse("2026-11-28T00:00:00Z");
        var owner = Guid.NewGuid();
        var asr = AsrState(owner, "Succeeded", now);
        var derived = DerivedState(owner, "Succeeded", now);

        Assert.Null(NotificationScanPolicy.ForTranscription(asr, now));
        Assert.Null(NotificationScanPolicy.ForDerived(derived, now.AddMinutes(1)));
        Assert.NotNull(NotificationScanPolicy.ForTranscription(asr, now.AddTicks(-1)));
        Assert.NotNull(NotificationScanPolicy.ForDerived(derived, now.AddTicks(-1)));
    }

    [Fact]
    public void ScanPolicy_AttentionDedupeKeyIsStablePerRevision()
    {
        var now = DateTimeOffset.Parse("2026-11-28T00:00:00Z");
        var owner = Guid.NewGuid();
        var first = AsrState(owner, "Failed", now);
        var second = AsrState(owner, "Failed", now);
        second.Request = first.Request;
        second.Revision = first.Revision;

        var left = NotificationScanPolicy.ForTranscription(first, now.AddMinutes(-1));
        var right = NotificationScanPolicy.ForTranscription(second, now.AddMinutes(-1));

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(left.DedupeKey, right.DedupeKey);
    }

    internal static NoteAsrState AsrState(
        Guid owner,
        string stepStatus,
        DateTimeOffset updatedAt)
    {
        var taskId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        return new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, versionId, null),
            Fingerprint = "fingerprint",
            NoteId = Guid.NewGuid(),
            InputSha256 = "input",
            ManifestSha256 = "manifest",
            CreatedAt = updatedAt.AddMinutes(-1),
            UpdatedAt = updatedAt,
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0,
                    Path = "recordings/0.wav",
                    Sha256 = "sha",
                    Samples = 16000,
                    Attempts = [new NoteAsrAttempt { Status = stepStatus }]
                }
            ]
        };
    }

    internal static NoteDerivedTaskState DerivedState(
        Guid owner,
        string stepStatus,
        DateTimeOffset updatedAt)
    {
        var taskId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        return new NoteDerivedTaskState
        {
            Owner = owner,
            Request = new StartNoteDerivedTaskRequest(
                taskId,
                owner,
                versionId,
                null,
                new NoteDerivedGenerationConsent(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "https://example.invalid",
                    true)),
            Fingerprint = "fingerprint",
            NoteId = Guid.NewGuid(),
            InputSha256 = "input",
            ManifestSha256 = "manifest",
            CreatedAt = updatedAt.AddMinutes(-1),
            UpdatedAt = updatedAt,
            Plan = null!,
            Steps =
            [
                new NoteDerivedStepState
                {
                    Key = "complete-record",
                    Kind = NoteDerivedStepKind.RecordSynthesis,
                    Attempts = [new NoteDerivedAttemptState { Status = stepStatus }]
                }
            ]
        };
    }
}
