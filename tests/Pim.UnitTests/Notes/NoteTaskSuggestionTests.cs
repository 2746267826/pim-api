using System.Net;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.NoteTaskSuggestions;
using Pim.Module.NoteTaskSuggestions.DTOs;
using Pim.Module.NoteTaskSuggestions.Entities;
using Pim.Module.NoteTaskSuggestions.Jobs;
using Pim.Module.NoteTaskSuggestions.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteTaskSuggestionTests
{
    [SkippableFact]
    public async Task StartRejectsMissingChargeConsentBeforeReadingSource()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_consent");
        var request = harness.Request() with { AcknowledgePossibleCharge = false };

        var error = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.StartExtractionAsync(request, default));

        Assert.Equal(40001, error.ErrorCode);
        Assert.Equal(0, harness.Source.ReadCount);
        Assert.Empty(await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>().ToListAsync());
    }

    [SkippableFact]
    public async Task StartPersistsFixedSourceAndReplaysSameRequestWithoutAnotherRead()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_start");
        var request = harness.Request();

        var first = await harness.Service.StartExtractionAsync(request, default);
        var replay = await harness.Service.StartExtractionAsync(
            request with { ExpectedContentSha256 = request.ExpectedContentSha256.ToUpperInvariant() },
            default);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(NoteTaskSuggestionExtractionStatusDto.Queued, replay.Status);
        Assert.Equal(harness.Source.ContentSha256, replay.SourceContentSha256);
        Assert.Equal(harness.Source.InputSha256, replay.SourceInputSha256);
        Assert.Equal(harness.Source.ManifestSha256, replay.SourceManifestSha256);
        Assert.Equal(harness.Profile.Id, replay.ModelProfileId);
        Assert.Equal(harness.Profile.Revision, replay.ModelRevision);
        Assert.Equal(NoteTaskSuggestionPrompt.Version, replay.PromptVersion);
        Assert.Equal(1, harness.Source.ReadCount);
        Assert.Equal(1, await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>().CountAsync());

        var conflict = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.StartExtractionAsync(
                request with { ExpectedContentSha256 = NoteTaskSuggestionService.Hash("another version") },
                default));
        Assert.Equal(40901, conflict.ErrorCode);
        Assert.Equal(1, harness.Source.ReadCount);
    }

    [SkippableFact]
    public async Task StartFailsClosedWithoutCreatingAnExtractionWhenDispatcherIsUnavailable()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_no_dispatcher", dispatchAvailable: false);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.StartExtractionAsync(harness.Request(), default));

        Assert.Equal(50301, error.ErrorCode);
        Assert.Equal(0, harness.Source.ReadCount);
        Assert.Empty(await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>().ToListAsync());
    }

    [SkippableFact]
    public async Task ExistingExtractionCanBeReadAndReplayedWithoutDispatcher()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_readback");
        var request = harness.Request();
        var first = await harness.Service.StartExtractionAsync(request, default);
        var unavailable = harness.NewService(harness.Db, dispatchAvailable: false);

        var replay = await unavailable.StartExtractionAsync(request, default);
        var readback = await unavailable.GetExtractionAsync(first.Id, default);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Id, readback.Id);
        Assert.Equal(NoteTaskSuggestionExtractionStatusDto.Queued, readback.Status);
        Assert.Equal(1, await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task StartRejectsMismatchedIdentityHashAndOversizedSource()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_source");

        var identity = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.StartExtractionAsync(
                harness.Request() with { NoteId = Guid.NewGuid() }, default));
        Assert.Equal(40901, identity.ErrorCode);

        var hash = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.StartExtractionAsync(
                harness.Request() with { ExpectedContentSha256 = NoteTaskSuggestionService.Hash("changed") },
                default));
        Assert.Equal(40901, hash.ErrorCode);

        harness.Source.ContentMarkdown = new string('x', NoteTaskSuggestionPrompt.MaxSourceCharacters + 1);
        var oversized = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.StartExtractionAsync(harness.Request(), default));
        Assert.Equal(41301, oversized.ErrorCode);
        Assert.Contains("不会静默截断", oversized.Message);
        Assert.Empty(await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>().ToListAsync());
    }

    [SkippableFact]
    public async Task ExtractionJobPersistsOrderedSuggestionsAndAuditsControlledWireCall()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_job");
        var extraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        harness.Wire.Reply = """
            {"suggestions":[
              {"title":"先写测试","description":"覆盖真实 PostgreSQL","priority":9,"due":"2026-11-02T09:00:00+08:00"},
              {"title":"再写实现","description":null,"priority":5,"due":null}
            ]}
            """;

        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var completed = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .AsNoTracking().SingleAsync(x => x.Id == extraction.Id);
        var suggestions = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.ExtractionId == extraction.Id)
            .OrderBy(x => x.Ordinal)
            .ToListAsync();
        var log = await harness.AdminDb.AiRequestLogs.AsNoTracking().SingleAsync();

        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Succeeded, completed.Status);
        Assert.Equal(2, completed.SuggestionCount);
        Assert.NotNull(completed.AiCallLogId);
        Assert.Null(completed.ErrorCode);
        Assert.Equal(["先写测试", "再写实现"], suggestions.Select(x => x.Title));
        Assert.Equal([9, 5], suggestions.Select(x => x.Priority));
        Assert.Equal(harness.Source.ContentSha256, suggestions[0].SourceContentSha256);
        Assert.Equal(harness.Owner, log.UserId);
        Assert.Equal("note-derived-version", log.SourceObjectType);
        Assert.Equal(harness.Source.ArtifactVersionId.ToString("D"), log.SourceObjectId);
        Assert.Equal("note.task-suggestion.extract", log.Purpose);
        Assert.Equal(harness.Profile.Revision, log.ModelRevision);
        Assert.Equal(1, harness.Wire.Calls);

        using var requestBody = JsonDocument.Parse(harness.Wire.LastRequestBody!);
        var messages = requestBody.RootElement.GetProperty("messages");
        Assert.Equal(NoteTaskSuggestionPrompt.SystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Contains(harness.Source.ContentMarkdown,
            messages[1].GetProperty("content").GetString());

        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);
        Assert.Equal(1, harness.Wire.Calls);
        Assert.Equal(2, await harness.AdminDb.Set<NoteTaskSuggestionEntity>().CountAsync());
        Assert.Equal(1, await harness.AdminDb.AiRequestLogs.CountAsync());
    }

    [SkippableFact]
    public async Task ExtractionJobPersistsTaskAndEventFieldsWithoutUsingDueForEvents()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_event_job");
        var extraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        harness.Wire.Reply = """
            {"suggestions":[
              {"kind":"Task","title":"回写会议纪要","description":null,"priority":5,"due":"2026-11-20T09:00:00+08:00"},
              {"kind":"Event","title":"机器学习答疑","description":"线上答疑","priority":5,"due":null,
               "start":"2026-11-21T19:00:00+08:00","end":"2026-11-21T20:00:00+08:00",
               "timeZoneId":"Asia/Shanghai"}
            ]}
            """;

        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var suggestions = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.ExtractionId == extraction.Id)
            .OrderBy(x => x.Ordinal)
            .ToListAsync();
        Assert.Equal(["Task", "Event"], suggestions.Select(x => x.Kind));
        Assert.NotNull(suggestions[0].Due);
        Assert.Null(suggestions[0].StartAt);
        Assert.Null(suggestions[0].EndAt);
        Assert.Null(suggestions[1].Due);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), suggestions[1].StartAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T12:00:00Z"), suggestions[1].EndAt);
        Assert.Equal("Asia/Shanghai", suggestions[1].TimeZoneId);
    }

    [SkippableFact]
    public async Task EventExtractionDeduplicatesAcrossCustomAndImplicitEarliestCalendar()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_calendar_dedupe");
        var earliest = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "最早日程日历",
            Kind = "calendar",
            IsDefault = false,
            CreatedAt = DateTimeOffset.Parse("2026-11-01T00:00:00Z")
        };
        var later = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "较晚日程日历",
            Kind = "calendar",
            IsDefault = false,
            CreatedAt = DateTimeOffset.Parse("2026-11-02T00:00:00Z")
        };
        harness.AdminDb.AddRange(earliest, later);
        await harness.AdminDb.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-12-03T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-12-03T10:00:00Z");
        harness.Wire.Reply = """
            {"suggestions":[{"kind":"Event","title":"非默认最早日历日程","description":null,
             "priority":5,"due":null,"start":"2026-12-03T09:00:00Z",
             "end":"2026-12-03T10:00:00Z","timeZoneId":"UTC"}]}
            """;
        var explicitExtraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        await harness.Job.RunAsync(harness.Owner, explicitExtraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var draft = await harness.Service.ListAsync(
            harness.Source.NoteId, NoteTaskSuggestionStatusDto.Draft, default);
        var explicitDraft = Assert.Single(draft.Items);
        explicitDraft = await harness.Service.UpdateAsync(
            explicitDraft.Id,
            new(explicitDraft.Revision, explicitDraft.Title, null, 5, null, null, earliest.Id,
                start, end, "UTC"),
            default);
        await harness.Service.ConfirmAsync(
            explicitDraft.Id, new(Guid.NewGuid(), explicitDraft.Revision), default);

        var implicitExtraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        await harness.Job.RunAsync(harness.Owner, implicitExtraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var suggestions = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.NoteId == harness.Source.NoteId)
            .ToListAsync();
        var storedImplicitExtraction = await harness.AdminDb
            .Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == implicitExtraction.Id);
        var evt = await harness.AdminDb.Set<EventEntity>().AsNoTracking().SingleAsync();

        Assert.Single(suggestions);
        Assert.Equal(NoteTaskSuggestionStatuses.Confirmed, suggestions[0].Status);
        Assert.Equal(earliest.Id, evt.CalendarId);
        Assert.Equal(0, storedImplicitExtraction.SuggestionCount);
    }

    [SkippableFact]
    public async Task ConfirmationReplayAndQueryUseTheSameCalendarResolverAsExtraction()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_resolver_replay");
        var earliest = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "最早日程日历",
            Kind = "calendar",
            IsDefault = false,
            CreatedAt = DateTimeOffset.Parse("2026-11-01T00:00:00Z")
        };
        var later = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "较晚日程日历",
            Kind = "calendar",
            IsDefault = false,
            CreatedAt = DateTimeOffset.Parse("2026-11-02T00:00:00Z")
        };
        harness.AdminDb.AddRange(earliest, later);
        await harness.AdminDb.SaveChangesAsync();
        var start = DateTimeOffset.Parse("2026-12-04T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-12-04T10:00:00Z");
        var first = await harness.CreateSuggestionAsync(
            "非默认日历重放日程", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        harness.AdvanceSourceVersion();
        var second = await harness.CreateSuggestionAsync(
            "非默认日历重放日程", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");

        var confirm = new ConfirmNoteTaskSuggestionRequest(Guid.NewGuid(), first.Revision);
        var confirmed = await harness.Service.ConfirmAsync(first.Id, confirm, default);
        var replay = await harness.Service.ConfirmAsync(first.Id, confirm, default);
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            second.Id, new(Guid.NewGuid(), second.Revision), default));

        Assert.Equal(confirmed.EventId, replay.EventId);
        Assert.Equal(earliest.Id, confirmed.CalendarId);
        Assert.Equal(40901, duplicate.ErrorCode);
        Assert.Contains(confirmed.EventId!.Value.ToString("D"), duplicate.Message);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ExtractionPersistenceWaitsForConfirmationOfTheSameNote()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_note_lock_race");
        var start = DateTimeOffset.Parse("2026-12-05T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-12-05T10:00:00Z");
        var first = await harness.CreateSuggestionAsync(
            "竞态日程", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        var confirm = new ConfirmNoteTaskSuggestionRequest(Guid.NewGuid(), first.Revision);
        harness.Wire.Delay = TimeSpan.FromSeconds(5);
        harness.Wire.Reply = """
            {"suggestions":[{"kind":"Event","title":"竞态日程","description":"覆盖真实 PostgreSQL",
             "priority":9,"due":null,"start":"2026-12-05T09:00:00Z",
             "end":"2026-12-05T10:00:00Z","timeZoneId":"UTC"}]}
            """;
        var replay = await harness.Service.StartExtractionAsync(harness.Request(), default);
        await using var jobDb = harness.NewContext();
        var job = harness.NewJob(jobDb);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var jobRun = job.RunAsync(harness.Owner, replay.Id, timeout.Token);
        await harness.WaitForJobClaimAsync(replay.Id, timeout.Token);
        var confirmed = await harness.Service.ConfirmAsync(first.Id, confirm, timeout.Token);
        await jobRun;

        harness.Db.ChangeTracker.Clear();
        var suggestions = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.NoteId == harness.Source.NoteId)
            .ToListAsync();
        var storedReplay = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .AsNoTracking().SingleAsync(x => x.Id == replay.Id);
        Assert.Equal(NoteTaskSuggestionStatusDto.Confirmed, confirmed.Status);
        Assert.Single(suggestions);
        Assert.Equal(NoteTaskSuggestionStatuses.Confirmed, suggestions[0].Status);
        Assert.Equal(0, storedReplay.SuggestionCount);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ReExtractionAllowsDeletedOriginalsButSkipsAnActiveReplacement()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_deleted_reextract");
        var start = DateTimeOffset.Parse("2026-12-07T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-12-07T10:00:00Z");
        var first = await harness.CreateSuggestionAsync(
            "删除后重加日程", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        var firstConfirmed = await harness.Service.ConfirmAsync(
            first.Id, new(Guid.NewGuid(), first.Revision), default);
        await harness.DeleteEventAsync(firstConfirmed.EventId!.Value);

        var reAdd = await harness.Service.StartExtractionAsync(harness.Request(), default);
        await harness.Job.RunAsync(harness.Owner, reAdd.Id, default);
        harness.Db.ChangeTracker.Clear();
        var replacementDraft = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.ExtractionId == reAdd.Id);
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, replacementDraft.Status);

        var replacement = await harness.Service.ConfirmAsync(
            replacementDraft.Id,
            new(Guid.NewGuid(), replacementDraft.Revision), default);
        Assert.NotEqual(firstConfirmed.EventId, replacement.EventId);

        var activeReplay = await harness.Service.StartExtractionAsync(harness.Request(), default);
        await harness.Job.RunAsync(harness.Owner, activeReplay.Id, default);
        harness.Db.ChangeTracker.Clear();
        var storedActiveReplay = await harness.AdminDb
            .Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == activeReplay.Id);
        Assert.Equal(0, storedActiveReplay.SuggestionCount);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());

        await harness.DeleteEventAsync(replacement.EventId!.Value);
        var deletedReplay = await harness.Service.StartExtractionAsync(harness.Request(), default);
        await harness.Job.RunAsync(harness.Owner, deletedReplay.Id, default);
        harness.Db.ChangeTracker.Clear();
        var againDraft = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.ExtractionId == deletedReplay.Id);
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, againDraft.Status);
        Assert.Equal(0, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task RepeatedExtractionOfSameVersionSkipsDuplicateDrafts()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_same_version_dedupe");
        var first = await harness.CreateSuggestionAsync("同一固定版本只保留一条待确认建议");
        var replay = await harness.Service.StartExtractionAsync(harness.Request(), default);

        await harness.Job.RunAsync(harness.Owner, replay.Id, default);

        harness.Db.ChangeTracker.Clear();
        var drafts = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.Status == NoteTaskSuggestionStatuses.Draft)
            .ToListAsync();
        var storedReplay = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .AsNoTracking().SingleAsync(x => x.Id == replay.Id);

        Assert.Single(drafts);
        Assert.Equal(first.Id, drafts[0].Id);
        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Succeeded, storedReplay.Status);
        Assert.Equal(0, storedReplay.SuggestionCount);
    }

    [SkippableFact]
    public async Task RepeatedExtractionOfSameVersionSkipsAlreadyConfirmedItems()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_same_version_confirmed_dedupe");
        var first = await harness.CreateSuggestionAsync("已确认事项不再生成新草稿");
        await harness.Service.ConfirmAsync(
            first.Id, new(Guid.NewGuid(), first.Revision), default);
        var replay = await harness.Service.StartExtractionAsync(harness.Request(), default);

        await harness.Job.RunAsync(harness.Owner, replay.Id, default);

        harness.Db.ChangeTracker.Clear();
        var storedReplay = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .AsNoTracking().SingleAsync(x => x.Id == replay.Id);
        var suggestions = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .Where(x => x.NoteId == harness.Source.NoteId)
            .ToListAsync();

        Assert.Single(suggestions);
        Assert.Equal(NoteTaskSuggestionStatuses.Confirmed, suggestions[0].Status);
        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Succeeded, storedReplay.Status);
        Assert.Equal(0, storedReplay.SuggestionCount);
        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ExtractionJobFailsClosedOnInvalidModelOutput()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_invalid_output");
        var extraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        harness.Wire.Reply = "not-json";

        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var failed = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .AsNoTracking().SingleAsync(x => x.Id == extraction.Id);
        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Failed, failed.Status);
        Assert.Equal("model_output_invalid", failed.ErrorCode);
        Assert.Equal(0, failed.SuggestionCount);
        Assert.Empty(await harness.AdminDb.Set<NoteTaskSuggestionEntity>().ToListAsync());
        Assert.Equal(1, harness.Wire.Calls);
    }

    [SkippableFact]
    public async Task ConcurrentExtractionClaimsAllowOnlyOneProviderCall()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_job_claim");
        var extraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        harness.Wire.Reply =
            """{"suggestions":[{"title":"只调用一次","description":null,"priority":5,"due":null}]}""";
        harness.Wire.Delay = TimeSpan.FromMilliseconds(200);

        await using var secondDb = harness.NewContext();
        await Task.WhenAll(
            harness.Job.RunAsync(harness.Owner, extraction.Id, default),
            harness.NewJob(secondDb).RunAsync(harness.Owner, extraction.Id, default));

        var stored = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == extraction.Id);
        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Succeeded, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(1, harness.Wire.Calls);
        Assert.Single(await harness.AdminDb.Set<NoteTaskSuggestionEntity>()
            .Where(x => x.ExtractionId == extraction.Id).ToListAsync());
    }

    [SkippableFact]
    public async Task ExtractionJobRecoversOnlyAStaleRunningClaim()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_job_stale");
        var extraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        var stored = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .SingleAsync(x => x.Id == extraction.Id);
        stored.Status = NoteTaskSuggestionExtractionStatuses.Running;
        stored.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        await harness.AdminDb.SaveChangesAsync();
        harness.Wire.Reply =
            """{"suggestions":[{"title":"恢复旧租约","description":null,"priority":5,"due":null}]}""";

        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var completed = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == extraction.Id);
        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Succeeded, completed.Status);
        Assert.Equal(1, completed.AttemptCount);
        Assert.Equal(1, harness.Wire.Calls);
    }

    [SkippableFact]
    public async Task StaleExtractionRechecksAnExistingRunningAiCallWithoutPayingAgain()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_running_receipt");
        var extraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        harness.Wire.Reply = "not-json";
        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);
        harness.Db.ChangeTracker.Clear();

        var log = await harness.AdminDb.AiRequestLogs.SingleAsync();
        var stale = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .SingleAsync(x => x.Id == extraction.Id);
        log.Status = "running";
        log.ErrorCode = null;
        log.ErrorMessage = null;
        log.FinishedAt = null;
        log.ExecutionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        stale.Status = NoteTaskSuggestionExtractionStatuses.Running;
        stale.ErrorCode = null;
        stale.ErrorMessage = null;
        stale.FinishedAt = null;
        stale.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        await harness.AdminDb.SaveChangesAsync();
        var callsBeforeRetry = harness.Wire.Calls;

        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var stored = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .AsNoTracking().SingleAsync(x => x.Id == extraction.Id);
        var storedLog = await harness.AdminDb.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal(extraction.Id, storedLog.ClientRequestId);
        Assert.Equal(log.Id, stored.AiCallLogId);
        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Running, stored.Status);
        Assert.Equal("running", storedLog.Status);
        Assert.Equal(2, stored.AttemptCount);
        Assert.Equal(callsBeforeRetry, harness.Wire.Calls);
        Assert.Equal(1, await harness.AdminDb.AiRequestLogs.CountAsync());
    }

    [SkippableFact]
    public async Task StaleExtractionReplaysExistingTimedOutAiCallWithoutPayingAgain()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_timeout_receipt");
        var extraction = await harness.Service.StartExtractionAsync(harness.Request(), default);
        harness.Wire.Reply = "not-json";
        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);
        harness.Db.ChangeTracker.Clear();

        var log = await harness.AdminDb.AiRequestLogs.SingleAsync();
        var stale = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .SingleAsync(x => x.Id == extraction.Id);
        log.Status = "timed_out";
        log.ErrorCode = "timed_out";
        log.ErrorMessage = "synthetic timed out receipt";
        log.FinishedAt = DateTimeOffset.UtcNow;
        stale.Status = NoteTaskSuggestionExtractionStatuses.Running;
        stale.ErrorCode = null;
        stale.ErrorMessage = null;
        stale.FinishedAt = null;
        stale.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        await harness.AdminDb.SaveChangesAsync();
        var callsBeforeRetry = harness.Wire.Calls;

        await harness.Job.RunAsync(harness.Owner, extraction.Id, default);

        harness.Db.ChangeTracker.Clear();
        var stored = await harness.AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
            .AsNoTracking().SingleAsync(x => x.Id == extraction.Id);
        var storedLog = await harness.AdminDb.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal(extraction.Id, storedLog.ClientRequestId);
        Assert.Equal(log.Id, stored.AiCallLogId);
        Assert.Equal(NoteTaskSuggestionExtractionStatuses.Failed, stored.Status);
        Assert.Equal("ai_timedout", stored.ErrorCode);
        Assert.Equal("timed_out", storedLog.Status);
        Assert.Equal(callsBeforeRetry, harness.Wire.Calls);
        Assert.Equal(1, await harness.AdminDb.AiRequestLogs.CountAsync());
    }

    [SkippableFact]
    public async Task EditAndDismissUseRevisionCasAndDismissNeverCreatesTask()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_lifecycle");
        var suggestion = await harness.CreateSuggestionAsync();

        var updated = await harness.Service.UpdateAsync(suggestion.Id,
            new(suggestion.Revision, "人工修订标题", "人工修订说明", 1, null, null, null), default);
        Assert.NotEqual(suggestion.Revision, updated.Revision);
        Assert.Equal("人工修订标题", updated.Title);

        var stale = await Assert.ThrowsAsync<DomainException>(() => harness.Service.UpdateAsync(suggestion.Id,
            new(suggestion.Revision, "过期修订", null, 5, null, null, null), default));
        Assert.Equal(40901, stale.ErrorCode);
        Assert.Equal("人工修订标题", (await harness.Service.GetAsync(suggestion.Id, default)).Title);

        var dismissed = await harness.Service.DismissAsync(
            suggestion.Id, new(updated.Revision), default);
        Assert.Equal(NoteTaskSuggestionStatusDto.Dismissed, dismissed.Status);
        Assert.Null(dismissed.TaskId);
        Assert.Equal(0, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConfirmCreatesExistingTaskAndReplaysSameRequestIdempotently()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_confirm");
        var suggestion = await harness.CreateSuggestionAsync();
        var confirm = new ConfirmNoteTaskSuggestionRequest(Guid.NewGuid(), suggestion.Revision);

        var first = await harness.Service.ConfirmAsync(suggestion.Id, confirm, default);
        var replay = await harness.Service.ConfirmAsync(suggestion.Id, confirm, default);

        Assert.Equal(NoteTaskSuggestionStatusDto.Confirmed, first.Status);
        Assert.Equal(first.TaskId, replay.TaskId);
        Assert.NotNull(first.TaskId);
        var task = await harness.AdminDb.Set<TaskEntity>().SingleAsync();
        Assert.Equal(first.TaskId, task.Id);
        Assert.Equal("note-task-suggestion", task.Source);
        Assert.Equal("先写测试", task.Title);
        Assert.Equal(9, task.Priority);

        var conflict = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            suggestion.Id,
            confirm with { RequestId = Guid.NewGuid(), ExpectedRevision = first.Revision },
            default));
        Assert.Equal(40901, conflict.ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConfirmEventCreatesOneCalendarEventAndReplaysSameRequest()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_event_confirm");
        var start = DateTimeOffset.Parse("2026-11-21T19:00:00+08:00");
        var end = DateTimeOffset.Parse("2026-11-21T20:00:00+08:00");
        var suggestion = await harness.CreateSuggestionAsync(
            "机器学习答疑", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");
        var confirm = new ConfirmNoteTaskSuggestionRequest(Guid.NewGuid(), suggestion.Revision);

        Assert.Equal(NoteTaskSuggestionKindDto.Event, suggestion.Kind);
        Assert.Null(suggestion.Due);
        Assert.Null(suggestion.TaskId);
        var first = await harness.Service.ConfirmAsync(suggestion.Id, confirm, default);
        var replay = await harness.Service.ConfirmAsync(suggestion.Id, confirm, default);

        Assert.Equal(NoteTaskSuggestionStatusDto.Confirmed, first.Status);
        Assert.Equal(first.EventId, replay.EventId);
        Assert.NotNull(first.EventId);
        Assert.Null(first.TaskId);
        var evt = await harness.AdminDb.Set<EventEntity>().SingleAsync();
        Assert.Equal(first.EventId, evt.Id);
        Assert.Equal("note-suggestion", evt.Source);
        Assert.Equal("机器学习答疑", evt.Title);
        Assert.Equal(start.ToUniversalTime(), evt.DtStart);
        Assert.Equal(end.ToUniversalTime(), evt.DtEnd);
        Assert.Equal(evt.CalendarId, first.CalendarId);
        Assert.Equal(0, await harness.AdminDb.Set<TaskEntity>().CountAsync());
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());

        var conflict = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            suggestion.Id,
            confirm with { RequestId = Guid.NewGuid(), ExpectedRevision = first.Revision },
            default));
        Assert.Equal(40901, conflict.ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task EventDraftRequiresCompleteRangeBeforeItCanBeConfirmed()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_event_range");
        var suggestion = await harness.CreateSuggestionAsync(
            "待补时间日程", NoteTaskSuggestionKindDto.Event);

        var missing = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            suggestion.Id, new(Guid.NewGuid(), suggestion.Revision), default));

        Assert.Equal(40001, missing.ErrorCode);
        harness.AdminDb.ChangeTracker.Clear();
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, (await harness.AdminDb
            .Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == suggestion.Id)).Status);
        Assert.Equal(0, await harness.AdminDb.Set<EventEntity>().CountAsync());
        Assert.Equal(0, await harness.AdminDb.Set<TaskEntity>().CountAsync());

        var start = DateTimeOffset.Parse("2026-11-22T09:00:00+08:00");
        var end = DateTimeOffset.Parse("2026-11-22T10:00:00+08:00");
        var updated = await harness.Service.UpdateAsync(suggestion.Id,
            new(suggestion.Revision, "待补时间日程", null, 5, null, null, null,
                start, end, "Asia/Shanghai"), default);
        var confirmed = await harness.Service.ConfirmAsync(
            suggestion.Id, new(Guid.NewGuid(), updated.Revision), default);

        Assert.Equal(NoteTaskSuggestionStatusDto.Confirmed, confirmed.Status);
        Assert.NotNull(confirmed.EventId);
        Assert.Equal(start.ToUniversalTime(), confirmed.Start);
        Assert.Equal(end.ToUniversalTime(), confirmed.End);
        Assert.Equal("Asia/Shanghai", confirmed.TimeZoneId);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task EventUpdateRejectsInvalidTimeZoneWithoutChangingTheDraft()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_timezone_update");
        var start = DateTimeOffset.Parse("2026-11-22T09:00:00+08:00");
        var end = DateTimeOffset.Parse("2026-11-22T10:00:00+08:00");
        var suggestion = await harness.CreateSuggestionAsync(
            "校验时区日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");

        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.UpdateAsync(
            suggestion.Id,
            new(suggestion.Revision, "被拒绝的修订", null, 5, null, null, null,
                start, end, "Mars/Olympus"),
            default));

        Assert.Equal(40001, error.ErrorCode);
        var unchanged = await harness.Service.GetAsync(suggestion.Id, default);
        Assert.Equal(suggestion.Revision, unchanged.Revision);
        Assert.Equal("校验时区日程", unchanged.Title);
        Assert.Equal("Asia/Shanghai", unchanged.TimeZoneId);

        var updated = await harness.Service.UpdateAsync(
            suggestion.Id,
            new(suggestion.Revision, "有效时区日程", null, 5, null, null, null,
                start, end, "America/New_York"),
            default);
        Assert.Equal("America/New_York", updated.TimeZoneId);
        Assert.Equal(start.ToUniversalTime(), updated.Start);
        Assert.Equal(end.ToUniversalTime(), updated.End);
    }

    [SkippableFact]
    public async Task EventUpdateConvertsWallClockTimesUsingTheDeclaredIanaTimeZone()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_wall_clock_update");
        var suggestion = await harness.CreateSuggestionAsync(
            "无偏移时间日程", NoteTaskSuggestionKindDto.Event);

        var updated = await harness.Service.UpdateAsync(
            suggestion.Id,
            new(suggestion.Revision, "无偏移时间日程", null, 5, null, null, null,
                Start: null, End: null, TimeZoneId: "Asia/Shanghai")
            {
                StartRaw = "2026-11-21T19:00:00",
                EndRaw = "2026-11-21T20:00:00"
            },
            default);

        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), updated.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T12:00:00Z"), updated.End);
        Assert.Equal("Asia/Shanghai", updated.TimeZoneId);
    }

    [SkippableFact]
    public async Task EventUpdateFailsClosedForMissingInvalidAndAmbiguousWallClockZones()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_wall_clock_fail_closed");
        var suggestion = await harness.CreateSuggestionAsync(
            "校验无偏移日程", NoteTaskSuggestionKindDto.Event);

        async Task<DomainException> RejectAsync(
            string startRaw, string endRaw, string? timeZoneId)
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                harness.Service.UpdateAsync(
                    suggestion.Id,
                    new(suggestion.Revision, "校验无偏移日程", null, 5, null, null, null,
                        Start: null, End: null, TimeZoneId: timeZoneId)
                    {
                        StartRaw = startRaw,
                        EndRaw = endRaw
                    },
                    default));
            Assert.Equal(40001, error.ErrorCode);
            return error;
        }

        var missing = await RejectAsync(
            "2026-11-21T19:00:00", "2026-11-21T20:00:00", null);
        Assert.Contains("必须提供有效 timeZoneId", missing.Message);
        var invalid = await RejectAsync(
            "2026-11-21T19:00:00", "2026-11-21T20:00:00", "Mars/Olympus");
        Assert.Contains("有效的 IANA 时区", invalid.Message);
        var gap = await RejectAsync(
            "2026-03-08T02:30:00", "2026-03-08T02:45:00", "America/New_York");
        Assert.Contains("无效的夏令时本地时间", gap.Message);
        var ambiguous = await RejectAsync(
            "2026-11-01T01:30:00", "2026-11-01T01:45:00", "America/New_York");
        Assert.Contains("重复的夏令时本地时间", ambiguous.Message);

        var unchanged = await harness.Service.GetAsync(suggestion.Id, default);
        Assert.Equal(suggestion.Revision, unchanged.Revision);
        Assert.Null(unchanged.Start);
        Assert.Null(unchanged.End);
        Assert.Null(unchanged.TimeZoneId);
    }

    [SkippableFact]
    public async Task EventUpdateAcceptsExplicitOffsetWithAnIndependentlyValidTimeZone()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_offset_timezone");
        var suggestion = await harness.CreateSuggestionAsync(
            "显式偏移日程", NoteTaskSuggestionKindDto.Event);

        var updated = await harness.Service.UpdateAsync(
            suggestion.Id,
            new(suggestion.Revision, "显式偏移日程", null, 5, null, null, null,
                Start: null, End: null, TimeZoneId: "America/New_York")
            {
                StartRaw = "2026-11-21T19:00:00+08:00",
                EndRaw = "2026-11-21T20:00:00+08:00"
            },
            default);

        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), updated.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T12:00:00Z"), updated.End);
        Assert.Equal("America/New_York", updated.TimeZoneId);

        var invalidZone = await Assert.ThrowsAsync<DomainException>(() =>
            harness.Service.UpdateAsync(
                suggestion.Id,
                new(updated.Revision, "显式偏移日程", null, 5, null, null, null,
                    Start: null, End: null, TimeZoneId: "Mars/Olympus")
                {
                    StartRaw = "2026-11-21T19:00:00+08:00",
                    EndRaw = "2026-11-21T20:00:00+08:00"
                },
                default));
        Assert.Equal(40001, invalidZone.ErrorCode);
        Assert.Contains("有效的 IANA 时区", invalidZone.Message);
    }

    [Theory]
    [InlineData(" 2026-11-21t19:00:00z ", true)]
    [InlineData(" 2026-11-21T19:00:00+08:00 ", true)]
    [InlineData("2026-11-21t19:00:00-05:00", true)]
    [InlineData("2026-11-21 19:00:00+08:00", true)]
    [InlineData("2026-11-21T19:00:00", false)]
    [InlineData("2026-11-21", false)]
    public void ExplicitOffsetDetectionTrimsAndAcceptsLowercaseSeparators(
        string value, bool expected)
    {
        Assert.Equal(expected, NoteTaskSuggestionTimeZone.HasExplicitOffset(value));
    }

    [Fact]
    public void IanaValidationRejectsWindowsIdsAndConvertsTrimmedLowercaseOffsets()
    {
        Assert.True(NoteTaskSuggestionTimeZone.IsIana("Asia/Shanghai"));
        Assert.True(NoteTaskSuggestionTimeZone.IsIana("UTC"));
        Assert.True(NoteTaskSuggestionTimeZone.IsIana("Etc/GMT+8"));
        Assert.False(NoteTaskSuggestionTimeZone.IsIana("China Standard Time"));
        Assert.False(NoteTaskSuggestionTimeZone.IsIana("Pacific Standard Time"));

        Assert.True(NoteTaskSuggestionTimeZone.TryConvertToUtc(
            " 2026-11-21t19:00:00z ", null, out var converted, out var error));
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T19:00:00Z"), converted);
        Assert.Equal("", error);
    }

    [SkippableFact]
    public async Task EventWithExplicitOffsetCanOmitTimeZoneAndConfirm()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_offset_without_zone");
        var suggestion = await harness.CreateSuggestionAsync(
            "显式偏移无时区", NoteTaskSuggestionKindDto.Event);

        var updated = await harness.Service.UpdateAsync(
            suggestion.Id,
            new(suggestion.Revision, "显式偏移无时区", null, 5, null, null, null,
                Start: null, End: null, TimeZoneId: null)
            {
                StartRaw = " 2026-11-21t19:00:00z ",
                EndRaw = " 2026-11-21t20:00:00z "
            },
            default);

        Assert.Equal(DateTimeOffset.Parse("2026-11-21T19:00:00Z"), updated.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T20:00:00Z"), updated.End);
        Assert.Null(updated.TimeZoneId);

        var confirmed = await harness.Service.ConfirmAsync(
            suggestion.Id, new(Guid.NewGuid(), updated.Revision), default);

        Assert.NotNull(confirmed.EventId);
        Assert.Null(confirmed.TimeZoneId);
        var stored = await harness.AdminDb.Set<EventEntity>().SingleAsync();
        Assert.Equal(confirmed.EventId, stored.Id);
        Assert.Null(stored.TimeZoneId);
        Assert.Equal(updated.Start, stored.DtStart);
        Assert.Equal(updated.End, stored.DtEnd);
    }

    [SkippableFact]
    public async Task EventUpdateAndConfirmationRejectTaskBookAsTheTargetCalendar()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_task_book");
        var taskBook = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "任务本不是日程日历",
            Kind = "task",
            IsDefault = false
        };
        harness.AdminDb.Add(taskBook);
        await harness.AdminDb.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-11-24T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-24T10:00:00Z");
        var suggestion = await harness.CreateSuggestionAsync(
            "不能写入任务本", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");

        var updateError = await Assert.ThrowsAsync<DomainException>(() =>
            harness.Service.UpdateAsync(
                suggestion.Id,
                new(suggestion.Revision, suggestion.Title, null, 5, null, null, taskBook.Id,
                    start, end, "UTC"),
                default));
        Assert.Equal(40001, updateError.ErrorCode);
        Assert.Contains("不是日程日历", updateError.Message);
        Assert.Null((await harness.Service.GetAsync(suggestion.Id, default)).CalendarId);

        harness.AdminDb.ChangeTracker.Clear();
        var stored = await harness.AdminDb.Set<NoteTaskSuggestionEntity>()
            .SingleAsync(x => x.Id == suggestion.Id);
        stored.CalendarId = taskBook.Id;
        await harness.AdminDb.SaveChangesAsync();
        await using var confirmDb = harness.NewContext();
        var confirmService = harness.NewService(confirmDb);

        var confirmError = await Assert.ThrowsAsync<DomainException>(() =>
            confirmService.ConfirmAsync(
                suggestion.Id, new(Guid.NewGuid(), suggestion.Revision), default));
        Assert.Equal(40001, confirmError.ErrorCode);
        Assert.Contains("不是日程日历", confirmError.Message);
        Assert.Equal(0, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConfirmationRejectsStoredWindowsTimeZoneOrTaskTimeZone()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_confirm_timezone");
        var start = DateTimeOffset.Parse("2026-11-24T11:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-24T12:00:00Z");
        var eventSuggestion = await harness.CreateSuggestionAsync(
            "确认时校验时区", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        var taskSuggestion = await harness.CreateSuggestionAsync(
            "任务不能带时区", NoteTaskSuggestionKindDto.Task);

        harness.AdminDb.ChangeTracker.Clear();
        var storedEvent = await harness.AdminDb.Set<NoteTaskSuggestionEntity>()
            .SingleAsync(x => x.Id == eventSuggestion.Id);
        storedEvent.TimeZoneId = "China Standard Time";
        var storedTask = await harness.AdminDb.Set<NoteTaskSuggestionEntity>()
            .SingleAsync(x => x.Id == taskSuggestion.Id);
        storedTask.TimeZoneId = "Asia/Shanghai";
        await harness.AdminDb.SaveChangesAsync();
        await using var confirmDb = harness.NewContext();
        var confirmService = harness.NewService(confirmDb);

        var eventError = await Assert.ThrowsAsync<DomainException>(() =>
            confirmService.ConfirmAsync(
                eventSuggestion.Id, new(Guid.NewGuid(), eventSuggestion.Revision), default));
        Assert.Equal(40001, eventError.ErrorCode);
        Assert.Contains("有效的 IANA 时区", eventError.Message);

        var taskError = await Assert.ThrowsAsync<DomainException>(() =>
            confirmService.ConfirmAsync(
                taskSuggestion.Id, new(Guid.NewGuid(), taskSuggestion.Revision), default));
        Assert.Equal(40001, taskError.ErrorCode);
        Assert.Contains("任务建议不能携带时区", taskError.Message);

        Assert.Equal(0, await harness.AdminDb.Set<EventEntity>().CountAsync());
        Assert.Equal(0, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task EventConfirmationRejectsOutlookBoundCalendarWithoutCreatingAnything()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_outlook");
        var calendar = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "Outlook 日历",
            Kind = "calendar",
            IsDefault = false
        };
        var connection = new OutlookConnectionEntity { UserId = harness.Owner };
        harness.AdminDb.AddRange(calendar, connection);
        await harness.AdminDb.SaveChangesAsync();
        harness.AdminDb.Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = connection.Id,
            PimCalendarId = calendar.Id,
            GraphCalendarId = "synthetic-outlook-calendar",
            Name = "Outlook 日历",
            CanEdit = true
        });
        await harness.AdminDb.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-11-23T09:00:00+08:00");
        var end = DateTimeOffset.Parse("2026-11-23T10:00:00+08:00");
        var suggestion = await harness.CreateSuggestionAsync(
            "Outlook 禁止直写", NoteTaskSuggestionKindDto.Event, null, start, end);
        var updated = await harness.Service.UpdateAsync(suggestion.Id,
            new(suggestion.Revision, suggestion.Title, null, 5, null, null, calendar.Id,
                start, end, null), default);

        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            suggestion.Id, new(Guid.NewGuid(), updated.Revision), default));

        Assert.Equal(02009, error.ErrorCode);
        harness.AdminDb.ChangeTracker.Clear();
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, (await harness.AdminDb
            .Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == suggestion.Id)).Status);
        Assert.Equal(0, await harness.AdminDb.Set<EventEntity>().CountAsync());
        Assert.Equal(0, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task CrossBatchTaskWithStableFingerprintConflictsWithoutCreatingAnotherTask()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_cross_batch_task");
        var due = DateTimeOffset.Parse("2026-11-20T09:00:00+08:00");
        var first = await harness.CreateSuggestionAsync(
            "跨批次任务", NoteTaskSuggestionKindDto.Task, due);
        harness.AdvanceSourceVersion();
        var second = await harness.CreateSuggestionAsync(
            "跨批次任务", NoteTaskSuggestionKindDto.Task, due);

        await harness.Service.ConfirmAsync(
            first.Id, new(Guid.NewGuid(), first.Revision), default);
        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            second.Id, new(Guid.NewGuid(), second.Revision), default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Contains("未重复创建", error.Message);
        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
        harness.AdminDb.ChangeTracker.Clear();
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, (await harness.AdminDb
            .Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == second.Id)).Status);
    }

    [SkippableFact]
    public async Task ConcurrentCrossBatchSameItemCreatesOnlyOneTask()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_cross_batch_task_race");
        var due = DateTimeOffset.Parse("2026-11-20T09:00:00+08:00");
        var first = await harness.CreateSuggestionAsync(
            "并发跨批次任务", NoteTaskSuggestionKindDto.Task, due);
        harness.AdvanceSourceVersion();
        var second = await harness.CreateSuggestionAsync(
            "并发跨批次任务", NoteTaskSuggestionKindDto.Task, due);
        await using var secondDb = harness.NewContext();
        var secondService = harness.NewService(secondDb);

        async Task<Exception?> ConfirmAsync(Func<Task<NoteTaskSuggestionDto>> call)
        {
            try
            {
                await call();
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var outcomes = await Task.WhenAll(
            ConfirmAsync(() => harness.Service.ConfirmAsync(
                first.Id, new(Guid.NewGuid(), first.Revision), default)),
            ConfirmAsync(() => secondService.ConfirmAsync(
                second.Id, new(Guid.NewGuid(), second.Revision), default)));

        Assert.Single(outcomes, x => x is null);
        var conflict = Assert.Single(outcomes, x => x is not null);
        Assert.IsType<DomainException>(conflict);
        Assert.Equal(40901, ((DomainException)conflict!).ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task CrossBatchEventWithStableFingerprintConflictsWithoutCreatingAnotherEvent()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_cross_batch_event");
        var start = DateTimeOffset.Parse("2026-11-20T09:00:00+08:00");
        var end = DateTimeOffset.Parse("2026-11-20T10:00:00+08:00");
        var first = await harness.CreateSuggestionAsync(
            "跨批次日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");
        harness.AdvanceSourceVersion();
        var second = await harness.CreateSuggestionAsync(
            "跨批次日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");

        await harness.Service.ConfirmAsync(
            first.Id, new(Guid.NewGuid(), first.Revision), default);
        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            second.Id, new(Guid.NewGuid(), second.Revision), default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Contains("未重复创建", error.Message);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
        Assert.Equal(0, await harness.AdminDb.Set<TaskEntity>().CountAsync());
        harness.AdminDb.ChangeTracker.Clear();
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, (await harness.AdminDb
            .Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == second.Id)).Status);
    }

    [SkippableFact]
    public async Task ConcurrentCrossBatchSameEventCreatesOnlyOneEvent()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_cross_batch_event_race");
        var start = DateTimeOffset.Parse("2026-11-20T11:00:00+08:00");
        var end = DateTimeOffset.Parse("2026-11-20T12:00:00+08:00");
        var first = await harness.CreateSuggestionAsync(
            "并发跨批次日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");
        harness.AdvanceSourceVersion();
        var second = await harness.CreateSuggestionAsync(
            "并发跨批次日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");
        await using var secondDb = harness.NewContext();
        var secondService = harness.NewService(secondDb);

        async Task<Exception?> ConfirmAsync(Func<Task<NoteTaskSuggestionDto>> call)
        {
            try
            {
                await call();
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var outcomes = await Task.WhenAll(
            ConfirmAsync(() => harness.Service.ConfirmAsync(
                first.Id, new(Guid.NewGuid(), first.Revision), default)),
            ConfirmAsync(() => secondService.ConfirmAsync(
                second.Id, new(Guid.NewGuid(), second.Revision), default)));

        Assert.Single(outcomes, x => x is null);
        var conflict = Assert.Single(outcomes, x => x is not null);
        Assert.IsType<DomainException>(conflict);
        Assert.Equal(40901, ((DomainException)conflict!).ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task DefaultCalendarIsCanonicalAcrossCrossBatchEventFingerprints()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_default_calendar_fingerprint");
        var defaultCalendar = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "默认日历",
            Kind = "calendar",
            IsDefault = true
        };
        harness.AdminDb.Add(defaultCalendar);
        await harness.AdminDb.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-11-25T09:00:00+08:00");
        var end = DateTimeOffset.Parse("2026-11-25T10:00:00+08:00");
        var first = await harness.CreateSuggestionAsync(
            "默认日历日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");
        harness.AdvanceSourceVersion();
        var second = await harness.CreateSuggestionAsync(
            "默认日历日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");
        second = await harness.Service.UpdateAsync(
            second.Id,
            new(second.Revision, second.Title, second.Description, second.Priority,
                null, null, defaultCalendar.Id, start, end, "Asia/Shanghai"),
            default);

        var confirmed = await harness.Service.ConfirmAsync(
            first.Id, new(Guid.NewGuid(), first.Revision), default);
        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            second.Id, new(Guid.NewGuid(), second.Revision), default));

        Assert.Equal(defaultCalendar.Id, confirmed.CalendarId);
        Assert.Equal(40901, error.ErrorCode);
        Assert.Contains("未重复创建", error.Message);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task EventIdentityIgnoresDescriptionPriorityAndTimeZoneLabel()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_core_identity");
        var start = DateTimeOffset.Parse("2026-11-26T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-26T10:00:00Z");
        var first = await harness.CreateSuggestionAsync(
            "核心身份日程", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        harness.AdvanceSourceVersion();
        var second = await harness.CreateSuggestionAsync(
            "核心身份日程", NoteTaskSuggestionKindDto.Event, null, start, end, "Asia/Shanghai");
        second = await harness.Service.UpdateAsync(
            second.Id,
            new(second.Revision, second.Title, "不同说明不应绕过幂等", 1, null, null, null,
                start, end, "Asia/Shanghai"),
            default);

        await harness.Service.ConfirmAsync(
            first.Id, new(Guid.NewGuid(), first.Revision), default);
        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            second.Id, new(Guid.NewGuid(), second.Revision), default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task SameEventTimesInDifferentCalendarsRemainDistinct()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_event_calendar_identity");
        var defaultCalendar = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "默认日历",
            Kind = "calendar",
            IsDefault = true
        };
        var calendar = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "工作日历",
            Kind = "calendar",
            IsDefault = false
        };
        harness.AdminDb.AddRange(defaultCalendar, calendar);
        await harness.AdminDb.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-11-27T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-27T10:00:00Z");
        var defaultEvent = await harness.CreateSuggestionAsync(
            "不同日历日程", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        harness.AdvanceSourceVersion();
        var explicitEvent = await harness.CreateSuggestionAsync(
            "不同日历日程", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        explicitEvent = await harness.Service.UpdateAsync(
            explicitEvent.Id,
            new(explicitEvent.Revision, explicitEvent.Title, null, 5, null, null, calendar.Id,
                start, end, "UTC"),
            default);

        await harness.Service.ConfirmAsync(
            defaultEvent.Id, new(Guid.NewGuid(), defaultEvent.Revision), default);
        var confirmed = await harness.Service.ConfirmAsync(
            explicitEvent.Id, new(Guid.NewGuid(), explicitEvent.Revision), default);

        Assert.Equal(calendar.Id, confirmed.CalendarId);
        Assert.Equal(2, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConcurrentDefaultAndExplicitDefaultCalendarStillCreateOneEvent()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_explicit_default_event_race");
        var defaultCalendar = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "默认日历",
            Kind = "calendar",
            IsDefault = true
        };
        harness.AdminDb.Add(defaultCalendar);
        await harness.AdminDb.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-11-28T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-28T10:00:00Z");
        var implicitDefault = await harness.CreateSuggestionAsync(
            "显式默认并发生成", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        harness.AdvanceSourceVersion();
        var explicitDefault = await harness.CreateSuggestionAsync(
            "显式默认并发生成", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        explicitDefault = await harness.Service.UpdateAsync(
            explicitDefault.Id,
            new(explicitDefault.Revision, explicitDefault.Title, null, 5, null, null,
                defaultCalendar.Id, start, end, "UTC"),
            default);
        await using var secondDb = harness.NewContext();
        var secondService = harness.NewService(secondDb);

        async Task<Exception?> ConfirmAsync(Func<Task<NoteTaskSuggestionDto>> call)
        {
            try
            {
                await call();
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var outcomes = await Task.WhenAll(
            ConfirmAsync(() => harness.Service.ConfirmAsync(
                implicitDefault.Id, new(Guid.NewGuid(), implicitDefault.Revision), default)),
            ConfirmAsync(() => secondService.ConfirmAsync(
                explicitDefault.Id, new(Guid.NewGuid(), explicitDefault.Revision), default)));

        Assert.Single(outcomes, x => x is null);
        var conflict = Assert.Single(outcomes, x => x is not null);
        Assert.IsType<DomainException>(conflict);
        Assert.Equal(40901, ((DomainException)conflict!).ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConcurrentEventConfirmationsForDifferentNotesCreateOneDefaultCalendar()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_default_calendar_race");
        var start = DateTimeOffset.Parse("2026-11-30T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-30T10:00:00Z");
        var first = await harness.CreateSuggestionAsync(
            "默认日历并发生成 A", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");

        harness.Source.NoteId = Guid.NewGuid();
        var second = await harness.CreateSuggestionAsync(
            "默认日历并发生成 B", NoteTaskSuggestionKindDto.Event, null,
            start.AddHours(1), end.AddHours(1), "UTC");
        await using var secondDb = harness.NewContext();
        var secondService = harness.NewService(secondDb);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var confirmed = await Task.WhenAll(
                harness.Service.ConfirmAsync(
                    first.Id, new(Guid.NewGuid(), first.Revision), timeout.Token),
                secondService.ConfirmAsync(
                    second.Id, new(Guid.NewGuid(), second.Revision), timeout.Token))
            .WaitAsync(TimeSpan.FromSeconds(35));

        Assert.Equal(2, confirmed.Length);
        Assert.Equal(confirmed[0].CalendarId, confirmed[1].CalendarId);
        Assert.NotNull(confirmed[0].CalendarId);
        var calendars = await harness.AdminDb.Set<CalendarEntity>().AsNoTracking()
            .Where(x => x.UserId == harness.Owner && x.Kind == "calendar")
            .ToListAsync();
        var calendar = Assert.Single(calendars);
        Assert.True(calendar.IsDefault);
        Assert.Equal(2, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConcurrentDefaultCalendarCreationIsIsolatedPerUser()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_default_calendar_users");
        var secondUser = Guid.NewGuid();
        harness.AdminDb.Users.Add(new UserEntity
        {
            Id = secondUser,
            Username = $"task-suggestion-{secondUser:N}",
            Email = $"{secondUser:N}@synthetic.invalid",
            PasswordHash = "synthetic",
            IsActive = true
        });
        await harness.AdminDb.SaveChangesAsync();

        await using var ownerDb = harness.NewContext();
        await using var secondDb = harness.NewContextFor(secondUser);
        var ownerCalendar = harness.NewCalendarService(ownerDb, harness.Owner);
        var secondCalendar = harness.NewCalendarService(secondDb, secondUser);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var ids = await Task.WhenAll(
                ownerCalendar.GetOrCreateDefaultEventCalendarIdAsync(timeout.Token),
                secondCalendar.GetOrCreateDefaultEventCalendarIdAsync(timeout.Token))
            .WaitAsync(TimeSpan.FromSeconds(35));

        Assert.NotEqual(ids[0], ids[1]);
        var calendars = await harness.AdminDb.Set<CalendarEntity>().AsNoTracking()
            .Where(x => x.Kind == "calendar" && (x.UserId == harness.Owner || x.UserId == secondUser))
            .ToListAsync();
        Assert.Equal(2, calendars.Count);
        Assert.Single(calendars, x => x.UserId == harness.Owner && x.IsDefault);
        Assert.Single(calendars, x => x.UserId == secondUser && x.IsDefault);
    }

    [SkippableFact]
    public async Task SameTitleIsStillDistinctAcrossKindsAndEventTimes()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_distinct_items");
        var firstStart = DateTimeOffset.Parse("2026-11-24T09:00:00+08:00");
        var firstEnd = DateTimeOffset.Parse("2026-11-24T10:00:00+08:00");
        var secondStart = DateTimeOffset.Parse("2026-11-24T11:00:00+08:00");
        var secondEnd = DateTimeOffset.Parse("2026-11-24T12:00:00+08:00");
        var due = DateTimeOffset.Parse("2026-11-24T09:00:00+08:00");
        var task = await harness.CreateSuggestionAsync(
            "同名事项", NoteTaskSuggestionKindDto.Task, due);
        var firstEvent = await harness.CreateSuggestionAsync(
            "同名事项", NoteTaskSuggestionKindDto.Event, null, firstStart, firstEnd);
        var secondEvent = await harness.CreateSuggestionAsync(
            "同名事项", NoteTaskSuggestionKindDto.Event, null, secondStart, secondEnd);

        await harness.Service.ConfirmAsync(task.Id, new(Guid.NewGuid(), task.Revision), default);
        await harness.Service.ConfirmAsync(
            firstEvent.Id, new(Guid.NewGuid(), firstEvent.Revision), default);
        await harness.Service.ConfirmAsync(
            secondEvent.Id, new(Guid.NewGuid(), secondEvent.Revision), default);

        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
        Assert.Equal(2, await harness.AdminDb.Set<EventEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConcurrentSameConfirmCreatesOneTask()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_confirm_race");
        var suggestion = await harness.CreateSuggestionAsync();
        var request = new ConfirmNoteTaskSuggestionRequest(Guid.NewGuid(), suggestion.Revision);

        await using var secondDb = harness.NewContext();
        var secondService = harness.NewService(secondDb);
        var results = await Task.WhenAll(
            harness.Service.ConfirmAsync(suggestion.Id, request, default),
            secondService.ConfirmAsync(suggestion.Id, request, default));

        Assert.Equal(results[0].TaskId, results[1].TaskId);
        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
        var stored = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == suggestion.Id);
        Assert.Equal(NoteTaskSuggestionStatuses.Confirmed, stored.Status);
        Assert.Equal(results[0].TaskId, stored.TaskId);
    }

    [SkippableFact]
    public async Task SameConfirmRequestCannotLinkTwoSuggestions()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_confirm_link");
        var first = await harness.CreateSuggestionAsync("第一条");
        var second = await harness.CreateSuggestionAsync("第二条");
        var requestId = Guid.NewGuid();

        await harness.Service.ConfirmAsync(first.Id,
            new(requestId, first.Revision), default);
        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            second.Id, new(requestId, second.Revision), default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
        harness.AdminDb.ChangeTracker.Clear();
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, (await harness.AdminDb
            .Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == second.Id)).Status);
    }

    [SkippableFact]
    public async Task ConcurrentSameConfirmRequestAcrossSuggestionsCreatesOneTask()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_confirm_link_race");
        var first = await harness.CreateSuggestionAsync("并发第一条");
        var second = await harness.CreateSuggestionAsync("并发第二条");
        var requestId = Guid.NewGuid();
        await using var secondDb = harness.NewContext();
        var secondService = harness.NewService(secondDb);

        async Task<Exception?> ConfirmAsync(Func<Task<NoteTaskSuggestionDto>> call)
        {
            try
            {
                await call();
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var outcomes = await Task.WhenAll(
            ConfirmAsync(() => harness.Service.ConfirmAsync(
                first.Id, new(requestId, first.Revision), default)),
            ConfirmAsync(() => secondService.ConfirmAsync(
                second.Id, new(requestId, second.Revision), default)));

        Assert.Single(outcomes, x => x is null);
        var conflict = Assert.Single(outcomes, x => x is not null);
        Assert.IsType<DomainException>(conflict);
        Assert.Equal(40901, ((DomainException)conflict!).ErrorCode);
        Assert.Equal(1, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConfirmFailureRollsBackTaskAndSuggestionState()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_confirm_rollback");
        var suggestion = await harness.CreateSuggestionAsync();
        var stored = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().SingleAsync(x => x.Id == suggestion.Id);
        stored.TaskBookId = Guid.NewGuid();
        await harness.AdminDb.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => harness.Service.ConfirmAsync(
            suggestion.Id, new(Guid.NewGuid(), suggestion.Revision), default));

        Assert.Equal(02003, error.ErrorCode);
        harness.AdminDb.ChangeTracker.Clear();
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, (await harness.AdminDb
            .Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == suggestion.Id)).Status);
        Assert.Equal(0, await harness.AdminDb.Set<TaskEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task ConfirmationFailureAfterEventInsertRollsBackTheInsertedObject()
    {
        await using var harness = await Harness.CreateAsync(
            "test_note_task_suggestion_confirm_event_rollback");
        var calendar = new CalendarEntity
        {
            UserId = harness.Owner,
            Name = "回滚日程日历",
            Kind = "calendar",
            IsDefault = false
        };
        harness.AdminDb.Add(calendar);
        await harness.AdminDb.SaveChangesAsync();
        var start = DateTimeOffset.Parse("2026-12-06T09:00:00Z");
        var end = DateTimeOffset.Parse("2026-12-06T10:00:00Z");
        var suggestion = await harness.CreateSuggestionAsync(
            "入库后失败回滚", NoteTaskSuggestionKindDto.Event, null, start, end, "UTC");
        suggestion = await harness.Service.UpdateAsync(
            suggestion.Id,
            new(suggestion.Revision, suggestion.Title, null, 5, null, null, calendar.Id,
                start, end, "UTC"),
            default);
        await harness.AdminDb.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_note_suggestion_confirmation() RETURNS trigger AS $$
            BEGIN
              IF NEW.status = 'Confirmed' AND OLD.status = 'Draft' THEN
                RAISE EXCEPTION 'synthetic confirmation failure after object insert';
              END IF;
              RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """);
        await harness.AdminDb.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_note_suggestion_confirmation
            BEFORE UPDATE ON note_task_suggestions
            FOR EACH ROW EXECUTE FUNCTION reject_note_suggestion_confirmation();
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() => harness.Service.ConfirmAsync(
            suggestion.Id, new(Guid.NewGuid(), suggestion.Revision), default));

        harness.AdminDb.ChangeTracker.Clear();
        var stored = await harness.AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
            .SingleAsync(x => x.Id == suggestion.Id);
        Assert.Equal(NoteTaskSuggestionStatuses.Draft, stored.Status);
        Assert.Null(stored.ConfirmRequestId);
        Assert.Null(stored.EventId);
        Assert.Equal(0, await harness.AdminDb.Set<EventEntity>().CountAsync());
        Assert.Equal(1, await harness.AdminDb.Set<CalendarEntity>().CountAsync());
    }

    [SkippableFact]
    public async Task OtherOwnerCannotReadOrMutatePersistedSuggestion()
    {
        await using var harness = await Harness.CreateAsync("test_note_task_suggestion_owner");
        var otherOwner = Guid.NewGuid();
        var extraction = new NoteTaskSuggestionExtractionEntity
        {
            UserId = otherOwner,
            RequestId = Guid.NewGuid(),
            RequestFingerprint = NoteTaskSuggestionService.Hash("other-owner"),
            NoteId = Guid.NewGuid(),
            ArtifactId = Guid.NewGuid(),
            ArtifactVersionId = Guid.NewGuid(),
            SourceContentSha256 = NoteTaskSuggestionService.Hash("content"),
            SourceInputSha256 = NoteTaskSuggestionService.Hash("input"),
            SourceManifestSha256 = NoteTaskSuggestionService.Hash("manifest"),
            SourceContentMarkdown = "other owner",
            ModelProfileId = harness.Profile.Id,
            ModelRevision = harness.Profile.Revision,
            ModelEndpoint = harness.Profile.BaseUrl,
            PromptVersion = NoteTaskSuggestionPrompt.Version
        };
        var suggestion = new NoteTaskSuggestionEntity
        {
            UserId = otherOwner,
            ExtractionId = extraction.Id,
            Ordinal = 0,
            NoteId = extraction.NoteId,
            ArtifactId = extraction.ArtifactId,
            ArtifactVersionId = extraction.ArtifactVersionId,
            SourceContentSha256 = extraction.SourceContentSha256,
            SourceInputSha256 = extraction.SourceInputSha256,
            SourceManifestSha256 = extraction.SourceManifestSha256,
            Title = "不可见建议",
            Priority = 5
        };
        harness.AdminDb.AddRange(extraction, suggestion);
        await harness.AdminDb.SaveChangesAsync();

        Assert.Empty((await harness.Service.ListAsync(null, null, default)).Items);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.GetExtractionAsync(extraction.Id, default))).ErrorCode);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.GetAsync(suggestion.Id, default))).ErrorCode);
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateAsync(suggestion.Id,
                new(suggestion.Revision, "越权修改", null, 5, null, null, null), default))).ErrorCode);
    }

    [Theory]
    [InlineData("""{"suggestions":[]}""", 0)]
    [InlineData("""```json\n{"suggestions":[{"title":"A","description":null,"priority":5,"due":null}]}\n```""", 1)]
    public void PromptParserAcceptsOnlyTheDeclaredShape(string json, int count)
    {
        Assert.Equal(count, NoteTaskSuggestionPrompt.Parse(json.Replace("\\n", "\n")).Count);
    }

    [Theory]
    [InlineData("""{"suggestions":[{"title":"A","priority":2}]}""")]
    [InlineData("""{"suggestions":[{"description":"missing title","priority":5}]}""")]
    [InlineData("""{"suggestions":[{"title":"A","priority":5,"due":"not-a-date"}]}""")]
    [InlineData("""{"items":[]}""")]
    public void PromptParserRejectsInvalidModelOutput(string json)
    {
        Assert.Throws<InvalidDataException>(() => NoteTaskSuggestionPrompt.Parse(json));
    }

    [Fact]
    public void PromptParserKeepsTaskAndEventKindsSeparate()
    {
        var drafts = NoteTaskSuggestionPrompt.Parse("""
            {"suggestions":[
              {"kind":"Task","title":"写测试","description":null,"priority":9,"due":"2026-11-20T09:00:00+08:00"},
              {"kind":"Event","title":"答疑","description":null,"priority":5,"due":null,
               "start":"2026-11-21T19:00:00+08:00","end":"2026-11-21T20:00:00+08:00",
               "timeZoneId":"Asia/Shanghai"}
            ]}
            """);

        Assert.Equal(NoteTaskSuggestionKindDto.Task, drafts[0].Kind);
        Assert.NotNull(drafts[0].Due);
        Assert.Null(drafts[0].Start);
        Assert.Null(drafts[0].End);
        Assert.Equal(NoteTaskSuggestionKindDto.Event, drafts[1].Kind);
        Assert.Null(drafts[1].Due);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), drafts[1].Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T12:00:00Z"), drafts[1].End);
        Assert.Equal("Asia/Shanghai", drafts[1].TimeZoneId);
    }

    [Fact]
    public void PromptParserConvertsWallClockEventTimesUsingTheDeclaredTimeZone()
    {
        var draft = Assert.Single(NoteTaskSuggestionPrompt.Parse("""
            {"suggestions":[{"kind":"Event","title":"无偏移时间","description":null,"priority":5,
             "due":null,"start":"2026-11-21T19:00:00","end":"2026-11-21T20:00:00",
             "timeZoneId":"Asia/Shanghai"}]}
            """));

        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), draft.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T12:00:00Z"), draft.End);
    }

    [Fact]
    public void PromptParserTreatsLegacyTaskOutputWithoutKindAsTask()
    {
        var draft = Assert.Single(NoteTaskSuggestionPrompt.Parse("""
            {"suggestions":[{"title":"旧格式","description":null,"priority":5,"due":null}]}
            """));

        Assert.Equal(NoteTaskSuggestionKindDto.Task, draft.Kind);
        Assert.Null(draft.Start);
        Assert.Null(draft.End);
    }

    [Theory]
    [InlineData("""
        {"suggestions":[{"kind":"Event","title":"倒序","priority":5,"due":null,
         "start":"2026-11-21T20:00:00+08:00","end":"2026-11-21T19:00:00+08:00"}]}
        """)]
    [InlineData("""
        {"suggestions":[{"kind":"Event","title":"只写截止","priority":5,
         "due":"2026-11-21T19:00:00+08:00","start":null,"end":null}]}
        """)]
    [InlineData("""
        {"suggestions":[{"kind":"Event","title":"半段时间","priority":5,"due":null,
         "start":"2026-11-21T19:00:00+08:00","end":null}]}
        """)]
    public void PromptParserRejectsInvalidEventTiming(string json)
    {
        Assert.Throws<InvalidDataException>(() => NoteTaskSuggestionPrompt.Parse(json));
    }

    [Theory]
    [InlineData("""
        {"suggestions":[{"kind":"Event","title":"缺少时区","priority":5,
         "start":"2026-11-21T19:00:00","end":"2026-11-21T20:00:00"}]}
        """)]
    [InlineData("""
        {"suggestions":[{"kind":"Event","title":"错误时区","priority":5,
         "start":"2026-11-21T19:00:00","end":"2026-11-21T20:00:00",
         "timeZoneId":"Mars/Olympus"}]}
        """)]
    [InlineData("""
        {"suggestions":[{"kind":"Event","title":"Windows 时区","priority":5,
         "start":"2026-11-21T19:00:00","end":"2026-11-21T20:00:00",
         "timeZoneId":"China Standard Time"}]}
        """)]
    public void PromptParserRejectsWallClockTimesWithoutAValidTimeZone(string json)
    {
        Assert.Throws<InvalidDataException>(() => NoteTaskSuggestionPrompt.Parse(json));
    }

    [Fact]
    public void PromptParserAcceptsExplicitOffsetEventWithoutTimeZone()
    {
        var draft = Assert.Single(NoteTaskSuggestionPrompt.Parse("""
            {"suggestions":[{"kind":"Event","title":"显式偏移无时区","priority":5,
             "start":"2026-11-21T19:00:00+08:00","end":"2026-11-21T20:00:00+08:00",
             "timeZoneId":null}]}
            """));

        Assert.Null(draft.TimeZoneId);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), draft.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T12:00:00Z"), draft.End);
    }

    [Fact]
    public void PromptParserRejectsTimeZoneOnTask()
    {
        Assert.Throws<InvalidDataException>(() => NoteTaskSuggestionPrompt.Parse("""
            {"suggestions":[{"kind":"Task","title":"任务不应带时区","priority":5,
             "due":"2026-11-21T19:00:00+08:00","timeZoneId":"Asia/Shanghai"}]}
            """));
    }

    [Theory]
    [InlineData("2026-03-08T02:30:00", "America/New_York")]
    [InlineData("2026-11-01T01:30:00", "America/New_York")]
    public void PromptParserFailsClosedOnDstGapsAndAmbiguousWallClockTimes(
        string localStart, string timeZoneId)
    {
        var json = $$"""
            {"suggestions":[{"kind":"Event","title":"夏令时边界","priority":5,
             "start":"{{localStart}}","end":"{{localStart.Replace(":30:00", ":45:00")}}",
             "timeZoneId":"{{timeZoneId}}"}]}
            """;

        Assert.Throws<InvalidDataException>(() => NoteTaskSuggestionPrompt.Parse(json));
    }

    [Theory]
    [InlineData("""{"expectedRevision":"11111111-1111-4111-8111-111111111111","title":"无偏移","priority":5,"start":"2026-11-21T19:00:00","end":"2026-11-21T20:00:00","timeZoneId":"Asia/Shanghai"}""", "2026-11-21T19:00:00", "2026-11-21T20:00:00", null)]
    [InlineData("""{"expectedRevision":"11111111-1111-4111-8111-111111111111","title":"显式偏移","priority":5,"start":"2026-11-21T19:00:00+08:00","end":"2026-11-21T20:00:00+08:00","timeZoneId":"America/New_York"}""", "2026-11-21T19:00:00+08:00", "2026-11-21T20:00:00+08:00", "2026-11-21T11:00:00Z")]
    public void UpdateRequestJsonPreservesWallClockAndExplicitOffsetSemantics(
        string json, string expectedStartRaw, string expectedEndRaw, string? expectedStart)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<UpdateNoteTaskSuggestionRequest>(json, options)!;

        Assert.Equal(expectedStartRaw, request.StartRaw);
        Assert.Equal(expectedEndRaw, request.EndRaw);
        Assert.Equal(
            expectedStart is null ? null : DateTimeOffset.Parse(expectedStart),
            request.Start);
        Assert.Equal(
            expectedStart is null ? null : DateTimeOffset.Parse(expectedStart).AddHours(1),
            request.End);
    }

    [Fact]
    public void UpdateRequestJsonSerializesTheContractedDateTimeShape()
    {
        var request = new UpdateNoteTaskSuggestionRequest(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            "往返", null, 5, null, null, null,
            DateTimeOffset.Parse("2026-11-21T11:00:00Z"),
            DateTimeOffset.Parse("2026-11-21T12:00:00Z"),
            "Asia/Shanghai");

        var json = JsonSerializer.Serialize(
            request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("2026-11-21T11:00:00+00:00", root.GetProperty("start").GetString());
        Assert.Equal("2026-11-21T12:00:00+00:00", root.GetProperty("end").GetString());
        Assert.False(root.TryGetProperty("startRaw", out _));
        Assert.False(root.TryGetProperty("endRaw", out _));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempMigrationDatabase _database;
        private readonly ISecretProtector _secrets;
        private readonly DbContextOptions<PimDbContext> _options;
        private readonly FakeUser _currentUser;

        private Harness(
            TempMigrationDatabase database,
            Guid owner,
            DbContextOptions<PimDbContext> options,
            FakeUser currentUser,
            ISecretProtector secrets,
            PimDbContext db,
            FakeSourceReader source,
            Wire wire,
            AiModelProfileEntity profile,
            IBackgroundJobClient? jobs)
        {
            _database = database;
            Owner = owner;
            _options = options;
            _currentUser = currentUser;
            _secrets = secrets;
            Db = db;
            Source = source;
            Wire = wire;
            Profile = profile;
            var calendar = CreateCalendarService(db);
            var materials = new NoteMaterialUploadStore(Options.Create(new NoteMaterialUploadOptions
            {
                StorageRoot = Path.Combine(Path.GetTempPath(), "pim-task-suggestion-materials",
                    Guid.NewGuid().ToString("N"))
            }));
            var calls = new AiTextCallService(db, secrets, new AiProfileChatClientFactory(wire),
                currentUser, new AiTextCallGate());
            Service = new NoteTaskSuggestionService(db, currentUser, source, materials, calendar,
                TimeProvider.System, NullLogger<NoteTaskSuggestionService>.Instance, jobs);
            Job = new NoteTaskSuggestionExtractionJob(db, calendar,
                new AiTextNoteTaskSuggestionModelClient(calls),
                TimeProvider.System,
                NullLogger<NoteTaskSuggestionExtractionJob>.Instance);
        }

        public Guid Owner { get; }
        public Guid NoteId { get; } = Guid.NewGuid();
        public Guid ArtifactId { get; } = Guid.NewGuid();
        public Guid ArtifactVersionId { get; } = Guid.NewGuid();
        public PimDbContext Db { get; }
        public PimDbContext AdminDb => _database.Db;
        public FakeSourceReader Source { get; }
        public Wire Wire { get; }
        public AiModelProfileEntity Profile { get; }
        public NoteTaskSuggestionService Service { get; }
        public NoteTaskSuggestionExtractionJob Job { get; }

        public static async Task<Harness> CreateAsync(
            string prefix, bool dispatchAvailable = true)
        {
            PimDbContext.RegisterModuleAssembly(typeof(NoteTaskSuggestionsModule).Assembly);
            PimDbContext.RegisterModuleAssembly(typeof(TaskEntity).Assembly);
            var database = await TempMigrationDatabase.CreateAsync(prefix);
            await database.MigrateAsync();
            var owner = Guid.NewGuid();
            var currentUser = new FakeUser(owner);
            var secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            var profile = new AiModelProfileEntity
            {
                Id = Guid.NewGuid(),
                Revision = Guid.NewGuid(),
                Name = "synthetic-task-text",
                NormalizedName = "synthetic-task-text",
                BaseUrl = "https://synthetic-task-suggestion.invalid/v1",
                Model = "synthetic-task-text-model",
                Enabled = true,
                Capabilities = AiModelCapabilityFlags.Text,
                ApiKeySecret = secrets.Protect("synthetic-task-suggestion-key"),
                TimeoutSeconds = 30,
                MaxOutputTokens = 512
            };
            database.Db.Users.Add(new UserEntity
            {
                Id = owner,
                Username = $"task-suggestion-{owner:N}",
                Email = $"{owner:N}@synthetic.invalid",
                PasswordHash = "synthetic",
                IsActive = true
            });
            database.Db.AiModelProfiles.Add(profile);
            database.Db.AiModelRuntime.Add(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId,
                Revision = Guid.NewGuid(),
                Enabled = true,
                TextModelId = profile.Id
            });
            await database.Db.SaveChangesAsync();

            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3))
                .AddInterceptors(new UtcDateTimeOffsetParameterInterceptor())
                .Options;
            var db = new PimDbContext(options, currentUser);
            var source = new FakeSourceReader
            {
                NoteId = Guid.NewGuid(),
                ArtifactId = Guid.NewGuid(),
                ArtifactVersionId = Guid.NewGuid(),
                ContentMarkdown = "# 固定笔记\n\n明天先补测试，再完成实现。",
                InputSha256 = NoteTaskSuggestionService.Hash("synthetic-input"),
                ManifestSha256 = NoteTaskSuggestionService.Hash("synthetic-manifest"),
                GenerationTaskId = Guid.NewGuid()
            };
            source.ContentSha256 = NoteTaskSuggestionService.Hash(source.ContentMarkdown);
            var wire = new Wire();
            return new Harness(database, owner, options, currentUser, secrets, db, source, wire, profile,
                dispatchAvailable ? new RecordingJobs() : null);
        }

        public StartNoteTaskSuggestionExtractionRequest Request() => new(
            Guid.NewGuid(),
            Owner,
            Source.NoteId,
            Source.ArtifactId,
            Source.ArtifactVersionId,
            Source.ContentSha256,
            new(Profile.Id, Profile.Revision, Profile.BaseUrl, true),
            true);

        public void AdvanceSourceVersion()
        {
            Source.ArtifactVersionId = Guid.NewGuid();
            Source.GenerationTaskId = Guid.NewGuid();
            Source.ContentMarkdown += "\n\n重新整理后的同一事项仍应沿用原关联。";
            Source.ContentSha256 = NoteTaskSuggestionService.Hash(Source.ContentMarkdown);
            Source.InputSha256 = NoteTaskSuggestionService.Hash($"synthetic-input-{Source.ArtifactVersionId:N}");
            Source.ManifestSha256 = NoteTaskSuggestionService.Hash($"synthetic-manifest-{Source.ArtifactVersionId:N}");
        }

        public PimDbContext NewContext() => new(_options, _currentUser);

        public PimDbContext NewContextFor(Guid owner) => new(_options, new FakeUser(owner));

        public CalendarService NewCalendarService(PimDbContext db, Guid owner) => new(
            db,
            new FakeUser(owner),
            new RecurrenceService(NullLogger<RecurrenceService>.Instance),
            new EventAttachmentService(db),
            TimeProvider.System);

        public NoteTaskSuggestionService NewService(
            PimDbContext db, bool dispatchAvailable = true)
        {
            var materials = new NoteMaterialUploadStore(Options.Create(new NoteMaterialUploadOptions
            {
                StorageRoot = Path.Combine(Path.GetTempPath(), "pim-task-suggestion-materials",
                    Guid.NewGuid().ToString("N"))
            }));
            return new(
                db,
                _currentUser,
                Source,
                materials,
                CreateCalendarService(db),
                TimeProvider.System,
                NullLogger<NoteTaskSuggestionService>.Instance,
                dispatchAvailable ? new RecordingJobs() : null);
        }

        public async Task<NoteTaskSuggestionDto> CreateSuggestionAsync(
            string title = "先写测试",
            NoteTaskSuggestionKindDto kind = NoteTaskSuggestionKindDto.Task,
            DateTimeOffset? due = null,
            DateTimeOffset? start = null,
            DateTimeOffset? end = null,
            string? timeZoneId = null)
        {
            var extraction = await Service.StartExtractionAsync(Request(), default);
            var payload = new Dictionary<string, object?>
            {
                ["kind"] = kind.ToString(),
                ["title"] = title,
                ["description"] = "覆盖真实 PostgreSQL",
                ["priority"] = 9,
                ["due"] = kind == NoteTaskSuggestionKindDto.Task
                    ? due ?? DateTimeOffset.Parse("2026-11-02T01:00:00Z")
                    : null,
                ["start"] = kind == NoteTaskSuggestionKindDto.Event ? start : null,
                ["end"] = kind == NoteTaskSuggestionKindDto.Event ? end : null,
                ["timeZoneId"] = kind == NoteTaskSuggestionKindDto.Event ? timeZoneId : null
            };
            Wire.Reply = JsonSerializer.Serialize(new
            {
                suggestions = new[] { payload }
            });
            await Job.RunAsync(Owner, extraction.Id, default);
            Db.ChangeTracker.Clear();
            var stored = await AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
                .SingleAsync(x => x.ExtractionId == extraction.Id);
            return await Service.GetAsync(stored.Id, default);
        }

        public async Task WaitForJobClaimAsync(Guid extractionId, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                AdminDb.ChangeTracker.Clear();
                var status = await AdminDb.Set<NoteTaskSuggestionExtractionEntity>()
                    .AsNoTracking()
                    .Where(x => x.Id == extractionId)
                    .Select(x => x.Status)
                    .SingleAsync(ct);
                if (status == NoteTaskSuggestionExtractionStatuses.Running)
                    return;
                await Task.Delay(25, ct);
            }
            throw new TimeoutException("Extraction job did not claim before the test deadline");
        }

        public NoteTaskSuggestionExtractionJob NewJob(PimDbContext db)
        {
            var calls = new AiTextCallService(db, _secrets, new AiProfileChatClientFactory(Wire),
                _currentUser, new AiTextCallGate());
            return new NoteTaskSuggestionExtractionJob(db, CreateCalendarService(db),
                new AiTextNoteTaskSuggestionModelClient(calls),
                TimeProvider.System,
                NullLogger<NoteTaskSuggestionExtractionJob>.Instance);
        }

        public async Task DeleteTaskAsync(Guid id)
        {
            await CreateCalendarService(Db).DeleteTaskAsync(id, default);
            Db.ChangeTracker.Clear();
        }

        public async Task DeleteEventAsync(Guid id)
        {
            await CreateCalendarService(Db).DeleteEventAsync(id, default);
            Db.ChangeTracker.Clear();
        }

        private CalendarService CreateCalendarService(PimDbContext db) => NewCalendarService(db, Owner);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    private sealed class RecordingJobs : IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            Guid.NewGuid().ToString("N");

        public bool ChangeState(
            string jobId, Hangfire.States.IState state, string expectedState) => true;
    }

    private sealed class FakeUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId { get; } = id;
        public string? Role => "user";
    }

    private sealed class FakeSourceReader : INoteTaskSuggestionSourceReader
    {
        public Guid NoteId { get; set; }
        public Guid ArtifactId { get; set; }
        public Guid ArtifactVersionId { get; set; }
        public string ContentMarkdown { get; set; } = "";
        public string ContentSha256 { get; set; } = "";
        public string InputSha256 { get; set; } = "";
        public string ManifestSha256 { get; set; } = "";
        public Guid? GenerationTaskId { get; set; }
        public int ReadCount { get; private set; }

        public Task<NoteTaskSuggestionSource> ReadAsync(Guid artifactId, Guid versionId, CancellationToken ct)
        {
            ReadCount++;
            return Task.FromResult(new NoteTaskSuggestionSource(
                NoteId,
                ArtifactId,
                ArtifactVersionId,
                ContentMarkdown,
                ContentSha256,
                InputSha256,
                ManifestSha256,
                GenerationTaskId));
        }
    }

    private sealed class Wire : IHttpClientFactory
    {
        public string Reply { get; set; } = """{"suggestions":[]}""";
        public TimeSpan Delay { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public string? LastRequestBody { get; private set; }

        public HttpClient CreateClient(string name) =>
            new(new AiTextResponseLimitHandler { InnerHandler = new Handler(this) });

        private sealed class Handler(Wire owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner._calls);
                if (owner.Delay > TimeSpan.Zero)
                    await Task.Delay(owner.Delay, ct);
                owner.LastRequestBody = await request.Content!.ReadAsStringAsync(ct);
                var body = JsonSerializer.Serialize(new
                {
                    id = "synthetic-task-suggestion",
                    created = 1793500000,
                    model = "synthetic-task-text-model",
                    @object = "chat.completion",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            finish_reason = "stop",
                            message = new { role = "assistant", content = owner.Reply }
                        }
                    },
                    usage = new { prompt_tokens = 12, completion_tokens = 8, total_tokens = 20 }
                });
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
        }

        private int _calls;
    }
}
