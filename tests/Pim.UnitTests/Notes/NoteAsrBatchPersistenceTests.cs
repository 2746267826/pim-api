using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Notes;

/// <summary>Synthetic PostgreSQL/HTTP only: no paid request, credential, or real audio.</summary>
[Collection("NoteAsrBatchSyntheticPg")]
public sealed class NoteAsrBatchPersistenceTests
{
    private static readonly Uri Origin = new("https://batch-api-cn.xiaomimimo.com/v1/");
    private static string? ConnectionString => Environment.GetEnvironmentVariable("PIM_ASR516_PG");

    [SkippableFact]
    public async Task Input_is_bounded_and_custom_ids_cannot_escape_attempt_segments()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await AssertRejected(JsonlBytes(seed, "seg-0", "seg-99"), null, 40301);
        await AssertRejected(JsonlBytes(seed, "seg-0"), null, 40901);
        await AssertRejected(JsonlBytes(seed, "seg-0", "seg-0"), null, 40001);
        await AssertRejected(Encoding.UTF8.GetBytes("{\n"), null, 40001);
        await AssertRejected([0xff, 0x0a], null, 40001);
        await AssertRejected(Encoding.UTF8.GetBytes(BatchLine("seg-0", seed, model: "other-model") + "\n" +
                                                     BatchLine("seg-1", seed) + "\n"), null, 40001);
        await AssertRejected(Encoding.UTF8.GetBytes(BatchLine("seg-0", seed, includeAudio: false) + "\n" +
                                                     BatchLine("seg-1", seed) + "\n"), null, 40001);
        await AssertRejected(Encoding.UTF8.GetBytes(
            "{\"custom_id\":\"seg-0\",\"method\":\"POST\",\"url\":\"/v1/chat/completions\",\"body\":{}}\n" +
            BatchLine("seg-1", seed) + "\n"), null, 40001);
        var valid = JsonlBytes(seed, "seg-0", "seg-1");
        await AssertRejected(valid, valid.Length + 1L, 40001);
        await AssertRejected(valid, MimoBatchDefaults.MaxJsonlBytes + 1L, 41301);

        await using var db = NewDb(seed.Owner);
        Assert.Equal(0, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == seed.Owner));
        Assert.Empty(factory.Requests);
        await CleanupAsync(seed.Owner);

        async Task AssertRejected(byte[] bytes, long? declaredLength, int expectedCode)
        {
            await using var db = NewDb(seed.Owner);
            var service = NewService(db, seed.Owner, factory);
            var error = await Assert.ThrowsAsync<DomainException>(() => StartAsync(
                service, seed, bytes, declaredLength));
            Assert.Equal(expectedCode, error.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task Jsonl_audio_must_match_the_immutable_material_plan_and_frozen_prompt()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));
        var replaced = Encoding.UTF8.GetBytes(
            BatchLine("seg-0", seed, audioOverride: seed.AudioSlices[1]) + "\n" +
            BatchLine("seg-1", seed) + "\n");
        var invalidBase64 = Encoding.UTF8.GetBytes(
            BatchLine("seg-0", seed, dataUrlOverride: "data:audio/wav;base64,%%%%") + "\n" +
            BatchLine("seg-1", seed) + "\n");
        var wrongPrompt = Encoding.UTF8.GetBytes(
            BatchLine("seg-0", seed, promptOverride: "分析并总结这段录音") + "\n" +
            BatchLine("seg-1", seed) + "\n");
        var wrongFormat = Encoding.UTF8.GetBytes(
            BatchLine("seg-0", seed, formatOverride: "mp3",
                dataUrlOverride: MimoAudioFormats.DataUrl("mp3", seed.AudioSlices[0])) + "\n" +
            BatchLine("seg-1", seed) + "\n");
        var wrongSignatureAudio = seed.AudioSlices[0].ToArray();
        wrongSignatureAudio[0] ^= 1;
        var wrongSignature = Encoding.UTF8.GetBytes(
            BatchLine("seg-0", seed, audioOverride: wrongSignatureAudio) + "\n" +
            BatchLine("seg-1", seed) + "\n");
        var extraBodyField = Encoding.UTF8.GetBytes(
            BatchLine("seg-0", seed).Replace("\"stream\":false", "\"stream\":false,\"metadata\":{}",
                StringComparison.Ordinal) + "\n" + BatchLine("seg-1", seed) + "\n");

        await AssertRejected(replaced, 40301);
        await AssertRejected(invalidBase64, 40001);
        await AssertRejected(wrongPrompt, 40001);
        await AssertRejected(wrongFormat, 40001);
        await AssertRejected(wrongSignature, 40301);
        await AssertRejected(extraBodyField, 40001);
        await AssertRejected(JsonlBytes(seed, "seg-0", "seg-1", "seg-2"), 40301);

        await UpdateAttemptAsync(seed, attempt => attempt.VersionId = Guid.NewGuid());
        await AssertRejected(JsonlBytes(seed, "seg-0", "seg-1"), 40401);
        Assert.Empty(factory.Requests);
        await CleanupAsync(seed.Owner);

        async Task AssertRejected(byte[] bytes, int expectedCode)
        {
            await using var db = NewDb(seed.Owner);
            var error = await Assert.ThrowsAsync<DomainException>(() => StartAsync(
                NewService(db, seed.Owner, factory), seed, bytes));
            Assert.Equal(expectedCode, error.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task Frozen_prompt_template_and_rendered_hashes_must_be_exact()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await AssertRejected(attempt => attempt.PromptTemplate = "", 40901);
        await AssertRejected(attempt => attempt.PromptRendered = "", 40901);
        await AssertRejected(attempt => attempt.PromptTemplateSha256 = new string('0', 64), 40901);
        await AssertRejected(attempt => attempt.PromptTemplateRevision = "asr-prompt-v1:0000000000000000", 40901);
        await AssertRejected(attempt => attempt.PromptSha256 = new string('0', 64), 40901);
        await AssertRejected(attempt => attempt.PromptRevision = "asr-prompt-v1:0000000000000000", 40901);
        await AssertRejected(attempt => attempt.PromptRendered += "\n分析并总结", 40901);

        Assert.Empty(factory.Requests);
        await CleanupAsync(seed.Owner);

        async Task AssertRejected(Action<NoteAsrAttemptEntity> mutate, int expectedCode)
        {
            await UpdateAttemptAsync(seed, mutate);
            await using var db = NewDb(seed.Owner);
            var error = await Assert.ThrowsAsync<DomainException>(() => StartAsync(
                NewService(db, seed.Owner, factory), seed));
            Assert.Equal(expectedCode, error.ErrorCode);
            await RestorePromptAsync(seed);
        }
    }

    [SkippableFact]
    public async Task Cross_owner_task_consent_and_state_bindings_are_rejected_before_network()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        // Seed order matters: SeedAsync prunes rows owned by anyone else, so the owner under
        // test must be seeded last or its attempt/task rows would be removed.
        var other = await SeedAsync();
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await using (var db = NewDb(other.Owner))
        {
            var service = NewService(db, other.Owner, factory);
            var error = await Assert.ThrowsAsync<DomainException>(() => StartAsync(service, seed));
            Assert.Equal(40401, error.ErrorCode);
        }
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var wrongTask = await Assert.ThrowsAsync<DomainException>(() => StartAsync(service, seed,
                taskId: Guid.NewGuid()));
            Assert.Equal(40901, wrongTask.ErrorCode);
            var wrongConsent = await Assert.ThrowsAsync<DomainException>(() => StartAsync(service, seed,
                consentRef: "other-consent"));
            Assert.Equal(40901, wrongConsent.ErrorCode);
        }

        await UpdateAttemptAsync(seed, attempt => attempt.State = "Leased");
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var staleState = await Assert.ThrowsAsync<DomainException>(() => StartAsync(service, seed));
            Assert.Equal(40901, staleState.ErrorCode);
        }

        Assert.Empty(factory.Requests);
        await CleanupAsync(seed.Owner);
        await CleanupAsync(other.Owner);
    }

    [SkippableFact]
    public async Task Disabled_capability_gate_blocks_every_submission_entry_before_any_mutation()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var routeConfig = await AddRouteConfigAsync(seed.Owner, seed.ProfileId, "route-consent-1");
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory, allowBatch: false);
            await using var stream = new MemoryStream(JsonlBytes(seed, "seg-0", "seg-1"), writable: false);
            var multipart = await Assert.ThrowsAsync<DomainException>(() => service.StartAsync(
                new StartNoteAsrBatchRequest(seed.TaskId, seed.AttemptId, seed.ProfileId, "consent-1",
                    true), stream, stream.Length, "input.jsonl", CancellationToken.None));
            Assert.Equal(40901, multipart.ErrorCode);

            var sourceRevision = (await Tasks(seed.Owner)
                .ReadAsync(seed.Owner, seed.TaskId, CancellationToken.None)).Revision;
            var fromTask = await Assert.ThrowsAsync<DomainException>(() => service.StartFromTaskAsync(
                new StartNoteAsrBatchFromTaskRequest(Guid.NewGuid(), seed.TaskId, sourceRevision, seed.Owner,
                    new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                        routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
                    new NoteAsrBatchTarget(seed.ProfileId, seed.ProfileRevision, "consent-1", true, true),
                    new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Manual, "gate closed")),
                CancellationToken.None));
            Assert.Equal(40901, fromTask.ErrorCode);

            var retry = await Assert.ThrowsAsync<DomainException>(() => service.RetryFailedItemsAsync(
                new RetryFailedNoteAsrBatchRequest(Guid.NewGuid(), Guid.NewGuid(), seed.Owner,
                    new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                        routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
                    new NoteAsrBatchTarget(seed.ProfileId, seed.ProfileRevision, "consent-1", true, true),
                    new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Quality, "gate closed")),
                CancellationToken.None));
            Assert.Equal(40901, retry.ErrorCode);
        }

        Assert.Empty(factory.Requests);
        await using (var db = NewDb(seed.Owner))
        {
            Assert.Equal(0, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == seed.Owner));
            Assert.Equal(1, await db.NoteAsrAttempts.CountAsync(x => x.OwnerId == seed.Owner));
            Assert.Equal(1, Tasks(seed.Owner).Enumerate(seed.Owner).Count());
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Batch_scope_confirmation_must_be_frozen_in_the_request_and_task_route()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var error = await Assert.ThrowsAsync<DomainException>(() => StartAsync(
                service, seed, batchScopeConfirmed: false));
            Assert.Equal(40001, error.ErrorCode);
        }

        var tasks = Tasks(seed.Owner);
        await tasks.UpdateAsync(seed.Owner, seed.TaskId, current =>
        {
            current.Request = current.Request with
            {
                Route = current.Request.Route! with { BatchScopeConfirmed = false }
            };
        }, CancellationToken.None);
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var error = await Assert.ThrowsAsync<DomainException>(() => StartAsync(service, seed));
            Assert.Equal(40901, error.ErrorCode);
        }

        Assert.Empty(factory.Requests);
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Profile_permissions_and_credentials_are_checked_before_submission()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await RejectProfile(profile => profile.Enabled = false);
        await RejectProfile(profile => profile.Capabilities = AiModelCapabilityFlags.Text);
        await RejectProfile(profile => profile.ApiKeySecret = "");
        await RejectProfile(profile => profile.AsrPresetId = "mimo-asr");
        await RejectProfile(profile => profile.Model = "MIMO-V2.6-PRO");
        await RejectProfile(profile => profile.AsrBatchBaseUrl = "https://api.example.invalid/v1/");
        await RejectProfile(profile => profile.AsrBatchBaseUrl = "http://batch-api.example.invalid/v1/");
        await RejectProfile(profile => profile.Revision = Guid.NewGuid());
        await UpdateRuntimeAsync(false);
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var disabledRuntime = await Assert.ThrowsAsync<DomainException>(() => StartAsync(service, seed));
            Assert.Equal(40901, disabledRuntime.ErrorCode);
        }
        await UpdateRuntimeAsync(true);

        Assert.Empty(factory.Requests);
        await CleanupAsync(seed.Owner);

        async Task RejectProfile(Action<AiModelProfileEntity> mutate)
        {
            await UpdateProfileAsync(seed, mutate);
            await using var db = NewDb(seed.Owner);
            var service = NewService(db, seed.Owner, factory);
            var error = await Assert.ThrowsAsync<DomainException>(() => StartAsync(service, seed));
            Assert.Equal(40901, error.ErrorCode);
            await ResetProfileAsync(seed);
        }
    }

    [SkippableTheory]
    [InlineData("upload", 400, "Failed", "batch_upload_rejected")]
    [InlineData("upload", 503, "Unknown", "batch_upload_unknown")]
    [InlineData("upload", 429, "Unknown", "batch_upload_unknown")]
    [InlineData("create", 400, "Failed", "batch_create_rejected")]
    [InlineData("create", 500, "Unknown", "batch_create_unknown")]
    [InlineData("create", 429, "Unknown", "batch_create_unknown")]
    [InlineData("create_timeout", 0, "Unknown", "batch_create_unknown")]
    public async Task Explicit_rejections_and_uncertain_create_outcomes_are_not_retried(
        string surface, int statusCode, string expectedStatus, string expectedError)
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var status = (HttpStatusCode)statusCode;
        var factory = new SyntheticFactory(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return surface == "upload" ? JsonResponse(status, new { error = "synthetic" }) : UploadOk();
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
            {
                if (surface == "create_timeout")
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    return BatchOk("batch-timeout");
                }
                return JsonResponse(status, new { error = "synthetic" });
            }
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        })
        {
            CreateTimeout = TimeSpan.FromMilliseconds(50)
        };

        NoteAsrBatchJobDto job;
        await using (var db = NewDb(seed.Owner))
        {
            job = await StartAsync(NewService(db, seed.Owner, factory), seed);
        }

        Assert.Equal(expectedStatus, job.Status);
        Assert.Equal(expectedError, job.LastErrorCode);
        Assert.Null(job.BatchId);
        Assert.Equal(surface == "upload" ? 0 : 1, factory.CreateCalls);
        Assert.Equal(1, factory.UploadCalls);
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Caller_cancellation_during_create_persists_unknown_and_poll_never_recreates()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var createStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SyntheticFactory(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return UploadOk();
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
            {
                createStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        using var caller = new CancellationTokenSource();
        Task<NoteAsrBatchJobDto> start;
        await using (var db = NewDb(seed.Owner))
        {
            start = StartAsync(NewService(db, seed.Owner, factory), seed, ct: caller.Token);
            await createStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var job = await start;
            Assert.Equal("Unknown", job.Status);
            Assert.Equal("batch_create_unknown", job.LastErrorCode);
            Assert.Equal("file-in-1", job.InputFileId);
            Assert.Null(job.BatchId);
        }

        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var polled = await service.PollAsync(
                await db.NoteAsrBatchJobs.Where(x => x.OwnerId == seed.Owner).Select(x => x.Id).SingleAsync(),
                CancellationToken.None);
            Assert.Equal("Unknown", polled.Status);
            Assert.Equal(1, factory.CreateCalls);
            var cancel = await Assert.ThrowsAsync<DomainException>(() =>
                service.CancelAsync(polled.Id, CancellationToken.None));
            Assert.Equal(40901, cancel.ErrorCode);
        }

        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Known_handle_survives_canceled_caller_and_sink_rejects_wrong_binding()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, intent) = await SeedPendingJobAsync(seed);
        var known = new MimoBatchRef("batch-known", "validating", MimoBatchStatusKind.Validating,
            DateTimeOffset.FromUnixTimeSeconds(1780009000), 2, 0, 0, "file-in-1", "file-out", "file-err");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await using (var db = NewDb(seed.Owner))
        {
            var sink = new EfMimoBatchHandleSink(db);
            await sink.SaveAsync(new MimoBatchCreateOutcome.Known(known), intent, canceled.Token);
        }
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Equal("batch-known", job.BatchId);
            Assert.Equal("file-out", job.OutputFileId);
            Assert.Equal("file-err", job.ErrorFileId);
            var sink = new EfMimoBatchHandleSink(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.SaveAsync(
                new MimoBatchCreateOutcome.Known(known),
                intent with { MaterialVersion = Guid.NewGuid().ToString("D") }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.SaveAsync(
                new MimoBatchCreateOutcome.Known(known),
                intent with { Owner = Guid.NewGuid() }, CancellationToken.None));
        }

        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Restart_uses_immutable_snapshot_and_imports_partial_files_only_once()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var polls = 0;
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return Task.FromResult(UploadOk());
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
                return Task.FromResult(BatchOk("batch-1"));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-1")
            {
                var poll = Interlocked.Increment(ref polls);
                return Task.FromResult(poll == 1
                    ? BatchOk("batch-1", "in_progress", null, null, 2, 0, 0)
                    : BatchOk("batch-1", "completed", "file-out", "file-err", 2, 2, 0));
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return Task.FromResult(FileContent(OutputJsonl()));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-err/content")
                return Task.FromResult(FileContent(ErrorJsonl()));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        Guid jobId;
        await using (var db = NewDb(seed.Owner))
        {
            var job = await StartAsync(NewService(db, seed.Owner, factory), seed);
            jobId = job.Id;
            Assert.Equal("Validating", job.Status);
            Assert.Equal("file-in-1", job.InputFileId);
        }

        await UpdateProfileAsync(seed, profile =>
        {
            profile.Revision = Guid.NewGuid();
            profile.BaseUrl = "https://changed.example.invalid/v1/";
            profile.AsrBatchBaseUrl = "https://batch-api-changed.invalid/v1/";
            profile.ApiKeySecret = "changed-key";
        });
        await UpdateRuntimeAsync(false);

        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var running = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("InProgress", running.Status);
            var completed = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Completed", completed.Status);
            Assert.True(completed.OutputImported);
            Assert.Equal("partial_or_failed_items", completed.LastErrorCode);
            Assert.Contains("seg-0", completed.ResultJson);
            Assert.Contains("seg-1", completed.ResultJson);
            Assert.Contains("attempt", completed.LastErrorMessage);
            Assert.Equal("batch", await db.NoteAsrSegmentEvidence.Where(x =>
                    x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId && x.SegmentIndex == 0)
                .Select(x => x.ReceiptKind).SingleAsync());
            Assert.Equal("Failed", await db.NoteAsrAttempts.Where(x => x.Id == seed.AttemptId)
                .Select(x => x.State).SingleAsync());
        }

        var downloadCount = factory.ContentCalls;
        Assert.Equal(2, downloadCount);
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var replayed = await service.PollAsync(jobId, CancellationToken.None);
            Assert.True(replayed.OutputImported);
        }
        Assert.Equal(downloadCount, factory.ContentCalls);
        Assert.All(factory.Origins, value => Assert.Equal(Origin, value));
        Assert.All(factory.ApiKeys, value => Assert.Equal("synthetic-key", value));
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Official_validating_zero_count_cannot_rewrite_expected_mapping_across_sink_and_polls()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var polls = 0;
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return Task.FromResult(UploadOk());
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
                return Task.FromResult(BatchOk("batch-official-zero", "validating", total: 0));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-official-zero")
            {
                var poll = Interlocked.Increment(ref polls);
                return Task.FromResult(poll == 1
                    ? BatchOk("batch-official-zero", "in_progress", total: 0)
                    : BatchOk("batch-official-zero", "completed", "file-out", total: 2, completed: 2));
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return Task.FromResult(FileContent(SuccessJsonl("seg-0", "seg-1")));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        NoteAsrBatchJobDto started;
        await using (var db = NewDb(seed.Owner))
        {
            started = await StartAsync(NewService(db, seed.Owner, factory), seed);
            Assert.Equal("Validating", started.Status);
            Assert.Equal(seed.ExpectedSegments, started.TotalItems);
            Assert.Equal(0, started.CompletedItems);
            var job = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == started.Id);
            var intent = JsonSerializer.Deserialize<MimoBatchIntent>(job.IntentJson)!;
            await new EfMimoBatchHandleSink(db).SaveAsync(new MimoBatchCreateOutcome.Known(
                new MimoBatchRef("batch-official-zero", "validating", MimoBatchStatusKind.Validating,
                    DateTimeOffset.FromUnixTimeSeconds(1780009000), 0, 0, 0,
                    "file-in-1", null, null)), intent, CancellationToken.None);
        }

        await using (var db = NewDb(seed.Owner))
        {
            var replay = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == started.Id);
            Assert.Equal(seed.ExpectedSegments, replay.TotalItems);
            var service = NewService(db, seed.Owner, factory);
            var inProgress = await service.PollAsync(started.Id, CancellationToken.None);
            Assert.Equal("InProgress", inProgress.Status);
            Assert.Equal(seed.ExpectedSegments, inProgress.TotalItems);
            Assert.Equal(0, inProgress.CompletedItems);
            var completed = await service.PollAsync(started.Id, CancellationToken.None);
            Assert.Equal("Completed", completed.Status);
            Assert.Equal(seed.ExpectedSegments, completed.TotalItems);
            Assert.Equal(2, completed.CompletedItems);
            Assert.True(completed.OutputImported);
            Assert.Equal(2, await db.NoteAsrSegmentEvidence.CountAsync(x =>
                x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId));
            Assert.Equal("Succeeded", await db.NoteAsrAttempts.Where(x => x.Id == seed.AttemptId)
                .Select(x => x.State).SingleAsync());
        }

        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Start_returns_persisted_handle_when_sink_commits_through_independent_context()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return Task.FromResult(UploadOk());
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
                return Task.FromResult(BatchOk("batch-independent", "validating", total: 0));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        NoteAsrBatchJobDto started;
        await using (var db = NewDb(seed.Owner))
        await using (var sinkDb = NewDb(seed.Owner))
        {
            // The sink commits through a different DbContext, mirroring production wiring;
            // the service context still tracks the pre-commit "Creating" entity.
            started = await StartAsync(NewService(db, seed.Owner, factory, sink: new EfMimoBatchHandleSink(sinkDb)), seed);
        }

        Assert.Equal("batch-independent", started.BatchId);
        Assert.Equal("validating", started.StatusRaw);
        Assert.Equal("Validating", started.Status);
        Assert.Equal(seed.ExpectedSegments, started.TotalItems);
        await using (var db = NewDb(seed.Owner))
        {
            var row = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == started.Id);
            Assert.Equal(started.BatchId, row.BatchId);
            Assert.Equal(started.Status, row.Status);
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Concurrent_start_has_one_submission_winner_and_one_existing_job_reader()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = 0;
        var factory = new SyntheticFactory(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref uploads) == 1)
                {
                    uploadStarted.TrySetResult();
                    await releaseUpload.Task.WaitAsync(ct);
                }
                return UploadOk();
            }
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
                return BatchOk("batch-one-winner");
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        NoteAsrBatchJobDto[] results;
        await using (var left = NewDb(seed.Owner))
        await using (var right = NewDb(seed.Owner))
        {
            var leftStart = StartAsync(NewService(left, seed.Owner, factory), seed);
            var rightStart = StartAsync(NewService(right, seed.Owner, factory), seed);
            await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var loserDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!leftStart.IsCompleted && !rightStart.IsCompleted)
            {
                Assert.True(DateTimeOffset.UtcNow < loserDeadline, "the non-winning Start did not return");
                await Task.Delay(20);
            }
            releaseUpload.TrySetResult();
            results = await Task.WhenAll(leftStart, rightStart);
        }

        Assert.Single(results.Select(x => x.Id).Distinct());
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Contains(results, x => x.Status == "Validating");
        Assert.Contains(results, x => x.Status == "Creating");
        await using (var db = NewDb(seed.Owner))
        {
            Assert.Equal(1, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == seed.Owner));
            Assert.Equal("Creating", await db.NoteAsrAttempts.Where(x => x.Id == seed.AttemptId)
                .Select(x => x.State).SingleAsync());
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Realtime_attempt_cannot_enter_the_batch_provider_path()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        await UpdateAttemptAsync(seed, attempt => attempt.ExecutionMode = NoteAsrExecutionMode.Realtime);
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await using (var db = NewDb(seed.Owner))
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                StartAsync(NewService(db, seed.Owner, factory), seed));
            Assert.Equal(40901, error.ErrorCode);
        }
        Assert.Empty(factory.Requests);
        await using (var db = NewDb(seed.Owner))
        {
            Assert.Equal("Queued", await db.NoteAsrAttempts.Where(x => x.Id == seed.AttemptId)
                .Select(x => x.State).SingleAsync());
            Assert.Equal(0, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == seed.Owner));
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Stale_poll_cannot_regress_terminal_or_resurrect_deleted_job()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.BatchId = "batch-1";
            job.StatusRaw = "in_progress";
            job.Status = "InProgress";
            await db.SaveChangesAsync();
        }
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-1")
                return Task.FromResult(BatchOk("batch-1", "in_progress", total: 2, completed: 0));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        await using (var staleDb = NewDb(seed.Owner))
        {
            // Track the in-progress row first, then let another context commit terminal.
            _ = await staleDb.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            await using (var other = NewDb(seed.Owner))
            {
                var job = await other.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
                job.StatusRaw = "completed";
                job.Status = "Completed";
                job.CompletedItems = seed.ExpectedSegments;
                job.OutputFileId = "file-out";
                job.OutputImportedAt = DateTimeOffset.UtcNow;
                job.ResultJson = "{}";
                await other.SaveChangesAsync();
            }
            var service = NewService(staleDb, seed.Owner, factory);
            var stale = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Completed", stale.Status);
            Assert.Equal("completed", stale.StatusRaw);
            Assert.Equal(seed.ExpectedSegments, stale.CompletedItems);
        }

        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.Status = "Deleted";
            job.StatusRaw = null;
            await db.SaveChangesAsync();
        }
        var getsBefore = factory.Requests.Count(x =>
            x.Method == HttpMethod.Get && x.Path == "/v1/batches/batch-1");
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var deleted = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Deleted", deleted.Status);
            var cancel = await Assert.ThrowsAsync<DomainException>(() =>
                service.CancelAsync(jobId, CancellationToken.None));
            Assert.Equal(40901, cancel.ErrorCode);
            await service.DeleteTerminalAsync(jobId, CancellationToken.None);
        }
        Assert.Equal(getsBefore, factory.Requests.Count(x =>
            x.Method == HttpMethod.Get && x.Path == "/v1/batches/batch-1"));
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Cancel_is_async_then_terminal_poll_imports_and_delete_keeps_history()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var polls = 0;
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return Task.FromResult(UploadOk());
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
                return Task.FromResult(BatchOk("batch-1"));
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/cancel", StringComparison.Ordinal))
                return Task.FromResult(BatchOk("batch-1", "cancelling", "file-out", null, 2, 1, 0));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-1")
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(BatchOk("batch-1", "cancelled", "file-out", "file-err", 2, 1, 1));
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return Task.FromResult(FileContent(OutputJsonl()));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-err/content")
                return Task.FromResult(FileContent(ErrorJsonl()));
            if (request.Method == HttpMethod.Delete && request.Path == "/v1/batches/batch-1")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        Guid jobId;
        await using (var db = NewDb(seed.Owner))
        {
            jobId = (await StartAsync(NewService(db, seed.Owner, factory), seed)).Id;
        }
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var cancelling = await service.CancelAsync(jobId, CancellationToken.None);
            Assert.Equal("Cancelling", cancelling.Status);
            Assert.Equal("file-out", cancelling.OutputFileId);
            Assert.False(cancelling.OutputImported);
            Assert.Equal(0, factory.ContentCalls);
            var earlyDelete = await Assert.ThrowsAsync<DomainException>(() =>
                service.DeleteTerminalAsync(jobId, CancellationToken.None));
            Assert.Equal(40901, earlyDelete.ErrorCode);
            var cancelled = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Cancelled", cancelled.Status);
            Assert.True(cancelled.OutputImported);
            Assert.NotNull(cancelled.ResultJson);
            await service.DeleteTerminalAsync(jobId, CancellationToken.None);
            var deleted = await service.GetAsync(jobId, CancellationToken.None);
            Assert.Equal("Deleted", deleted.Status);
            Assert.Equal(cancelled.ResultJson, deleted.ResultJson);
        }

        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Error_only_terminal_file_is_imported_and_failed_items_are_recorded_without_automatic_retry()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return Task.FromResult(UploadOk());
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
                return Task.FromResult(BatchOk("batch-1"));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-1")
                return Task.FromResult(BatchOk("batch-1", "failed", null, "file-err", 2, 0, 2));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-err/content")
                return Task.FromResult(FileContent(AllErrorsJsonl()));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        Guid jobId;
        await using (var db = NewDb(seed.Owner))
        {
            jobId = (await StartAsync(NewService(db, seed.Owner, factory), seed)).Id;
        }
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var failed = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Failed", failed.Status);
            Assert.True(failed.OutputImported);
            Assert.Equal("partial_or_failed_items", failed.LastErrorCode);
            Assert.Contains("attempt", failed.LastErrorMessage);
            Assert.Contains("seg-0", failed.ResultJson);
            Assert.Contains("seg-1", failed.ResultJson);
            Assert.Equal(1, await db.NoteAsrAttempts.CountAsync(x => x.OwnerId == seed.Owner));
            Assert.Equal(1, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == seed.Owner));
            Assert.Equal(1, factory.CreateCalls);
            Assert.Equal(1, factory.UploadCalls);
            Assert.Equal(1, factory.ContentCalls);
            Assert.Contains(factory.Requests, x => x.Path == "/v1/files/file-err/content");
            Assert.DoesNotContain(factory.Requests, x => x.Path.StartsWith("/v1/files/file-out", StringComparison.Ordinal));
        }

        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Crash_after_job_intent_becomes_durable_unknown_without_recreate()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));

        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory);
            var recovered = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Unknown", recovered.Status);
            Assert.Equal("batch_create_unknown", recovered.LastErrorCode);
            Assert.Null(recovered.BatchId);
            var cancel = await Assert.ThrowsAsync<DomainException>(() =>
                service.CancelAsync(jobId, CancellationToken.None));
            Assert.Equal(40901, cancel.ErrorCode);
        }

        Assert.Empty(factory.Requests);
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Known_batch_with_unknown_raw_status_stays_unknown_and_is_never_recreated()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/files", StringComparison.Ordinal))
                return Task.FromResult(UploadOk());
            if (request.Method == HttpMethod.Post && request.Path.EndsWith("/v1/batches", StringComparison.Ordinal))
                return Task.FromResult(BatchOk("batch-1", "mystery"));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-1")
                return Task.FromResult(BatchOk("batch-1", "mystery"));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        Guid jobId;
        await using (var db = NewDb(seed.Owner))
        {
            var started = await StartAsync(NewService(db, seed.Owner, factory), seed);
            jobId = started.Id;
            Assert.Equal("Unknown", started.Status);
            Assert.Equal("batch-1", started.BatchId);
            Assert.Equal("mystery", started.StatusRaw);
        }
        await using (var db = NewDb(seed.Owner))
        {
            var polled = await NewService(db, seed.Owner, factory)
                .PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Unknown", polled.Status);
            Assert.Equal("batch_status_unknown", polled.LastErrorCode);
            Assert.Equal("batch-1", polled.BatchId);
            Assert.Equal(1, factory.CreateCalls);
        }

        await CleanupAsync(seed.Owner);
    }

    [Fact]
    public void Factory_rejects_empty_or_control_character_credentials()
    {
        var factory = new MimoBatchClientFactory();
        Assert.Throws<ArgumentException>(() => factory.Create(Origin, ""));
        Assert.Throws<ArgumentException>(() => factory.Create(Origin, "synthetic\rkey"));
        using var lease = factory.Create(Origin, "synthetic-key");
        Assert.Equal("Bearer", lease.Http.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("synthetic-key", lease.Http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Background_poll_options_are_clamped_into_finite_bounds()
    {
        var low = new NoteAsrBatchPollOptions
        {
            PageSize = -10,
            MaxPagesPerRun = -1,
            PerJobTimeout = TimeSpan.FromSeconds(-1),
            PollBackoff = TimeSpan.FromSeconds(-1),
            UnknownCreateGrace = TimeSpan.FromSeconds(-1)
        }.Normalized();
        Assert.Equal(1, low.PageSize);
        Assert.Equal(1, low.MaxPagesPerRun);
        Assert.Equal(TimeSpan.FromSeconds(1), low.PerJobTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), low.PollBackoff);
        Assert.Equal(TimeSpan.FromMinutes(1), low.UnknownCreateGrace);

        var high = new NoteAsrBatchPollOptions
        {
            PageSize = int.MaxValue,
            MaxPagesPerRun = int.MaxValue,
            PerJobTimeout = TimeSpan.MaxValue,
            PollBackoff = TimeSpan.MaxValue,
            UnknownCreateGrace = TimeSpan.MaxValue
        }.Normalized();
        Assert.Equal(100, high.PageSize);
        Assert.Equal(20, high.MaxPagesPerRun);
        Assert.Equal(TimeSpan.FromMinutes(2), high.PerJobTimeout);
        Assert.Equal(TimeSpan.FromHours(1), high.PollBackoff);
        Assert.Equal(TimeSpan.FromDays(7), high.UnknownCreateGrace);
    }

    [SkippableFact]
    public async Task Background_job_only_polls_known_ids_and_marks_stale_creating_unknown()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (knownJobId, _) = await SeedPendingJobAsync(seed);
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == knownJobId);
            job.BatchId = "batch-known";
            job.StatusRaw = "in_progress";
            job.Status = "InProgress";
            await db.SaveChangesAsync();
        }
        Guid staleId;
        await using (var db = NewDb(seed.Owner))
        {
            var old = DateTimeOffset.UtcNow.AddHours(-2);
            var stale = new NoteAsrBatchJobEntity
            {
                OwnerId = seed.Owner, TaskId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
                Status = "Creating",
                Model = "mimo-v2.6-pro", BaseUrl = Origin.AbsoluteUri,
                ProfileRevision = seed.ProfileRevision, CredentialSecret = "synthetic-key",
                ConsentRef = "consent-1", TotalItems = seed.ExpectedSegments,
                CreatedAt = old, UpdatedAt = old
            };
            db.NoteAsrBatchJobs.Add(stale);
            await db.SaveChangesAsync();
            staleId = stale.Id;
        }
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-known")
                return Task.FromResult(BatchOk("batch-known", "in_progress", total: 2, completed: 0));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        var settings = new NoteAsrBatchPollOptions { PageSize = 25, PollBackoff = TimeSpan.Zero };
        await using (var db = NewDb(seed.Owner))
        {
            var pollJob = NewPollJob(NewService(db, seed.Owner, factory), db, settings);
            await pollJob.MarkStaleCreatingUnknownAsync(DateTimeOffset.UtcNow, settings, CancellationToken.None);
            Assert.Equal(1, await pollJob.RunPageAsync(DateTimeOffset.UtcNow, settings, CancellationToken.None));
        }
        await using (var db = NewDb(seed.Owner))
        {
            var known = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == knownJobId);
            Assert.Equal("InProgress", known.Status);
            Assert.NotNull(known.LastPolledAt);
            var stale = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == staleId);
            Assert.Equal("Unknown", stale.Status);
            Assert.Equal("batch_create_unknown", stale.LastErrorCode);
            Assert.Null(stale.BatchId);
        }
        Assert.Single(factory.Requests);
        Assert.Equal(0, factory.UploadCalls);
        Assert.Equal(0, factory.CreateCalls);
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Background_job_retries_terminal_missing_files_and_respects_backoff()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.BatchId = "batch-terminal";
            job.Status = "Completed";
            job.StatusRaw = "completed";
            job.CompletedItems = seed.ExpectedSegments;
            await db.SaveChangesAsync();
        }
        var polls = 0;
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-terminal")
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(BatchOk("batch-terminal", "completed", "file-out", null, 2, 2));
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return Task.FromResult(FileContent(SuccessJsonl("seg-0", "seg-1")));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        var settings = new NoteAsrBatchPollOptions
        {
            PageSize = 10, PollBackoff = TimeSpan.FromSeconds(30)
        };
        await using (var db = NewDb(seed.Owner))
        {
            var pollJob = NewPollJob(NewService(db, seed.Owner, factory), db, settings);
            Assert.Equal(1, await pollJob.RunPageAsync(DateTimeOffset.UtcNow, settings, CancellationToken.None));
        }
        Assert.Equal(1, polls);
        await using (var db = NewDb(seed.Owner))
        {
            var row = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.NotNull(row.OutputImportedAt);
            Assert.Equal(2, await db.NoteAsrSegmentEvidence.CountAsync(x =>
                x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId));
        }
        await using (var db = NewDb(seed.Owner))
        {
            var pollJob = NewPollJob(NewService(db, seed.Owner, factory), db, settings);
            Assert.Equal(0, await pollJob.RunPageAsync(DateTimeOffset.UtcNow, settings, CancellationToken.None));
        }
        Assert.Equal(1, polls);
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Parallel_polls_with_revision_token_never_regress_terminal_state()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        var providerId = "batch-race-" + seed.Owner.ToString("N");
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.BatchId = providerId;
            job.StatusRaw = "in_progress";
            job.Status = "InProgress";
            await db.SaveChangesAsync();
        }
        var arrivals = 0;
        var responses = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SyntheticFactory(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/" + providerId)
            {
                if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return Interlocked.Increment(ref responses) == 1
                    ? BatchOk(providerId, "completed", "file-out", null, 2, 2, 0)
                    : BatchOk(providerId, "in_progress", null, null, 2, 0, 0);
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return FileContent(SuccessJsonl("seg-0", "seg-1"));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });

        NoteAsrBatchJobDto[] results;
        await using (var left = NewDb(seed.Owner))
        await using (var right = NewDb(seed.Owner))
        {
            var leftPoll = NewService(left, seed.Owner, factory).PollAsync(jobId, CancellationToken.None);
            var rightPoll = NewService(right, seed.Owner, factory).PollAsync(jobId, CancellationToken.None);
            results = await Task.WhenAll(leftPoll, rightPoll);
        }
        Assert.Contains(results, x => x.Status == "Completed");
        Assert.DoesNotContain(results, x => x.Status == "Unknown");
        await using (var db = NewDb(seed.Owner))
        {
            var row = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Equal("Completed", row.Status);
            Assert.Equal("completed", row.StatusRaw);
            Assert.NotNull(row.OutputImportedAt);
            Assert.Equal(2, await db.NoteAsrSegmentEvidence.CountAsync(x =>
                x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId));
            Assert.Equal("Succeeded", await db.NoteAsrAttempts.Where(x => x.Id == seed.AttemptId)
                .Select(x => x.State).SingleAsync());
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Background_job_leases_attempts_so_failures_and_inactive_owners_do_not_starve_later_pages()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var inactiveOwner = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        await using (var db = NewDb(inactiveOwner))
        {
            var suffix = inactiveOwner.ToString("N")[..8];
            db.Users.Add(new UserEntity
            {
                Id = inactiveOwner, Username = "inactive-" + suffix, Email = suffix + "@example.invalid",
                PasswordHash = "synthetic", IsActive = false
            });
            db.NoteAsrBatchJobs.Add(NewBatchJobRow(inactiveOwner, "batch-inactive", "InProgress", "in_progress",
                baseTime));
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb(seed.Owner))
        {
            db.NoteAsrBatchJobs.Add(NewBatchJobRow(seed.Owner, "batch-fail", "InProgress", "in_progress",
                baseTime.AddMinutes(1)));
            db.NoteAsrBatchJobs.Add(NewBatchJobRow(seed.Owner, "batch-ok", "InProgress", "in_progress",
                baseTime.AddMinutes(2)));
            await db.SaveChangesAsync();
        }
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-fail")
                throw new HttpRequestException("synthetic network failure");
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-ok")
                return Task.FromResult(BatchOk("batch-ok", "in_progress", total: 2, completed: 0));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        var settings = new NoteAsrBatchPollOptions
        {
            PageSize = 1, MaxPagesPerRun = 3, PollBackoff = TimeSpan.FromSeconds(1)
        };
        NoteAsrBatchPollJob pollJob;
        await using (var db = NewDb(seed.Owner))
        {
            pollJob = NewPollJob(NewService(db, seed.Owner, factory), db, settings);
            await pollJob.RunAsync(CancellationToken.None);
            // Same run boundary again: leases must keep all three rows out of the next page.
            Assert.Equal(0, await pollJob.RunPageAsync(DateTimeOffset.UtcNow, settings, CancellationToken.None));
        }
        Assert.DoesNotContain(factory.Requests, x => x.Path.Contains("batch-inactive", StringComparison.Ordinal));
        Assert.Contains(factory.Requests, x => x.Path == "/v1/batches/batch-fail");
        Assert.Contains(factory.Requests, x => x.Path == "/v1/batches/batch-ok");
        await using (var db = NewDb(seed.Owner))
        {
            Assert.All(await db.NoteAsrBatchJobs.AsNoTracking().Where(x => x.OwnerId == seed.Owner).ToListAsync(),
                row => Assert.NotNull(row.LastPolledAt));
        }
        await using (var db = NewDb(inactiveOwner))
        {
            var inactive = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.BatchId == "batch-inactive");
            Assert.NotNull(inactive.LastPolledAt);
        }
        await CleanupAsync(seed.Owner);
        await CleanupAsync(inactiveOwner);
    }

    [SkippableFact]
    public async Task Concurrent_background_runs_claim_each_known_job_only_once()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        var providerId = "batch-poll-claim-" + seed.Owner.ToString("N");
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.BatchId = providerId;
            job.Status = "InProgress";
            job.StatusRaw = "in_progress";
            await db.SaveChangesAsync();
        }
        var gets = 0;
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/" + providerId)
            {
                Interlocked.Increment(ref gets);
                return Task.FromResult(BatchOk(providerId, "in_progress", total: 2, completed: 0));
            }
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        var settings = new NoteAsrBatchPollOptions
        {
            PageSize = 10, MaxPagesPerRun = 1, PollBackoff = TimeSpan.FromSeconds(30)
        };
        await using (var left = NewDb(seed.Owner))
        await using (var right = NewDb(seed.Owner))
        {
            var leftJob = NewPollJob(NewService(left, seed.Owner, factory), left, settings);
            var rightJob = NewPollJob(NewService(right, seed.Owner, factory), right, settings);
            await Task.WhenAll(leftJob.RunAsync(CancellationToken.None),
                rightJob.RunAsync(CancellationToken.None));
        }
        Assert.Equal(1, gets);
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Concurrent_cancel_and_poll_stay_terminal_and_delete_never_resurrects()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.BatchId = "batch-cancel-race";
            job.StatusRaw = "in_progress";
            job.Status = "InProgress";
            await db.SaveChangesAsync();
        }
        var arrivals = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SyntheticFactory(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post && request.Path == "/v1/batches/batch-cancel-race/cancel")
            {
                if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return BatchOk("batch-cancel-race", "cancelled", "file-out", null, 2, 2, 0);
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-cancel-race")
            {
                if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return BatchOk("batch-cancel-race", "completed", "file-out", null, 2, 2, 0);
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return FileContent(SuccessJsonl("seg-0", "seg-1"));
            if (request.Method == HttpMethod.Delete && request.Path == "/v1/batches/batch-cancel-race")
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        await using (var left = NewDb(seed.Owner))
        await using (var right = NewDb(seed.Owner))
        {
            var cancel = NewService(left, seed.Owner, factory).CancelAsync(jobId, CancellationToken.None);
            var poll = NewService(right, seed.Owner, factory).PollAsync(jobId, CancellationToken.None);
            var results = await Task.WhenAll(cancel, poll);
            Assert.All(results, x => Assert.Contains(x.Status, new[] { "Completed", "Cancelled", "Cancelling" }));
        }
        await using (var db = NewDb(seed.Owner))
        {
            var row = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Contains(row.Status, new[] { "Completed", "Cancelled" });
            Assert.Equal(2, await db.NoteAsrSegmentEvidence.CountAsync(x =>
                x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId));
            var service = NewService(db, seed.Owner, factory);
            await service.DeleteTerminalAsync(jobId, CancellationToken.None);
            var deleted = await service.GetAsync(jobId, CancellationToken.None);
            Assert.Equal("Deleted", deleted.Status);
            var getsBefore = factory.Requests.Count(x =>
                x.Method == HttpMethod.Get && x.Path == "/v1/batches/batch-cancel-race");
            var polled = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Deleted", polled.Status);
            Assert.Equal(getsBefore, factory.Requests.Count(x =>
                x.Method == HttpMethod.Get && x.Path == "/v1/batches/batch-cancel-race"));
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Concurrent_delete_and_stale_poll_never_resurrects_the_job()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        var providerId = "batch-delete-race-" + seed.Owner.ToString("N");
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.BatchId = providerId;
            job.Status = "Completed";
            job.StatusRaw = "completed";
            job.CompletedItems = seed.ExpectedSegments;
            job.OutputFileId = "file-out";
            await db.SaveChangesAsync();
        }
        var arrivals = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SyntheticFactory(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/" + providerId)
            {
                if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return BatchOk(providerId, "in_progress", total: 2, completed: 0);
            }
            if (request.Method == HttpMethod.Delete && request.Path == "/v1/batches/" + providerId)
            {
                if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return FileContent(SuccessJsonl("seg-0", "seg-1"));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        await using (var left = NewDb(seed.Owner))
        await using (var right = NewDb(seed.Owner))
        {
            var poll = NewService(left, seed.Owner, factory).PollAsync(jobId, CancellationToken.None);
            var delete = NewService(right, seed.Owner, factory).DeleteTerminalAsync(jobId, CancellationToken.None);
            await Task.WhenAll(poll, delete);
        }
        await using (var db = NewDb(seed.Owner))
        {
            var row = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Equal("Deleted", row.Status);
            Assert.Null(row.StatusRaw);
            var service = NewService(db, seed.Owner, factory);
            var replay = await service.PollAsync(jobId, CancellationToken.None);
            Assert.Equal("Deleted", replay.Status);
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Concurrent_terminal_polls_import_evidence_once()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var (jobId, _) = await SeedPendingJobAsync(seed);
        await using (var db = NewDb(seed.Owner))
        {
            var job = await db.NoteAsrBatchJobs.SingleAsync(x => x.Id == jobId);
            job.BatchId = "batch-import-race";
            job.StatusRaw = "completed";
            job.Status = "Completed";
            job.CompletedItems = seed.ExpectedSegments;
            job.OutputFileId = "file-out";
            await db.SaveChangesAsync();
        }
        var arrivals = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SyntheticFactory(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-import-race")
            {
                if (Interlocked.Increment(ref arrivals) == 2) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return BatchOk("batch-import-race", "completed", "file-out", null, 2, 2, 0);
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return FileContent(SuccessJsonl("seg-0", "seg-1"));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        await using (var left = NewDb(seed.Owner))
        await using (var right = NewDb(seed.Owner))
        {
            var first = NewService(left, seed.Owner, factory).PollAsync(jobId, CancellationToken.None);
            var second = NewService(right, seed.Owner, factory).PollAsync(jobId, CancellationToken.None);
            var results = await Task.WhenAll(first, second);
            Assert.All(results, x => Assert.Equal("Completed", x.Status));
        }
        await using (var db = NewDb(seed.Owner))
        {
            Assert.Equal(2, await db.NoteAsrSegmentEvidence.CountAsync(x =>
                x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId));
            var row = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == jobId);
            Assert.Equal("Completed", row.Status);
            Assert.NotNull(row.OutputImportedAt);
        }
        await CleanupAsync(seed.Owner);
    }

    [SkippableFact]
    public async Task Real_start_from_task_generates_a_full_batch_for_long_audio()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        // Two one-minute chunks: more than one minute of real audio, while each slice stays within
        // the material store's per-read boundary. The plan is still the 10-minute multimodal plan.
        var (material, _) = await SeedMaterialAsync(owner, NoteAsrPlan.SampleRate * 60);
        await using var db = NewDb(owner);
        await db.Database.EnsureCreatedAsync();
        await db.NoteAsrBatchJobs.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await db.NoteAsrSegmentEvidence.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await db.NoteAsrAttempts.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        var suffix = owner.ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        await db.Users.AddAsync(new UserEntity
        {
            Id = owner, Username = "real-" + suffix, Email = suffix + "@example.invalid",
            PasswordHash = "synthetic", IsActive = true
        });
        var profile = new AiModelProfileEntity
        {
            Id = Guid.NewGuid(), Revision = Guid.NewGuid(), Name = "Batch Real " + suffix,
            NormalizedName = "batch-real-" + suffix, BaseUrl = "https://api.example.invalid/v1/",
            Model = "mimo-v2.6-pro", Enabled = true, Capabilities = AiModelCapabilityFlags.Transcription,
            AsrPresetId = MimoMultimodalPreset.PresetId, AsrBatchBaseUrl = Origin.AbsoluteUri,
            ApiKeySecret = "synthetic-key", CreatedAt = now, UpdatedAt = now
        };
        await db.AiModelProfiles.AddAsync(profile);
        var runtime = await db.AiModelRuntime.SingleOrDefaultAsync(x => x.Id == AiModelRuntimeEntity.SingletonId);
        if (runtime is null)
            await db.AiModelRuntime.AddAsync(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId, Revision = Guid.NewGuid(), Enabled = true,
                TranscriptionModelId = profile.Id
            });
        else
        {
            runtime.Enabled = true;
            runtime.TranscriptionModelId = profile.Id;
            runtime.Revision = Guid.NewGuid();
        }
        await db.SaveChangesAsync();

        var (source, routeConfig) = await StartViaRealServiceAsync(owner, material, Guid.NewGuid(), profile);
        Assert.True(source.TotalSegments > 1, "the 90s source must produce more than one multimodal slice");
        var sourceState = await Tasks(owner).ReadAsync(owner, source.TaskId, CancellationToken.None);
        Assert.Equal(NoteAsrPlan.SampleRate * 60 * 10, sourceState.SamplesPerCall);
        Assert.All(sourceState.Steps, step => Assert.Equal(NoteAsrPlan.SamplesPerCall, step.Samples));

        // A distinct full reprocess is only valid from a finished source, so close this synthetic
        // source before submitting the child.
        await Tasks(owner).UpdateAsync(owner, source.TaskId, current =>
        {
            foreach (var step in current.Steps) step.Current.Status = "Succeeded";
            current.CompletionAcknowledged = true;
        }, CancellationToken.None);
        var terminal = NoteAsrTaskStore.Describe(await Tasks(owner)
            .ReadAsync(owner, source.TaskId, CancellationToken.None));
        Assert.Equal("Succeeded", terminal.Phase);

        var factory = new SyntheticFactory((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post && request.Path == "/v1/files"
                ? UploadOk()
                : request.Method == HttpMethod.Post && request.Path == "/v1/batches"
                    ? BatchOk("batch-real", total: source.TotalSegments)
                    : throw new InvalidOperationException("unexpected " + request.Path)));
        var consent = Consent(profile);
        await using (var serviceDb = NewDb(owner))
        {
            var service = NewService(serviceDb, owner, factory, noteAsr: RealNoteAsr(serviceDb, owner));
            var result = await service.StartFromTaskAsync(new StartNoteAsrBatchFromTaskRequest(
                Guid.NewGuid(), source.TaskId, terminal.Revision, owner,
                new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                    routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
                new NoteAsrBatchTarget(profile.Id, profile.Revision, "consent-real", true, true),
                new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Manual, "synthetic full batch")),
                CancellationToken.None);
            Assert.Equal(source.TaskId, result.SourceTaskId);
            Assert.Equal(source.TotalSegments, result.Job.TotalItems);
            Assert.Equal(source.TotalSegments, result.Task.TotalSegments);
            Assert.NotEqual(source.TaskId, result.Task.TaskId);
        }
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Distinct_full_child_is_rejected_for_queued_or_running_source()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var (owner, material, profile) = await SeedRealBatchOwnerAsync(NoteAsrPlan.SampleRate, "clone-guard");
        var (source, routeConfig) = await StartViaRealServiceAsync(owner, material, Guid.NewGuid(), profile);
        Assert.Equal("Queued", source.Phase);
        var factory = new SyntheticFactory((_, _) => throw new InvalidOperationException("must not send"));
        var request = new StartNoteAsrBatchFromTaskRequest(Guid.NewGuid(), source.TaskId, source.Revision,
            owner, new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
            new NoteAsrBatchTarget(profile.Id, profile.Revision, "consent-guard", true, true),
            new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Manual, "queued clone"));

        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner, factory, noteAsr: RealNoteAsr(db, owner));
            var queued = await Assert.ThrowsAsync<DomainException>(() =>
                service.StartFromTaskAsync(request, CancellationToken.None));
            Assert.Equal(40901, queued.ErrorCode);
        }
        Assert.Empty(factory.Requests);
        await using (var db = NewDb(owner))
        {
            Assert.Equal(0, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == owner));
            Assert.Equal(1, await db.NoteAsrAttempts.CountAsync(x => x.OwnerId == owner));
            Assert.Equal(1, Tasks(owner).Enumerate(owner).Count());
        }

        // A running source is equally ineligible once the child id is new.
        await Tasks(owner).UpdateAsync(owner, source.TaskId, current =>
            current.Steps[0].Current.Status = "Running", CancellationToken.None);
        await using (var db = NewDb(owner))
        {
            var revision = (await Tasks(owner).ReadAsync(owner, source.TaskId, CancellationToken.None)).Revision;
            var service = NewService(db, owner, factory, noteAsr: RealNoteAsr(db, owner));
            var running = await Assert.ThrowsAsync<DomainException>(() => service.StartFromTaskAsync(
                request with { ClientTaskId = Guid.NewGuid(), ExpectedSourceRevision = revision },
                CancellationToken.None));
            Assert.Equal(40901, running.ErrorCode);
        }
        Assert.Empty(factory.Requests);
        await using (var db = NewDb(owner))
        {
            Assert.Equal(0, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == owner));
            Assert.Equal(1, await db.NoteAsrAttempts.CountAsync(x => x.OwnerId == owner));
            Assert.Equal(1, Tasks(owner).Enumerate(owner).Count());
        }
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Initial_batch_submission_uses_the_prepared_task_without_cloning_or_recreating()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        var (material, _) = await SeedMaterialAsync(owner, NoteAsrPlan.SampleRate * 60);
        await using var seedDb = NewDb(owner);
        await seedDb.Database.EnsureCreatedAsync();
        await seedDb.NoteAsrBatchJobs.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await seedDb.NoteAsrSegmentEvidence.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await seedDb.NoteAsrAttempts.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        var suffix = owner.ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        await seedDb.Users.AddAsync(new UserEntity
        {
            Id = owner, Username = "initial-" + suffix, Email = suffix + "@example.invalid",
            PasswordHash = "synthetic", IsActive = true
        });
        var profile = new AiModelProfileEntity
        {
            Id = Guid.NewGuid(), Revision = Guid.NewGuid(), Name = "Batch Initial " + suffix,
            NormalizedName = "batch-initial-" + suffix, BaseUrl = "https://api.example.invalid/v1/",
            Model = "mimo-v2.6-pro", Enabled = true, Capabilities = AiModelCapabilityFlags.Transcription,
            AsrPresetId = MimoMultimodalPreset.PresetId, AsrBatchBaseUrl = Origin.AbsoluteUri,
            ApiKeySecret = "synthetic-key", CreatedAt = now, UpdatedAt = now
        };
        await seedDb.AiModelProfiles.AddAsync(profile);
        var runtime = await seedDb.AiModelRuntime.SingleOrDefaultAsync(x => x.Id == AiModelRuntimeEntity.SingletonId);
        if (runtime is null)
            await seedDb.AiModelRuntime.AddAsync(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId, Revision = Guid.NewGuid(), Enabled = true,
                TranscriptionModelId = profile.Id
            });
        else
        {
            runtime.Enabled = true;
            runtime.TranscriptionModelId = profile.Id;
            runtime.Revision = Guid.NewGuid();
        }
        await seedDb.SaveChangesAsync();

        var (source, routeConfig) = await StartViaRealServiceAsync(owner, material, Guid.NewGuid(), profile);
        Assert.Equal("Queued", source.Phase);
        Assert.Equal(NoteAsrExecutionMode.Batch, source.ExecutionMode);
        Assert.Equal("Queued", await seedDb.NoteAsrAttempts.Where(x => x.OwnerId == owner)
            .Select(x => x.State).SingleAsync());

        var factory = new SyntheticFactory((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post && request.Path == "/v1/files"
                ? UploadOk()
                : request.Method == HttpMethod.Post && request.Path == "/v1/batches"
                    ? BatchOk("batch-initial", total: source.TotalSegments)
                    : throw new InvalidOperationException("unexpected " + request.Path)));
        var request0 = new StartNoteAsrBatchFromTaskRequest(source.TaskId, source.TaskId, source.Revision,
            owner, new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
            new NoteAsrBatchTarget(profile.Id, profile.Revision, "consent-initial", true, true),
            new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Manual, "initial trusted batch"));

        Guid jobId;
        await using (var probe = NewDb(owner))
        {
            var stateValue = await probe.NoteAsrAttempts.Where(x => x.OwnerId == owner)
                .Select(x => x.State).SingleAsync();
            Assert.Equal("Queued", stateValue);
        }
        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner, factory, noteAsr: RealNoteAsr(db, owner));
            var result = await service.StartFromTaskAsync(request0, CancellationToken.None);
            Assert.Equal(source.TaskId, result.Task.TaskId);
            Assert.Equal(source.TaskId, result.SourceTaskId);
            Assert.Equal(source.TotalSegments, result.Job.TotalItems);
            jobId = result.Job.Id;
        }
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);
        await using (var db = NewDb(owner))
        {
            Assert.Equal(1, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == owner));
            Assert.Equal(1, await db.NoteAsrAttempts.CountAsync(x => x.OwnerId == owner));
            Assert.Equal(1, Tasks(owner).Enumerate(owner).Count());
        }

        await using (var db = NewDb(owner))
        {
            var revision = (await Tasks(owner).ReadAsync(owner, source.TaskId, CancellationToken.None)).Revision;
            var service = NewService(db, owner, factory, noteAsr: RealNoteAsr(db, owner));
            var replay = await service.StartFromTaskAsync(request0 with { ExpectedSourceRevision = revision },
                CancellationToken.None);
            Assert.Equal(jobId, replay.Job.Id);
            Assert.Equal(source.TaskId, replay.Task.TaskId);
        }
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Initial_batch_replay_and_unfrozen_target_disagreement_never_resubmit()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var (owner, material, profile) = await SeedRealBatchOwnerAsync(NoteAsrPlan.SampleRate * 60, "replay");
        var (source, routeConfig) = await StartViaRealServiceAsync(owner, material, Guid.NewGuid(), profile);
        var factory = new SyntheticFactory((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post && request.Path == "/v1/files"
                ? UploadOk()
                : request.Method == HttpMethod.Post && request.Path == "/v1/batches"
                    ? BatchOk("batch-replay", total: source.TotalSegments)
                    : throw new InvalidOperationException("unexpected " + request.Path)));
        var target = new NoteAsrBatchTarget(profile.Id, profile.Revision, "consent-replay", true, true);
        StartNoteAsrBatchFromTaskRequest Build(NoteAsrBatchTarget value, Guid configRevision, Guid revision) =>
            new(source.TaskId, source.TaskId, revision, owner,
                new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, configRevision,
                    routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true), value,
                new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Manual, "replay scope"));

        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner, factory, noteAsr: RealNoteAsr(db, owner));
            var modelMismatch = await Assert.ThrowsAsync<DomainException>(() => service.StartFromTaskAsync(
                Build(target with { ModelRevision = Guid.NewGuid() }, routeConfig.Revision, source.Revision),
                CancellationToken.None));
            Assert.Equal(40901, modelMismatch.ErrorCode);
            var routeMismatch = await Assert.ThrowsAsync<DomainException>(() => service.StartFromTaskAsync(
                Build(target, Guid.NewGuid(), source.Revision), CancellationToken.None));
            Assert.Equal(40901, routeMismatch.ErrorCode);
            var consentMismatch = await Assert.ThrowsAsync<DomainException>(() => service.StartFromTaskAsync(
                Build(target with { ModelId = Guid.NewGuid() }, routeConfig.Revision, source.Revision),
                CancellationToken.None));
            Assert.Equal(40901, consentMismatch.ErrorCode);
        }
        Assert.Empty(factory.Requests);
        await using (var db = NewDb(owner))
        {
            Assert.Equal(0, await db.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == owner));
            var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.OwnerId == owner);
            Assert.Equal("Queued", attempt.State);
            Assert.Equal(profile.Revision, attempt.ModelRevision);
            Assert.Equal(profile.Id, attempt.ModelId);
            Assert.Equal(routeConfig.Revision, attempt.ConfigRevision);
        }

        Guid jobId;
        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner, factory, noteAsr: RealNoteAsr(db, owner));
            jobId = (await service.StartFromTaskAsync(
                Build(target, routeConfig.Revision, source.Revision), CancellationToken.None)).Job.Id;
        }
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);

        // Configuration drift and a terminal attempt must not turn an identity replay into a resubmit.
        await AddRouteConfigAsync(owner, profile.Id, "route-consent-drifted");
        await using (var db = NewDb(owner))
        {
            await db.NoteAsrAttempts.Where(x => x.OwnerId == owner)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, "Succeeded"));
        }
        await using (var db = NewDb(owner))
        {
            var revision = (await Tasks(owner).ReadAsync(owner, source.TaskId, CancellationToken.None)).Revision;
            var service = NewService(db, owner, factory, noteAsr: RealNoteAsr(db, owner));
            var replay = await service.StartFromTaskAsync(
                Build(target, routeConfig.Revision, revision), CancellationToken.None);
            Assert.Equal(jobId, replay.Job.Id);
            Assert.Equal(source.TaskId, replay.Task.TaskId);
        }
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Retry_failed_items_creates_a_failed_only_child_and_completes_after_inheritance()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var seed = await SeedAsync();
        var routeConfig = await AddRouteConfigAsync(seed.Owner, seed.ProfileId, "route-consent-1");
        var parentPolls = 0;
        var firstChildPolls = 0;
        var secondChildPolls = 0;
        var creates = 0;
        var factory = new SyntheticFactory((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Path == "/v1/files")
                return Task.FromResult(UploadOk());
            if (request.Method == HttpMethod.Post && request.Path == "/v1/batches")
            {
                return Task.FromResult(Interlocked.Increment(ref creates) switch
                {
                    1 => BatchOk("batch-parent-1", total: 2),
                    2 => BatchOk("batch-child-1", total: 1),
                    _ => BatchOk("batch-child-2", total: 1)
                });
            }
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-parent-1")
                return Task.FromResult(Interlocked.Increment(ref parentPolls) == 1
                    ? BatchOk("batch-parent-1", "in_progress", total: 2, completed: 0, failed: 0)
                    : BatchOk("batch-parent-1", "completed", "file-out", "file-err", 2, 1, 1));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-child-1")
                return Task.FromResult(Interlocked.Increment(ref firstChildPolls) == 1
                    ? BatchOk("batch-child-1", "completed", null, "file-child-1-err", 1, 0, 1)
                    : BatchOk("batch-child-1", "completed", null, "file-child-1-err", 1, 0, 1));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/batches/batch-child-2")
                return Task.FromResult(Interlocked.Increment(ref secondChildPolls) is 1 or 2
                    ? BatchOk("batch-child-2", "completed", "file-child-2-out", null, 1, 1, 0)
                    : BatchOk("batch-child-2", "completed", "file-child-2-out", null, 1, 1, 0));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-out/content")
                return Task.FromResult(FileContent(SuccessJsonl("seg-0")));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-err/content")
                return Task.FromResult(FileContent(ErrorJsonl()));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-child-1-err/content")
                return Task.FromResult(FileContent(ErrorJsonl()));
            if (request.Method == HttpMethod.Get && request.Path == "/v1/files/file-child-2-out/content")
                return Task.FromResult(FileContent(SuccessJsonl("seg-1")));
            throw new InvalidOperationException("unexpected synthetic request " + request.Path);
        });
        Guid firstChildTaskId;
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory, noteAsr: RealNoteAsr(db, seed.Owner));
            var parent = await StartAsync(service, seed);
            await service.PollAsync(parent.Id, CancellationToken.None);
            await service.PollAsync(parent.Id, CancellationToken.None);
            await service.PollAsync(parent.Id, CancellationToken.None);
        }
        await using (var db = NewDb(seed.Owner))
        {
            var parent = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.OwnerId == seed.Owner);
            Assert.Equal("Completed", parent.Status);
            Assert.Equal(1, parent.FailedItems);
            var service = NewService(db, seed.Owner, factory, noteAsr: RealNoteAsr(db, seed.Owner));
            var result = await service.RetryFailedItemsAsync(new RetryFailedNoteAsrBatchRequest(
                Guid.NewGuid(), parent.Id, seed.Owner,
                new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                    routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
                new NoteAsrBatchTarget(seed.ProfileId, seed.ProfileRevision, "consent-1", true, true),
                new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Quality, "retry failed slice v2")),
                CancellationToken.None);
            firstChildTaskId = result.Task.TaskId;
            Assert.Equal(seed.TaskId, result.SourceTaskId);
            Assert.NotEqual(seed.TaskId, firstChildTaskId);
            Assert.Equal(1, result.Job.TotalItems);
            var firstJob = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == result.Job.Id);
            Assert.Equal(new[] { "seg-1" }, JsonSerializer.Deserialize<string[]>(firstJob.ExpectedCustomIdsJson) ?? []);
            var child = await Tasks(seed.Owner).ReadAsync(seed.Owner, firstChildTaskId, CancellationToken.None);
            Assert.Equal(seed.TaskId, child.ParentTaskId);
            Assert.Equal("Succeeded", child.Steps[0].Current.Status);
            Assert.Equal("Queued", child.Steps[1].Current.Status);
            var attempts = await db.NoteAsrAttempts.AsNoTracking()
                .Where(x => x.OwnerId == seed.Owner && x.TaskId == firstChildTaskId).ToListAsync();
            Assert.Single(attempts);
            Assert.NotEqual(seed.AttemptId, attempts[0].Id);
        }
        Assert.Equal(2, factory.UploadCalls);
        Assert.Equal(2, factory.CreateCalls);

        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory, noteAsr: RealNoteAsr(db, seed.Owner));
            var childJob = await db.NoteAsrBatchJobs.AsNoTracking()
                .SingleAsync(x => x.OwnerId == seed.Owner && x.TaskId == firstChildTaskId);
            await service.PollAsync(childJob.Id, CancellationToken.None);
            await service.PollAsync(childJob.Id, CancellationToken.None);
            await service.PollAsync(childJob.Id, CancellationToken.None);
        }
        await using (var db = NewDb(seed.Owner))
        {
            var childJob = await db.NoteAsrBatchJobs.AsNoTracking()
                .SingleAsync(x => x.OwnerId == seed.Owner && x.TaskId == firstChildTaskId);
            Assert.Equal("Completed", childJob.Status);
            Assert.Equal(1, childJob.FailedItems);
            var partialState = await Tasks(seed.Owner).ReadAsync(seed.Owner, firstChildTaskId, CancellationToken.None);
            var partial = NoteAsrTaskStore.Describe(partialState);
            Assert.NotEqual("Succeeded", partial.Phase);
            Assert.Equal("Succeeded", partialState.Steps[0].Current.Status);
            Assert.NotEqual("Succeeded", partialState.Steps[1].Current.Status);
        }

        Guid secondChildTaskId;
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory, noteAsr: RealNoteAsr(db, seed.Owner));
            var partialJob = await db.NoteAsrBatchJobs.AsNoTracking()
                .SingleAsync(x => x.OwnerId == seed.Owner && x.TaskId == firstChildTaskId);
            var result = await service.RetryFailedItemsAsync(new RetryFailedNoteAsrBatchRequest(
                Guid.NewGuid(), partialJob.Id, seed.Owner,
                    new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                        routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
                    new NoteAsrBatchTarget(seed.ProfileId, seed.ProfileRevision, "consent-1", true, true),
                    new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Quality, "retry failed slice v3")),
                CancellationToken.None);
            secondChildTaskId = result.Task.TaskId;
            Assert.Equal(firstChildTaskId, result.SourceTaskId);
            Assert.Equal(1, result.Job.TotalItems);
            var secondJob = await db.NoteAsrBatchJobs.AsNoTracking().SingleAsync(x => x.Id == result.Job.Id);
            Assert.Equal(new[] { "seg-1" }, JsonSerializer.Deserialize<string[]>(secondJob.ExpectedCustomIdsJson) ?? []);
        }
        Assert.Equal(3, factory.UploadCalls);
        Assert.Equal(3, factory.CreateCalls);
        await using (var db = NewDb(seed.Owner))
        {
            var service = NewService(db, seed.Owner, factory, noteAsr: RealNoteAsr(db, seed.Owner));
            var secondJob = await db.NoteAsrBatchJobs.AsNoTracking()
                .SingleAsync(x => x.OwnerId == seed.Owner && x.TaskId == secondChildTaskId);
            await service.PollAsync(secondJob.Id, CancellationToken.None);
            await service.PollAsync(secondJob.Id, CancellationToken.None);
            await service.PollAsync(secondJob.Id, CancellationToken.None);
        }
        await using (var db = NewDb(seed.Owner))
        {
            var secondJob = await db.NoteAsrBatchJobs.AsNoTracking()
                .SingleAsync(x => x.OwnerId == seed.Owner && x.TaskId == secondChildTaskId);
            Assert.Equal("Completed", secondJob.Status);
            Assert.Equal(0, secondJob.FailedItems);
            var childState = await Tasks(seed.Owner).ReadAsync(seed.Owner, secondChildTaskId, CancellationToken.None);
            var child = NoteAsrTaskStore.Describe(childState);
            Assert.Equal("Succeeded", child.Phase);
            Assert.True(childState.CompletionAcknowledged);
            Assert.All(childState.Steps, step => Assert.Equal("Succeeded", step.Current.Status));
            var service = NewService(db, seed.Owner, factory, noteAsr: RealNoteAsr(db, seed.Owner));
            var replay = await Assert.ThrowsAsync<DomainException>(() => service.RetryFailedItemsAsync(
                new RetryFailedNoteAsrBatchRequest(Guid.NewGuid(), secondJob.Id, seed.Owner,
                    new NoteAsrReprocessRoute(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
                        routeConfig.ConsentVersion, NoteAsrExecutionMode.Batch, true, true),
                    new NoteAsrBatchTarget(seed.ProfileId, seed.ProfileRevision, "consent-1", true, true),
                    new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Quality, "should not resend")),
                CancellationToken.None));
            Assert.Equal(40901, replay.ErrorCode);
        }
        Assert.Equal(3, factory.UploadCalls);
        Assert.Equal(3, factory.CreateCalls);
        await CleanupAsync(seed.Owner);
    }

    private static NoteAsrBatchJobEntity NewBatchJobRow(Guid owner, string? batchId, string status,
        string? statusRaw, DateTimeOffset updatedAt) => new()
    {
        OwnerId = owner, TaskId = Guid.NewGuid(), BatchId = batchId, Status = status, StatusRaw = statusRaw,
        Model = "mimo-v2.6-pro", BaseUrl = Origin.AbsoluteUri, CredentialSecret = "synthetic-key",
        ConsentRef = "consent-1", TotalItems = 2, InputFileId = "file-in-1",
        CreatedAt = updatedAt, UpdatedAt = updatedAt
    };

    private static NoteAsrBatchPollJob NewPollJob(NoteAsrBatchService service, PimDbContext db,
        NoteAsrBatchPollOptions settings) =>
        new(service, db, Options.Create(settings), NullLogger<NoteAsrBatchPollJob>.Instance);

    private static AiAsrDestinationConsent Consent(AiModelProfileEntity profile) =>
        new(profile.Id, profile.Revision, "https://api.example.invalid/v1/chat/completions",
            MimoMultimodalPreset.PresetId, "1", true);

    private static async Task<NoteAsrRouteConfigDto> AddRouteConfigAsync(Guid owner, Guid profileId,
        string consentVersion)
    {
        await using var db = NewDb(owner);
        var routing = new NoteAsrRoutingService(db, new FixedUser(owner), Materials(owner),
            new AiAsrPresetRegistry([new MimoMultimodalPreset(_ => new HttpClient())]), Tasks(owner),
            new NoteAsrEvidenceService(db, Tasks(owner), new FixedUser(owner)), new PassthroughSecrets(),
            Options.Create(new NoteAsrRoutingOptions()));
        var current = await db.NoteAsrRouteConfigs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner);
        return await routing.UpdateConfigAsync(new UpdateNoteAsrRouteConfigRequest(current?.Revision ?? Guid.Empty,
            NoteAsrRoute.CloudMultimodal, null, null, profileId, consentVersion), CancellationToken.None);
    }

    private static NoteAsrService RealNoteAsr(PimDbContext db, Guid owner)
    {
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot(owner) });
        var materials = new NoteMaterialUploadStore(options);
        var tasks = new NoteAsrTaskStore(options);
        var user = new FixedUser(owner);
        var presets = new AiAsrPresetRegistry([new MimoMultimodalPreset(_ => new HttpClient())]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DisableHangfire"] = "true"
        }).Build();
        var routing = new NoteAsrRoutingService(db, user, materials, presets, tasks,
            new NoteAsrEvidenceService(db, tasks, user), new PassthroughSecrets(),
            Options.Create(new NoteAsrRoutingOptions()));
        return new NoteAsrService(tasks, materials, db, user, presets, configuration,
            new PassthroughSecrets(), routing);
    }

    private static async Task<(Guid Owner, NoteMaterialVersionDto Version, AiModelProfileEntity Profile)>
        SeedRealBatchOwnerAsync(int samplesPerChunk, string tag)
    {
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        var (material, _) = await SeedMaterialAsync(owner, samplesPerChunk);
        await using var db = NewDb(owner);
        await db.Database.EnsureCreatedAsync();
        await db.NoteAsrBatchJobs.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await db.NoteAsrSegmentEvidence.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await db.NoteAsrAttempts.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        var suffix = owner.ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        await db.Users.AddAsync(new UserEntity
        {
            Id = owner, Username = tag + "-" + suffix, Email = suffix + "@example.invalid",
            PasswordHash = "synthetic", IsActive = true
        });
        var profile = new AiModelProfileEntity
        {
            Id = Guid.NewGuid(), Revision = Guid.NewGuid(), Name = "Batch " + tag + " " + suffix,
            NormalizedName = "batch-" + tag + "-" + suffix, BaseUrl = "https://api.example.invalid/v1/",
            Model = "mimo-v2.6-pro", Enabled = true, Capabilities = AiModelCapabilityFlags.Transcription,
            AsrPresetId = MimoMultimodalPreset.PresetId, AsrBatchBaseUrl = Origin.AbsoluteUri,
            ApiKeySecret = "synthetic-key", CreatedAt = now, UpdatedAt = now
        };
        await db.AiModelProfiles.AddAsync(profile);
        var runtime = await db.AiModelRuntime.SingleOrDefaultAsync(x => x.Id == AiModelRuntimeEntity.SingletonId);
        if (runtime is null)
            await db.AiModelRuntime.AddAsync(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId, Revision = Guid.NewGuid(), Enabled = true,
                TranscriptionModelId = profile.Id
            });
        else
        {
            runtime.Enabled = true;
            runtime.TranscriptionModelId = profile.Id;
            runtime.Revision = Guid.NewGuid();
        }
        await db.SaveChangesAsync();
        return (owner, material, profile);
    }

    private static async Task<(NoteAsrTaskDto Task, NoteAsrRouteConfigDto Route)> StartViaRealServiceAsync(Guid owner,
        NoteMaterialVersionDto version, Guid taskId,
        AiModelProfileEntity profile)
    {
        await using var db = NewDb(owner);
        var service = RealNoteAsr(db, owner);
        var routeConfig = await AddRouteConfigAsync(owner, profile.Id, "route-real-consent");
        var consent = Consent(profile);
        var route = new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal, routeConfig.Revision,
            routeConfig.ConsentVersion, true, NoteAsrExecutionMode.Batch, true);
        return (await service.StartAsync(new StartNoteAsrRequest(taskId, owner,
            version.Upload.VersionId, consent, "zh", route), CancellationToken.None), routeConfig);
    }

    private sealed record Seeded(Guid Owner, Guid ProfileId, Guid ProfileRevision, Guid TaskId,
        Guid AttemptId, Guid VersionId, int ExpectedSegments, string PromptRendered,
        IReadOnlyList<byte[]> AudioSlices);

    private static byte[] JsonlBytes(Seeded seed, params string[] customIds) => Encoding.UTF8.GetBytes(
        string.Join('\n', customIds.Select(id => BatchLine(id, seed))) + "\n");

    private static string BatchLine(string id, Seeded seed, string model = "mimo-v2.6-pro",
        bool includeAudio = true, byte[]? audioOverride = null, string? promptOverride = null,
        string? formatOverride = null, string? dataUrlOverride = null)
    {
        var segment = int.TryParse(id.AsSpan("seg-".Length), out var parsed) && parsed >= 0 &&
                      parsed < seed.AudioSlices.Count ? parsed : 0;
        var audioBytes = audioOverride ?? seed.AudioSlices[segment];
        var audioFormat = formatOverride ?? "wav";
        var dataUrl = dataUrlOverride ?? MimoAudioFormats.DataUrl("wav", audioBytes);
        var audio = includeAudio
            ? $"{{\"type\":\"input_audio\",\"input_audio\":{{\"data\":{JsonSerializer.Serialize(dataUrl)}," +
              $"\"format\":{JsonSerializer.Serialize(audioFormat)}}}}},"
            : "";
        var prompt = promptOverride ?? seed.PromptRendered;
        return $"{{\"custom_id\":{JsonSerializer.Serialize(id)},\"method\":\"POST\"," +
               "\"url\":\"/v1/chat/completions\",\"body\":{\"model\":" + JsonSerializer.Serialize(model) +
               ",\"messages\":[{\"role\":\"user\",\"content\":[" + audio +
               $"{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(prompt)}}}]}}]," +
               "\"max_completion_tokens\":8192,\"stream\":false}}";
    }

    private static async Task<NoteAsrBatchJobDto> StartAsync(NoteAsrBatchService service, Seeded seed,
        byte[]? input = null, long? declaredLength = null, Guid? taskId = null, string consentRef = "consent-1",
        bool batchScopeConfirmed = true, CancellationToken ct = default)
    {
        input ??= JsonlBytes(seed, Enumerable.Range(0, seed.ExpectedSegments).Select(i => $"seg-{i}").ToArray());
        await using var stream = new MemoryStream(input, writable: false);
        return await service.StartAsync(new StartNoteAsrBatchRequest(
            taskId ?? seed.TaskId, seed.AttemptId, seed.ProfileId, consentRef, batchScopeConfirmed), stream,
            declaredLength ?? input.Length, "input.jsonl", ct);
    }

    private static async Task<Seeded> SeedAsync()
    {
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        var material = await SeedMaterialAsync(owner);
        await using var db = NewDb(owner);
        await db.Database.EnsureCreatedAsync();
        // The synthetic PG database is shared by this serial collection only. Remove rows left by
        // earlier failed runs so global background scans and unique provider ids stay deterministic.
        await db.NoteAsrBatchJobs.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await db.NoteAsrSegmentEvidence.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        await db.NoteAsrAttempts.Where(x => x.OwnerId != owner).ExecuteDeleteAsync();
        var now = DateTimeOffset.UtcNow;
        var suffix = owner.ToString("N")[..8];
        await db.Users.AddAsync(new UserEntity
        {
            Id = owner, Username = "synthetic-" + suffix, Email = suffix + "@example.invalid",
            PasswordHash = "synthetic", IsActive = true
        });
        var profile = new AiModelProfileEntity
        {
            Id = Guid.NewGuid(), Revision = Guid.NewGuid(), Name = "Batch " + suffix,
            NormalizedName = "batch-" + suffix, BaseUrl = "https://api.example.invalid/v1/",
            Model = "mimo-v2.6-pro", Enabled = true,
            Capabilities = AiModelCapabilityFlags.Transcription, AsrPresetId = MimoMultimodalPreset.PresetId,
            AsrBatchBaseUrl = Origin.AbsoluteUri, ApiKeySecret = "synthetic-key", CreatedAt = now, UpdatedAt = now
        };
        await db.AiModelProfiles.AddAsync(profile);
        var runtime = await db.AiModelRuntime.SingleOrDefaultAsync(x => x.Id == AiModelRuntimeEntity.SingletonId);
        if (runtime is null)
        {
            runtime = new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId, Revision = Guid.NewGuid(), Enabled = true,
                TranscriptionModelId = profile.Id
            };
            await db.AiModelRuntime.AddAsync(runtime);
        }
        else
        {
            runtime.Enabled = true;
            runtime.TranscriptionModelId = profile.Id;
            runtime.Revision = Guid.NewGuid();
        }

        var taskId = Guid.NewGuid();
        var promptTemplate = AiAsrPromptTemplate.Normalize();
        var prompt = promptTemplate.Snapshot("zh");
        var attempt = new NoteAsrAttemptEntity
        {
            Id = Guid.NewGuid(), OwnerId = owner, TaskId = taskId, NoteId = material.Version.Upload.NoteId,
            VersionId = material.Version.Upload.VersionId,
            InputSha256 = NoteMaterialManifestValidator.Hash(material.Version.InputJson),
            Route = NoteAsrRoute.CloudMultimodal, State = "Queued", ConfigRevision = Guid.NewGuid(),
            ConsentVersion = "consent-1", ModelId = profile.Id, ModelRevision = profile.Revision,
            PresetId = MimoMultimodalPreset.PresetId, PresetVersion = "1", Model = profile.Model,
            Endpoint = "https://api.example.invalid/v1/chat/completions", ExecutionMode = "batch",
            PromptTemplate = promptTemplate.Text, PromptTemplateSha256 = promptTemplate.Sha256,
            PromptTemplateRevision = promptTemplate.Revision, PromptRendered = prompt.Text,
            PromptSha256 = prompt.Sha256, PromptRevision = prompt.Revision,
            FencingToken = 7, ExpectedSegments = 2, Language = "zh"
        };
        await db.NoteAsrAttempts.AddAsync(attempt);
        await db.SaveChangesAsync();
        var consent = new AiAsrDestinationConsent(profile.Id, profile.Revision,
            "https://api.example.invalid/v1/chat/completions", MimoMultimodalPreset.PresetId, "1", true);
        var route = new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal, attempt.ConfigRevision,
            attempt.ConsentVersion, true, NoteAsrExecutionMode.Batch, true);
        var request = new StartNoteAsrRequest(taskId, owner, attempt.VersionId, consent, "zh", route);
        var tasks = Tasks(owner);
        var state = await tasks.CreateAsync(owner, request, material.Version,
            NoteAsrPlan.Build(material.Version, NoteAsrPlan.SamplesPerCall), route, "batch",
            NoteAsrPlan.SamplesPerCall, CancellationToken.None);
        await tasks.UpdateAsync(owner, taskId, current =>
        {
            current.AttemptId = attempt.Id;
            current.ExecutionMode = "batch";
        }, CancellationToken.None);
        var audioSlices = new List<byte[]>();
        foreach (var step in state.Steps)
        {
            audioSlices.Add(await Materials(owner).ReadAudioSliceAsync(owner, attempt.VersionId,
                step.FileIndex, step.SourceOffset, step.Samples, CancellationToken.None));
        }
        return new Seeded(owner, profile.Id, profile.Revision, taskId, attempt.Id, attempt.VersionId,
            attempt.ExpectedSegments, prompt.Text, audioSlices);
    }

    private static async Task<(NoteMaterialVersionDto Version, List<byte[]> Content)> SeedMaterialAsync(
        Guid owner, int samplesPerChunk = 160)
    {
        var versionId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        const string text = "合成 Batch 安全测试材料";
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id = noteId.ToString("D"), title = "Batch 合成材料", revision = 1,
            createdAtMillis = 1,
            blocks = new[] { new { id = Guid.NewGuid().ToString("D"), kind = "markdown", text } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var recordingJson = JsonSerializer.Serialize(new
        {
            id = recordingId.ToString("D"), schemaVersion = 1, createdAtMillis = 1,
            noteId = noteId.ToString("D"), phase = "Completed", currentSegment = 1, problem = (string?)null,
            chunks = new[]
            {
                new { segment = 1, index = 1, samples = samplesPerChunk },
                new { segment = 1, index = 2, samples = samplesPerChunk }
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var noteDoc = JsonDocument.Parse(noteJson);
        using var recordingDoc = JsonDocument.Parse(recordingJson);
        var inputJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id = versionId.ToString("D"), createdAtMillis = 1,
            note = noteDoc.RootElement.Clone(), recordings = new[] { recordingDoc.RootElement.Clone() }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var chunks = new[]
        {
            SyntheticWav(samplesPerChunk, 2_000),
            SyntheticWav(samplesPerChunk, 8_000)
        };
        var content = new List<byte[]>
        {
            Encoding.UTF8.GetBytes(noteJson), Encoding.UTF8.GetBytes(text),
            Encoding.UTF8.GetBytes(recordingJson), chunks[0], chunks[1]
        };
        var paths = new[]
        {
            "note.json", "document.md", $"recordings/{recordingId:D}/manifest.json",
            $"recordings/{recordingId:D}/s0001-c000001.wav", $"recordings/{recordingId:D}/s0001-c000002.wav"
        };
        var kinds = new[] { "Document", "Markdown", "RecordingManifest", "AudioChunk", "AudioChunk" };
        var files = paths.Select((path, index) => new
        {
            path, kind = kinds[index], sha256 = Hash(content[index]), sizeBytes = content[index].Length,
            blockId = (string?)null, recordingId = index >= 2 ? recordingId.ToString("D") : null,
            segment = index >= 3 ? (int?)1 : null, chunkIndex = index >= 3 ? (int?)(index - 2) : null,
            samples = index >= 3 ? (long?)samplesPerChunk : null
        }).ToArray();
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, versionId = versionId.ToString("D"), noteId = noteId.ToString("D"),
            revision = 1, inputSha256 = NoteMaterialManifestValidator.Hash(inputJson), files
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new BeginNoteMaterialUploadRequest(versionId, noteId, Guid.NewGuid(), inputJson,
            manifestJson, true);
        var store = Materials(owner);
        await store.BeginAsync(owner, request);
        for (var file = 0; file < content.Count; file++)
        {
            for (var part = 0; part < (content[file].Length + NoteMaterialUploadOptions.PartSizeBytes - 1) /
                 NoteMaterialUploadOptions.PartSizeBytes; part++)
            {
                var start = part * NoteMaterialUploadOptions.PartSizeBytes;
                var bytes = content[file].AsSpan(start,
                    Math.Min(NoteMaterialUploadOptions.PartSizeBytes, content[file].Length - start)).ToArray();
                await store.UploadPartAsync(owner, versionId, file, part, Hash(bytes), new MemoryStream(bytes));
            }
        }
        await store.CompleteAsync(owner, versionId);
        return (await store.GetAsync(owner, versionId), content);
    }

    private static byte[] SyntheticWav(int samples, short seed)
    {
        var bytes = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)(bytes.Length - 8)));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), checked((uint)(samples * 2)));
        for (var index = 0; index < samples; index++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + index * 2), unchecked((short)(seed + index)));
        return bytes;
    }

    private static async Task<(Guid JobId, MimoBatchIntent Intent)> SeedPendingJobAsync(Seeded seed)
    {
        await using var db = NewDb(seed.Owner);
        var job = new NoteAsrBatchJobEntity
        {
            OwnerId = seed.Owner, TaskId = seed.TaskId, AttemptId = seed.AttemptId,
            Model = "mimo-v2.6-pro", BaseUrl = Origin.AbsoluteUri, ProfileRevision = seed.ProfileRevision,
            CredentialSecret = "synthetic-key", ConsentRef = "consent-1", CompletionWindow = "24h",
            ExpectedCustomIdsJson = JsonSerializer.Serialize(new[] { "seg-0", "seg-1" }),
            TotalItems = seed.ExpectedSegments, InputFileId = "file-in-1", Status = "Creating"
        };
        db.NoteAsrBatchJobs.Add(job);
        await db.SaveChangesAsync();
        return (job.Id, new MimoBatchIntent(seed.Owner, seed.VersionId.ToString("D"),
            seed.TaskId.ToString("D"), 7, job.Id.ToString("D"), seed.ProfileId.ToString("D"),
            seed.ProfileRevision.ToString("D"), Origin.AbsoluteUri, "mimo-v2.6-pro", "24h", "consent-1"));
    }

    private static async Task UpdateProfileAsync(Seeded seed, Action<AiModelProfileEntity> mutate)
    {
        await using var db = NewDb(seed.Owner);
        var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == seed.ProfileId);
        mutate(profile);
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static Task ResetProfileAsync(Seeded seed) => UpdateProfileAsync(seed, profile =>
    {
        profile.Revision = seed.ProfileRevision;
        profile.Enabled = true;
        profile.Capabilities = AiModelCapabilityFlags.Transcription;
        profile.AsrPresetId = MimoMultimodalPreset.PresetId;
        profile.Model = "mimo-v2.6-pro";
        profile.AsrBatchBaseUrl = Origin.AbsoluteUri;
        profile.ApiKeySecret = "synthetic-key";
    });

    private static async Task UpdateRuntimeAsync(bool enabled)
    {
        await using var db = NewDb(Guid.NewGuid());
        var runtime = await db.AiModelRuntime.SingleAsync(x => x.Id == AiModelRuntimeEntity.SingletonId);
        runtime.Enabled = enabled;
        runtime.Revision = Guid.NewGuid();
        await db.SaveChangesAsync();
    }

    private static async Task UpdateAttemptAsync(Seeded seed, Action<NoteAsrAttemptEntity> mutate)
    {
        await using var db = NewDb(seed.Owner);
        var attempt = await db.NoteAsrAttempts.SingleAsync(x => x.Id == seed.AttemptId);
        mutate(attempt);
        attempt.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static Task RestorePromptAsync(Seeded seed)
    {
        var template = AiAsrPromptTemplate.Normalize();
        var prompt = template.Snapshot("zh");
        return UpdateAttemptAsync(seed, attempt =>
        {
            attempt.PromptTemplate = template.Text;
            attempt.PromptTemplateSha256 = template.Sha256;
            attempt.PromptTemplateRevision = template.Revision;
            attempt.PromptRendered = prompt.Text;
            attempt.PromptSha256 = prompt.Sha256;
            attempt.PromptRevision = prompt.Revision;
        });
    }

    private static PimDbContext NewDb(Guid owner) => new(
        new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(ConnectionString!).Options, new FixedUser(owner));

    private static string StorageRoot(Guid owner) =>
        Path.Combine(Path.GetTempPath(), "pim-asr516-batch-tests", owner.ToString("D"));

    private static NoteMaterialUploadStore Materials(Guid owner) => new(Options.Create(
        new NoteMaterialUploadOptions
        {
            StorageRoot = StorageRoot(owner), MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024
        }));

    private static NoteAsrTaskStore Tasks(Guid owner) => new(Options.Create(
        new NoteMaterialUploadOptions { StorageRoot = StorageRoot(owner) }));

    private static NoteAsrBatchService NewService(PimDbContext db, Guid owner,
        IMimoBatchClientFactory factory, IMimoBatchHandleSink? sink = null,
        INoteAsrEvidenceSink? evidence = null, NoteAsrService? noteAsr = null,
        AiAsrPresetRegistry? presets = null, bool allowBatch = true) =>
        new(db, new FixedUser(owner), Tasks(owner), Materials(owner),
            evidence ?? new NoteAsrEvidenceService(db, Tasks(owner), new FixedUser(owner)),
            sink ?? new EfMimoBatchHandleSink(db), factory,
            new PassthroughSecrets(),
            Options.Create(new NoteAsrBatchOptions
            {
                AllowUnverifiedAudioBatch = allowBatch,
                MaxJsonlBytes = checked((int)MimoBatchDefaults.MaxJsonlBytes)
            }), noteAsr, presets ?? new AiAsrPresetRegistry([new MimoMultimodalPreset(_ => new HttpClient())]));

    private static async Task CleanupAsync(Guid owner)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var root = StorageRoot(owner);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var prefix = "Batch " + owner.ToString("N")[..8];
        await using var db = NewDb(owner);
        try
        {
            var profileIds = await db.AiModelProfiles.Where(x => x.Name.StartsWith(prefix))
                .Select(x => x.Id).ToListAsync();
            var runtime = await db.AiModelRuntime.SingleOrDefaultAsync(x => x.Id == AiModelRuntimeEntity.SingletonId);
            if (runtime is not null && (runtime.TextModelId is { } text && profileIds.Contains(text) ||
                                        runtime.VisionModelId is { } vision && profileIds.Contains(vision) ||
                                        runtime.TranscriptionModelId is { } transcription &&
                                        profileIds.Contains(transcription)))
            {
                if (runtime.TextModelId is { } textId && profileIds.Contains(textId)) runtime.TextModelId = null;
                if (runtime.VisionModelId is { } visionId && profileIds.Contains(visionId)) runtime.VisionModelId = null;
                if (runtime.TranscriptionModelId is { } transcriptionId &&
                    profileIds.Contains(transcriptionId)) runtime.TranscriptionModelId = null;
                runtime.Revision = Guid.NewGuid();
                await db.SaveChangesAsync();
            }
            await db.NoteAsrBatchJobs.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.NoteAsrAttempts.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.AiModelProfiles.Where(x => x.Name.StartsWith(prefix)).ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == owner).ExecuteDeleteAsync();
        }
        catch (Exception error) when (error is Npgsql.PostgresException { SqlState: "42P01" }) { }
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Role => "user";
    }

    private sealed class PassthroughSecrets : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed record SyntheticRequest(HttpMethod Method, string Path, string ApiKey);

    private sealed class SyntheticFactory(
        Func<SyntheticRequest, CancellationToken, Task<HttpResponseMessage>> responder) : IMimoBatchClientFactory
    {
        private readonly object _gate = new();
        private readonly Func<SyntheticRequest, CancellationToken, Task<HttpResponseMessage>> _responder = responder;
        private readonly List<SyntheticRequest> _requests = [];
        private readonly List<Uri> _origins = [];
        private readonly List<string> _keys = [];

        public TimeSpan CreateTimeout { get; init; } = TimeSpan.FromSeconds(2);
        public IReadOnlyList<SyntheticRequest> Requests
        {
            get { lock (_gate) return _requests.ToArray(); }
        }
        public IReadOnlyList<Uri> Origins
        {
            get { lock (_gate) return _origins.ToArray(); }
        }
        public IReadOnlyList<string> ApiKeys
        {
            get { lock (_gate) return _keys.ToArray(); }
        }
        public int UploadCalls => Requests.Count(x =>
            x.Method == HttpMethod.Post && x.Path.EndsWith("/v1/files", StringComparison.Ordinal));
        public int CreateCalls => Requests.Count(x =>
            x.Method == HttpMethod.Post && x.Path.EndsWith("/v1/batches", StringComparison.Ordinal));
        public int ContentCalls => Requests.Count(x => x.Path.EndsWith("/content", StringComparison.Ordinal));

        public IMimoBatchClientLease Create(Uri baseAddress, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key required", nameof(apiKey));
            lock (_gate)
            {
                _origins.Add(baseAddress);
                _keys.Add(apiKey);
            }
            var http = new HttpClient(new Handler(this), disposeHandler: true)
            {
                BaseAddress = baseAddress,
                Timeout = Timeout.InfiniteTimeSpan
            };
            return new Lease(http, new MimoBatchClient(http,
                new MimoBatchClientOptions(baseAddress, CreateTimeout, AudioBatchEnabled: true)));
        }

        private sealed class Lease(HttpClient http, MimoBatchClient client) : IMimoBatchClientLease
        {
            public HttpClient Http { get; } = http;
            public MimoBatchClient Client { get; } = client;
            public void Dispose() => Http.Dispose();
        }

        private sealed class Handler(SyntheticFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var synthetic = new SyntheticRequest(request.Method,
                    request.RequestUri?.AbsolutePath ?? "",
                    request.Headers.Authorization?.Parameter ?? "");
                lock (owner._gate) owner._requests.Add(synthetic);
                return await owner._responder(synthetic, cancellationToken);
            }
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object? body = null) =>
        new(status)
        {
            Content = new StringContent(body is null ? "" : JsonSerializer.Serialize(body),
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage UploadOk() => JsonResponse(HttpStatusCode.OK, new
    {
        id = "file-in-1", purpose = "batch", filename = "input.jsonl", expire_at = 1780000000L
    });

    private static HttpResponseMessage BatchOk(string id, string status = "validating",
        string? outputFileId = null, string? errorFileId = null, int total = 2, int completed = 0,
        int failed = 0) => JsonResponse(HttpStatusCode.OK, new
    {
        id, status, expires_at = 1780009000L, output_file_id = outputFileId, error_file_id = errorFileId,
        request_counts = new { total, completed, failed }
    });

    private static HttpResponseMessage FileContent(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/jsonl")
    };

    private static string OutputJsonl() =>
        "{\"id\":\"r1\",\"custom_id\":\"seg-0\",\"response\":{\"status_code\":200,\"body\":{\"choices\":[{\"message\":{\"content\":\"0-1 | A: hello\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12,\"prompt_tokens_details\":{\"audio_tokens\":5}}}},\"error\":null}\n";

    private static string SuccessJsonl(params string[] customIds) => string.Join('\n',
        customIds.Select((id, index) =>
            $"{{\"id\":\"success-{index}\",\"custom_id\":{JsonSerializer.Serialize(id)},\"response\":" +
            "{\"status_code\":200,\"body\":{\"choices\":[{\"message\":{\"content\":\"0-1 | A: synthetic\"}," +
            "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2," +
            "\"total_tokens\":12,\"prompt_tokens_details\":{\"audio_tokens\":5}}}},\"error\":null}")) + "\n";

    private static string ErrorJsonl() =>
        "{\"id\":\"r2\",\"custom_id\":\"seg-1\",\"response\":null,\"error\":{\"code\":\"inference_failed\",\"message\":\"synthetic\"}}\n";

    private static string AllErrorsJsonl() =>
        "{\"id\":\"r1\",\"custom_id\":\"seg-0\",\"response\":null,\"error\":{\"code\":\"inference_failed\",\"message\":\"synthetic\"}}\n" +
        "{\"id\":\"r2\",\"custom_id\":\"seg-1\",\"response\":null,\"error\":{\"code\":\"inference_failed\",\"message\":\"synthetic\"}}\n";
}

[CollectionDefinition("NoteAsrBatchSyntheticPg", DisableParallelization = true)]
public sealed class NoteAsrBatchSyntheticPgCollection;
