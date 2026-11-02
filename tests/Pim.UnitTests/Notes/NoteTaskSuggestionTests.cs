using System.Net;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
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
            var calendar = Calendar(db);
            var calls = new AiTextCallService(db, secrets, new AiProfileChatClientFactory(wire),
                currentUser, new AiTextCallGate());
            Service = new NoteTaskSuggestionService(db, currentUser, source, calendar,
                TimeProvider.System, NullLogger<NoteTaskSuggestionService>.Instance, jobs);
            Job = new NoteTaskSuggestionExtractionJob(db,
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

        public PimDbContext NewContext() => new(_options, _currentUser);

        public NoteTaskSuggestionService NewService(
            PimDbContext db, bool dispatchAvailable = true) => new(
            db,
            _currentUser,
            Source,
            Calendar(db),
            TimeProvider.System,
            NullLogger<NoteTaskSuggestionService>.Instance,
            dispatchAvailable ? new RecordingJobs() : null);

        public async Task<NoteTaskSuggestionDto> CreateSuggestionAsync(string title = "先写测试")
        {
            var extraction = await Service.StartExtractionAsync(Request(), default);
            Wire.Reply = JsonSerializer.Serialize(new
            {
                suggestions = new[]
                {
                    new
                    {
                        title,
                        description = (string?)"覆盖真实 PostgreSQL",
                        priority = 9,
                        due = "2026-11-02T01:00:00Z"
                    }
                }
            });
            await Job.RunAsync(Owner, extraction.Id, default);
            Db.ChangeTracker.Clear();
            var stored = await AdminDb.Set<NoteTaskSuggestionEntity>().AsNoTracking()
                .SingleAsync(x => x.ExtractionId == extraction.Id);
            return await Service.GetAsync(stored.Id, default);
        }

        public NoteTaskSuggestionExtractionJob NewJob(PimDbContext db)
        {
            var calls = new AiTextCallService(db, _secrets, new AiProfileChatClientFactory(Wire),
                _currentUser, new AiTextCallGate());
            return new NoteTaskSuggestionExtractionJob(db,
                new AiTextNoteTaskSuggestionModelClient(calls),
                TimeProvider.System,
                NullLogger<NoteTaskSuggestionExtractionJob>.Instance);
        }

        private CalendarService Calendar(PimDbContext db) => new(
            db,
            _currentUser,
            new RecurrenceService(NullLogger<RecurrenceService>.Instance),
            new EventAttachmentService(db),
            TimeProvider.System);

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
