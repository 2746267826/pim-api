using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteProcessingFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pim-note-processing", Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<Program> Factory { get; }
    public SyntheticModelHandler Models { get; } = new();
    public string NativeNotesRoot => Path.Combine(_root, "materials");

    public NoteProcessingFixture() =>
        Factory = AuthRegistrationTestHost.Create(
                AuthRegistrationTestHost.NewDbName("note-processing"))
            .WithWebHostBuilder(builder => builder.UseEnvironment("Test")
                .UseSetting("NativeNotes:StorageRoot", Path.Combine(_root, "materials"))
                .UseSetting("ManagedStorage:Root", Path.Combine(_root, "managed"))
                .UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"))
                .ConfigureServices(services => services
                    .AddHttpClient(AiProfileChatClientFactory.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Models)));

    public void Dispose()
    {
        Factory.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public sealed class SyntheticModelHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Reset() => Interlocked.Exchange(ref _calls, 0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = "synthetic-note-processing-" + call,
                    @object = "chat.completion",
                    created = 1780000000,
                    model = "synthetic-text",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            finish_reason = "stop",
                            message = new
                            {
                                role = "assistant",
                                content = "## 合成整理\n\n来自固定材料与合成 ASR 的完整结果。"
                            }
                        }
                    },
                    usage = new
                    {
                        prompt_tokens = 10,
                        completion_tokens = 5,
                        total_tokens = 15
                    }
                }), Encoding.UTF8, "application/json")
            };
        }
    }
}

public sealed class NoteProcessingTests(NoteProcessingFixture host)
    : IClassFixture<NoteProcessingFixture>, IDisposable
{
    private const string Processing = "/api/v1/native-notes/processing";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AutoOffCompletionStaysPending_ThenManualStartCreatesTwoStableTasksIdempotently()
    {
        var owner = await OwnerAsync("manual-off");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        var pending = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingState.PendingProcessing, pending.State);
        Assert.False(pending.AutoStarted);
        Assert.True(pending.ManualStartAvailable);
        Assert.Contains("未开启", pending.PendingReason);
        Assert.False(pending.Asr.TaskCreated);
        Assert.False(pending.Derived.TaskCreated);
        Assert.Equal(0, TaskCounts(owner).Asr);
        Assert.Equal(0, TaskCounts(owner).Derived);

        var first = await Data<NoteProcessingStatusDto>(await client.PostAsJsonAsync(
            Processing + "/start", new StartNoteProcessingRequest(material.NoteId,
                material.VersionId)));
        Assert.Equal(NoteProcessingState.Processing, first.State);
        Assert.True(first.ManualStartAvailable == false);
        Assert.True(first.Asr.TaskCreated);
        Assert.True(first.Derived.TaskCreated);
        Assert.Equal(NoteProcessingIdentity.Task(owner.UserId, material.VersionId,
            NoteProcessingTaskKind.Asr), first.Asr.TaskId);
        Assert.Equal(NoteProcessingIdentity.Task(owner.UserId, material.VersionId,
            NoteProcessingTaskKind.Derived), first.Derived.TaskId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));

        var replay = await Data<NoteProcessingStatusDto>(await client.PostAsJsonAsync(
            Processing + "/start", new StartNoteProcessingRequest(material.NoteId,
                material.VersionId)));
        Assert.Equal(first.Asr.TaskId, replay.Asr.TaskId);
        Assert.Equal(first.Derived.TaskId, replay.Derived.TaskId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task AutoEnabledCompletionCreatesBothChildrenOnce_WithoutCallingProviders()
    {
        var owner = await OwnerAsync("auto-on");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(started.AutoStarted);
        Assert.Equal(NoteProcessingState.Processing, started.State);
        Assert.True(started.Asr.TaskCreated);
        Assert.True(started.Derived.TaskCreated);
        Assert.Equal(NoteProcessingChainState.WaitingForAsr, started.Derived.State);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        var asrBefore = await AsrTaskAsync(owner.UserId, started.Asr.TaskId!.Value);
        var derivedBefore = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId!.Value);

        _ = await CompleteMaterialAsync(owner, client, withAudio: true, material);
        var replay = await StatusAsync(client, material.NoteId, material.VersionId);
        var asrAfter = await AsrTaskAsync(owner.UserId, replay.Asr.TaskId!.Value);
        var derivedAfter = await DerivedTaskAsync(owner.UserId, replay.Derived.TaskId!.Value);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(started.Asr.TaskId, replay.Asr.TaskId);
        Assert.Equal(started.Derived.TaskId, replay.Derived.TaskId);
        Assert.Equal(asrBefore.Revision, asrAfter.Revision);
        Assert.Equal(derivedBefore.Revision, derivedAfter.Revision);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task ZeroAudioMaterialCreatesOnlyDerivedTask_AndAsrIsNotApplicable()
    {
        var owner = await OwnerAsync("no-audio");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        var material = await CompleteMaterialAsync(owner, client, withAudio: false);
        var status = await StatusAsync(client, material.NoteId, material.VersionId);

        Assert.Equal(NoteProcessingState.Processing, status.State);
        Assert.Equal(NoteProcessingChainState.NotApplicable, status.Asr.State);
        Assert.Null(status.Asr.TaskId);
        Assert.False(status.Asr.TaskCreated);
        Assert.True(status.Derived.TaskCreated);
        Assert.Equal(NoteProcessingChainState.Queued, status.Derived.State);
        Assert.Equal((Asr: 0, Derived: 1), TaskCounts(owner));
    }

    [Fact]
    public async Task MissingDefaultTargetsStayPendingWithoutTaskOrProviderCall()
    {
        var owner = await OwnerAsync("missing-target");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "missing-target-admin-" + Guid.NewGuid().ToString("N"), "admin");
        using var adminClient = AuthRegistrationTestHost.Authed(host.Factory, admin.AccessToken);
        var settings = await Data<AiModelSettingsDto>(
            await adminClient.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await adminClient.PutAsJsonAsync(
            "/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, true, null, null, null)));
        _ = await SetAutoProcessingAsync(client, true);

        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var status = await StatusAsync(client, material.NoteId, material.VersionId);

        Assert.Equal(NoteProcessingState.PendingProcessing, status.State);
        Assert.True(status.AutoProcessingEnabled);
        Assert.False(status.AutoStarted);
        Assert.False(status.Asr.TaskCreated);
        Assert.False(status.Derived.TaskCreated);
        Assert.Contains("默认语音转写模型未配置", status.PendingReason);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task StatusAndStartAreOwnerScoped_EvenWithKnownProcessingIdentity()
    {
        var owner = await OwnerAsync("owner-scope");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        var other = await OwnerAsync("other-scope");
        using var otherClient = AuthRegistrationTestHost.Authed(host.Factory, other.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(
            $"{Processing}?noteId={material.NoteId}&versionId={material.VersionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsJsonAsync(
            Processing + "/start",
            new StartNoteProcessingRequest(material.NoteId, material.VersionId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(
            $"{Processing}/{status.ProcessingId}")).StatusCode);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(other));
    }

    [Fact]
    public async Task DerivedWaitsForAsr_ThenMaterializesTranscriptsOnceBeforeCompleting()
    {
        var owner = await OwnerAsync("continuation");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        var derivedId = status.Derived.TaskId!.Value;
        var derivedJob = host.Factory.Services.GetRequiredService<NoteDerivedJob>();

        Assert.Equal(NoteProcessingChainState.WaitingForAsr, status.Derived.State);
        Assert.Equal(NoteProcessingState.Processing, status.State);
        var pending = await DerivedTaskAsync(owner.UserId, derivedId);
        Assert.True(pending.AwaitingAsr);
        Assert.Equal("WaitingForAsr", NoteDerivedStore.DescribePhase(pending));
        Assert.Empty(pending.Plan.Input.Transcripts);
        await derivedJob.RunAsync(owner.UserId, derivedId, CancellationToken.None);
        var stillPending = await DerivedTaskAsync(owner.UserId, derivedId);
        Assert.True(stillPending.AwaitingAsr);
        Assert.Empty(stillPending.Plan.Input.Transcripts);
        Assert.Equal(0, host.Models.Calls);

        await MarkAsrSucceededWithSyntheticReceiptsAsync(owner.UserId, status.Asr.TaskId!.Value,
            material);
        await derivedJob.RunAsync(owner.UserId, derivedId, CancellationToken.None);

        var continued = await DerivedTaskAsync(owner.UserId, derivedId);
        Assert.False(continued.AwaitingAsr);
        Assert.Equal("Succeeded", NoteDerivedStore.DescribePhase(continued));
        var transcript = Assert.Single(continued.Plan.Input.Transcripts);
        Assert.False(string.IsNullOrWhiteSpace(transcript.Text));
        Assert.True(host.Models.Calls >= 1);
        var completed = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingChainState.Succeeded, completed.Asr.State);
        Assert.Equal(NoteProcessingChainState.Succeeded, completed.Derived.State);
        Assert.Equal(NoteProcessingState.Completed, completed.State);
    }

    [Fact]
    public async Task DerivedBlocksWhenAsrFailsBeforeMaterialization_WithoutEmptyTranscriptSuccess()
    {
        var owner = await OwnerAsync("continuation-failed");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        var asr = await AsrTaskAsync(owner.UserId, started.Asr.TaskId!.Value);
        _ = await AsrStore().UpdateAsync(owner.UserId, asr.Request.TaskId, state =>
        {
            state.Steps[0].Current.Status = "Failed";
            state.Steps[0].Current.ErrorMessage = "合成 ASR 失败";
        }, CancellationToken.None);

        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(
            owner.UserId, started.Derived.TaskId!.Value, CancellationToken.None);

        var task = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        Assert.False(task.AwaitingAsr);
        Assert.True(task.Blocked);
        Assert.Empty(task.Plan.Input.Transcripts);
        Assert.Contains(task.Issues, issue => issue.Code == "asr_unavailable");
        Assert.Equal("Blocked", NoteDerivedStore.DescribePhase(task));
        Assert.Equal(0, host.Models.Calls);
        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingChainState.NeedsAttention, status.Asr.State);
        Assert.Equal(NoteProcessingChainState.NeedsAttention, status.Derived.State);
        Assert.Equal(NoteProcessingState.NeedsAttention, status.State);
    }

    [Fact]
    public async Task CompletedProcessingRecordIsNotRewrittenByCompensationScan()
    {
        var owner = await OwnerAsync("dispatch-complete");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        var derivedJob = host.Factory.Services.GetRequiredService<NoteDerivedJob>();
        await MarkAsrSucceededWithSyntheticReceiptsAsync(owner.UserId, started.Asr.TaskId!.Value,
            material);
        await derivedJob.RunAsync(owner.UserId, started.Derived.TaskId!.Value,
            CancellationToken.None);
        var completed = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingState.Completed, completed.State);

        var store = host.Factory.Services.GetRequiredService<NoteProcessingStore>();
        var before = await store.ReadAsync(owner.UserId, started.ProcessingId, default);
        var asrBefore = await AsrTaskAsync(owner.UserId, started.Asr.TaskId.Value);
        var derivedBefore = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        var callsBefore = host.Models.Calls;
        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();

        await dispatch.RunAsync(default);
        await dispatch.RunAsync(default);

        var after = await store.ReadAsync(owner.UserId, started.ProcessingId, default);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.StartedAt, after.StartedAt);
        Assert.Equal(asrBefore.Revision,
            (await AsrTaskAsync(owner.UserId, started.Asr.TaskId.Value)).Revision);
        Assert.Equal(derivedBefore.Revision,
            (await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value)).Revision);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(callsBefore, host.Models.Calls);
        Assert.Equal(NoteProcessingState.Completed,
            (await StatusAsync(client, material.NoteId, material.VersionId)).State);
    }

    [Fact]
    public async Task CompensationScanStartsMissingIntentOnce_WithoutRewritingStartedRecord()
    {
        var owner = await OwnerAsync("dispatch-recovery");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        var material = NoteDerivedFixture.CreateMaterial(withAudio: false);
        await NoteDerivedFixture.UploadAsync(
            host.Factory.Services.GetRequiredService<NoteMaterialUploadStore>(),
            owner.UserId, material);
        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();

        await dispatch.RunAsync(default);

        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(status.Derived.TaskCreated);
        Assert.Equal((Asr: 0, Derived: 1), TaskCounts(owner));
        var store = host.Factory.Services.GetRequiredService<NoteProcessingStore>();
        var before = await store.ReadAsync(owner.UserId, status.ProcessingId, default);

        await dispatch.RunAsync(default);

        var after = await store.ReadAsync(owner.UserId, status.ProcessingId, default);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal((Asr: 0, Derived: 1), TaskCounts(owner));
        Assert.Equal(NoteProcessingChainState.Queued,
            (await StatusAsync(client, material.NoteId, material.VersionId)).Derived.State);
    }

    [Fact]
    public void ProcessingStatusRouteHasNoTrailingSlash()
    {
        const string expected = "/api/v1/native-notes/processing";
        var routes = host.Factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true &&
                endpoint.RoutePattern.RawText is { } raw && !raw.Contains('{') &&
                "/" + raw.Trim('/') == expected)
            .ToArray();

        var route = Assert.Single(routes);
        Assert.False(route.RoutePattern.RawText!.EndsWith('/'),
            $"processing status route still ends with a slash: {route.RoutePattern.RawText}");
    }

    [Fact]
    public async Task StoppedWaitingDerivedIsVisibleAndHighLevelStartReplayDoesNotAllocateAgain()
    {
        var owner = await OwnerAsync("stop-visible");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        _ = await Data<NoteDerivedTaskDto>(await client.PostAsync(
            $"/api/v1/native-notes/derived-tasks/{started.Derived.TaskId}/stop", null));

        var stopped = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingChainState.Stopped, stopped.Derived.State);
        Assert.Equal(NoteProcessingState.NeedsAttention, stopped.State);
        var replay = await Data<NoteProcessingStatusDto>(await client.PostAsJsonAsync(
            Processing + "/start", new StartNoteProcessingRequest(material.NoteId,
                material.VersionId)));
        Assert.Equal(started.Derived.TaskId, replay.Derived.TaskId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
    }

    [Fact]
    public async Task FileCommittedAsrIntentWithoutAttemptIsRecoveredByCompensationScanAndRemainsStable()
    {
        var owner = await OwnerAsync("asr-attempt-recovery");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrStore = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var request = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, CancellationToken.None);
        Assert.NotNull(request.Route);
        _ = await asrStore.CreateAsync(owner.UserId, request, version,
            NoteAsrPlan.Build(version), request.Route, request.Route.ExecutionMode,
            NoteAsrPlan.SamplesPerCall, CancellationToken.None);
        Assert.Equal(0, await AttemptCountAsync(owner.UserId, request.TaskId));
        var wrongVersion = await Assert.ThrowsAsync<DomainException>(() =>
            asrService.EnsureAttemptForOwnerAsync(owner.UserId, request.TaskId,
                Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(40901, wrongVersion.ErrorCode);
        Assert.Equal(0, await AttemptCountAsync(owner.UserId, request.TaskId));
        _ = await SetAutoProcessingAsync(client, true);

        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();
        await dispatch.RunAsync(CancellationToken.None);

        var first = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(first.Asr.TaskCreated);
        Assert.True(first.Derived.TaskCreated);
        Assert.Equal(NoteProcessingChainState.WaitingForAsr, first.Derived.State);
        var asr = await AsrTaskAsync(owner.UserId, first.Asr.TaskId!.Value);
        Assert.NotNull(asr.AttemptId);
        Assert.Equal(1, await AttemptCountAsync(owner.UserId, asr.Request.TaskId));
        var boundAttempt = asr.AttemptId;
        var revision = asr.Revision;

        var jobs = new RecordingJobs();
        var asrDispatch = new NoteAsrDispatchJob(AsrStore(), asrService, jobs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NoteAsrDispatchJob>.Instance);
        await asrDispatch.RunAsync(CancellationToken.None);
        var job = Assert.Single(jobs.Items.Where(item =>
            item.Args[0].Equals(owner.UserId) && item.Args[1].Equals(asr.Request.TaskId)));
        Assert.Equal(typeof(NoteAsrJob), job.Type);

        await dispatch.RunAsync(CancellationToken.None);
        var second = await StatusAsync(client, material.NoteId, material.VersionId);
        var replay = await AsrTaskAsync(owner.UserId, first.Asr.TaskId.Value);
        Assert.Equal(first.Asr.TaskId, second.Asr.TaskId);
        Assert.Equal(first.Derived.TaskId, second.Derived.TaskId);
        Assert.Equal(boundAttempt, replay.AttemptId);
        Assert.Equal(revision, replay.Revision);
        Assert.Equal(1, await AttemptCountAsync(owner.UserId, replay.Request.TaskId));
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
    }

    [Fact]
    public async Task MissingBoundAttemptIsBlockedInsteadOfReboundToANewSnapshot()
    {
        var owner = await OwnerAsync("asr-bound-attempt-missing");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrStore = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var request = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, CancellationToken.None);
        var created = await asrStore.CreateAsync(owner.UserId, request, version,
            NoteAsrPlan.Build(version), request.Route, request.Route?.ExecutionMode,
            NoteAsrPlan.SamplesPerCall, CancellationToken.None);
        var missingAttempt = Guid.NewGuid();
        await asrStore.UpdateAsync(owner.UserId, request.TaskId, state =>
        {
            state.AttemptId = missingAttempt;
        }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            asrService.EnsureAttemptForOwnerAsync(owner.UserId, request.TaskId,
                request.VersionId, CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        var blocked = await asrStore.ReadAsync(owner.UserId, request.TaskId,
            CancellationToken.None);
        Assert.Equal(missingAttempt, blocked.AttemptId);
        Assert.All(blocked.Steps, step => Assert.Equal("Blocked", step.Current.Status));
        Assert.Equal(0, await AttemptCountAsync(owner.UserId, request.TaskId));
        Assert.Equal(created.Steps.Count, blocked.Steps.Count);
    }

    [Fact]
    public async Task LegacyDerivedPayloadWithoutAwaitingAsrMigratesToWaitingWithoutEmptyTranscriptSuccess()
    {
        var owner = await OwnerAsync("legacy-derived-wait");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingChainState.WaitingForAsr, started.Derived.State);

        await RemoveAwaitingAsrPropertyAsync(owner.UserId, started.Derived.TaskId!.Value);

        var migrated = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingChainState.WaitingForAsr, migrated.Derived.State);
        var task = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        Assert.True(task.AwaitingAsr);
        Assert.Empty(task.Plan.Input.Transcripts);

        var derivedJob = host.Factory.Services.GetRequiredService<NoteDerivedJob>();
        await derivedJob.RunAsync(owner.UserId, started.Derived.TaskId.Value,
            CancellationToken.None);
        await derivedJob.RunAsync(owner.UserId, started.Derived.TaskId.Value,
            CancellationToken.None);
        var stillWaiting = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        Assert.True(stillWaiting.AwaitingAsr);
        Assert.NotEqual("Succeeded", NoteDerivedStore.DescribePhase(stillWaiting));
        Assert.Empty(stillWaiting.Plan.Input.Transcripts);
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task AsrStoppedBeforeDerivedMaterializationBlocksWithoutEmptyTranscriptSuccess()
    {
        var owner = await OwnerAsync("asr-stopped-block");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingChainState.WaitingForAsr, started.Derived.State);
        _ = await AsrStore().UpdateAsync(owner.UserId, started.Asr.TaskId!.Value, state =>
        {
            state.StopRequested = true;
            foreach (var step in state.Steps)
            {
                step.Current.Status = "Cancelled";
                step.Current.ErrorMessage = "用户已停止 ASR";
            }
        }, CancellationToken.None);

        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(
            owner.UserId, started.Derived.TaskId!.Value, CancellationToken.None);

        var task = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        Assert.False(task.AwaitingAsr);
        Assert.True(task.Blocked);
        Assert.NotEqual("Succeeded", NoteDerivedStore.DescribePhase(task));
        Assert.Empty(task.Plan.Input.Transcripts);
        Assert.Contains(task.Issues, issue => issue.Code == "asr_unavailable");
        Assert.DoesNotContain(task.Issues, issue =>
            issue.Message.Contains("继续使用固定原始材料", StringComparison.Ordinal));
        Assert.Contains(task.Issues, issue =>
            issue.Message.Contains("不会以空转写继续", StringComparison.Ordinal));
        Assert.Equal(0, host.Models.Calls);
        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingChainState.NeedsAttention, status.Derived.State);
        Assert.Equal(NoteProcessingState.NeedsAttention, status.State);
    }

    [Fact]
    public async Task AsrSucceededWithoutReceiptsBlocksInsteadOfUsingEmptyTranscripts()
    {
        var owner = await OwnerAsync("asr-missing-receipt");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        _ = await AsrStore().UpdateAsync(owner.UserId, started.Asr.TaskId!.Value, state =>
        {
            foreach (var step in state.Steps)
            {
                step.Current.Status = "Succeeded";
                step.Current.LogId = null;
                step.Current.ErrorMessage = null;
            }
            state.CompletionAcknowledged = true;
        }, CancellationToken.None);

        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(
            owner.UserId, started.Derived.TaskId!.Value, CancellationToken.None);

        var task = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        Assert.False(task.AwaitingAsr);
        Assert.True(task.Blocked);
        Assert.NotEqual("Succeeded", NoteDerivedStore.DescribePhase(task));
        Assert.Empty(task.Plan.Input.Transcripts);
        Assert.Contains(task.Issues, issue => issue.Code == "asr_segment_incomplete");
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
    }

    [Fact]
    public async Task AsrSourceFromAnotherVersionBlocksInsteadOfUsingEmptyTranscripts()
    {
        var owner = await OwnerAsync("asr-wrong-source");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        _ = await AsrStore().UpdateAsync(owner.UserId, started.Asr.TaskId!.Value, state =>
        {
            state.Request = state.Request with { VersionId = Guid.NewGuid() };
        }, CancellationToken.None);

        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(
            owner.UserId, started.Derived.TaskId!.Value, CancellationToken.None);

        var task = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        Assert.False(task.AwaitingAsr);
        Assert.True(task.Blocked);
        Assert.NotEqual("Succeeded", NoteDerivedStore.DescribePhase(task));
        Assert.Empty(task.Plan.Input.Transcripts);
        Assert.Contains(task.Issues, issue => issue.Code == "asr_source_mismatch");
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
    }

    [Fact]
    public async Task UserProcessingSettingUsesRevisionCas_AndStatusErrorsRedactPrivateUrls()
    {
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "revision-admin-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, admin.AccessToken);
        var preferences = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        var saved = await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(preferences.Revision, true)));
        Assert.True(saved.AutoProcessingEnabled);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(preferences.Revision, false)))
            .StatusCode);
        Assert.True((await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"))).AutoProcessingEnabled);

        _ = await ConfigureAsync(client, auto: true);
        var material = await CompleteMaterialAsync(admin, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        var asr = await AsrTaskAsync(admin.UserId, started.Asr.TaskId!.Value);
        await AsrStore().UpdateAsync(admin.UserId, asr.Request.TaskId, state =>
        {
            state.Steps[0].Current.Status = "Failed";
            state.Steps[0].Current.ErrorMessage =
                "provider https://private.example.invalid/v1 failed with Bearer private-token-123456";
        }, CancellationToken.None);
        var failed = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingState.NeedsAttention, failed.State);
        Assert.Contains(failed.Asr.Errors, error => error.Contains("[地址已隐藏]", StringComparison.Ordinal));
        Assert.All(failed.Asr.Errors,
            error => Assert.DoesNotContain("private.example.invalid", error, StringComparison.Ordinal));
        Assert.All(failed.Asr.Errors,
            error => Assert.DoesNotContain("private-token", error, StringComparison.Ordinal));
    }

    private async Task<AuthRegistrationTestHost.Session> OwnerAsync(string prefix) =>
        await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            $"{prefix}-{Guid.NewGuid():N}", "admin");

    private async Task<(AiModelDto Asr, AiModelDto Derived)> ConfigureAsync(
        HttpClient client, bool auto)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var asr = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("ASR " + suffix, "http://asr.invalid/v1/", "mimo-v2.5-asr",
                true, [AiModelCapability.Transcription], 30, 1024, "synthetic-asr-key",
                AsrPresetId: "mimo-asr")));
        var derived = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("整理 " + suffix, "http://derived.invalid/v1/",
                "synthetic-text", true, [AiModelCapability.Text, AiModelCapability.Vision],
                30, 4096, "synthetic-derived-key")));
        var settings = await Data<AiModelSettingsDto>(
            await client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
            "/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, true, derived.Id, derived.Id,
                asr.Id)));
        _ = await SetAutoProcessingAsync(client, auto);
        return (asr, derived);
    }

    private static async Task<NoteAsrRouteConfigDto> ConfigureCloudRouteAsync(
        HttpClient client, Guid asrModelId)
    {
        var current = await Data<NoteAsrRouteConfigDto>(
            await client.GetAsync("/api/v1/native-notes/asr-routing/config"));
        return await Data<NoteAsrRouteConfigDto>(await client.PutAsJsonAsync(
            "/api/v1/native-notes/asr-routing/config",
            new UpdateNoteAsrRouteConfigRequest(current.Revision, NoteAsrRoute.CloudAsr,
                null, null, asrModelId, "synthetic-cloud-consent-v1")));
    }

    private static async Task<NoteProcessingPreferencesDto> SetAutoProcessingAsync(
        HttpClient client, bool enabled)
    {
        var current = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        return await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(current.Revision, enabled)));
    }

    private async Task<NoteDerivedFixture.Material> CompleteMaterialAsync(
        AuthRegistrationTestHost.Session owner, HttpClient client, bool withAudio,
        NoteDerivedFixture.Material? existing = null)
    {
        var material = existing ?? NoteDerivedFixture.CreateMaterial(withAudio: withAudio);
        if (existing is null)
            await NoteDerivedFixture.UploadAsync(
                host.Factory.Services.GetRequiredService<NoteMaterialUploadStore>(),
                owner.UserId, material);
        var response = await client.PostAsync(
            $"/api/v1/native-notes/versions/{material.VersionId}/complete", null);
        var completed = await Data<NoteMaterialUploadDto>(response);
        Assert.Equal("Uploaded", completed.Phase);
        return material;
    }

    private static async Task<NoteProcessingStatusDto> StatusAsync(HttpClient client,
        Guid noteId, Guid versionId)
    {
        var response = await client.GetAsync(
            $"{Processing}?noteId={noteId}&versionId={versionId}");
        return await Data<NoteProcessingStatusDto>(response);
    }

    private (int Asr, int Derived) TaskCounts(AuthRegistrationTestHost.Session owner) =>
        (AsrStore().Enumerate(owner.UserId).Count(x => x.Owner == owner.UserId),
            DerivedStore().EnumerateTasks(owner.UserId).Count());

    private NoteAsrTaskStore AsrStore() =>
        host.Factory.Services.GetRequiredService<NoteAsrTaskStore>();

    private NoteDerivedStore DerivedStore() =>
        host.Factory.Services.GetRequiredService<NoteDerivedStore>();

    private Task<NoteAsrState> AsrTaskAsync(Guid owner, Guid id) =>
        AsrStore().ReadAsync(owner, id, CancellationToken.None);

    private Task<NoteDerivedTaskState> DerivedTaskAsync(Guid owner, Guid id) =>
        DerivedStore().ReadTaskAsync(owner, id, CancellationToken.None);

    private async Task<int> AiCallCountAsync(Guid owner)
    {
        using var scope = host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PimDbContext>()
            .AiRequestLogs.AsNoTracking().CountAsync(x => x.UserId == owner);
    }

    private async Task<int> AttemptCountAsync(Guid owner, Guid taskId)
    {
        using var scope = host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PimDbContext>()
            .NoteAsrAttempts.AsNoTracking().CountAsync(x =>
                x.OwnerId == owner && x.TaskId == taskId);
    }

    private async Task RemoveAwaitingAsrPropertyAsync(Guid owner, Guid taskId)
    {
        var path = Path.Combine(host.NativeNotesRoot, "derived", owner.ToString("D"),
            "tasks", taskId.ToString("D") + ".json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(root.Remove("awaitingAsr"));
        await File.WriteAllTextAsync(path, root.ToJsonString(Json));
    }

    private async Task MarkAsrSucceededWithSyntheticReceiptsAsync(Guid owner,
        Guid asrTaskId, NoteDerivedFixture.Material material)
    {
        var asr = await AsrTaskAsync(owner, asrTaskId);
        var consent = asr.Request.Consent
            ?? throw new InvalidOperationException("Synthetic ASR test requires cloud consent");
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var materials = host.Factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        var logs = new Dictionary<int, AiRequestLogEntity>();
        foreach (var step in asr.Steps)
        {
            var bytes = await materials.ReadAudioSliceAsync(owner, material.VersionId,
                step.FileIndex, step.SourceOffset, step.Samples, CancellationToken.None);
            var log = new AiRequestLogEntity
            {
                ClientRequestId = step.Current.RequestId,
                UserId = owner,
                Module = "notes",
                Purpose = "ai.asr-call",
                SourceObjectType = "native-note-asr-task",
                SourceObjectId = asrTaskId.ToString("D"),
                Provider = "synthetic",
                Model = "mimo-v2.5-asr",
                ModelProfileId = consent.ModelId,
                ModelRevision = consent.ModelRevision,
                ModelEndpoint = consent.Endpoint,
                CorrelationId = "synthetic-asr",
                Status = "succeeded",
                AttemptNumber = 1,
                MaxAttempts = 1,
                ResponseText = "合成转写 " + (step.Index + 1),
                FinishedAt = DateTimeOffset.UtcNow
            };
            db.AiRequestLogs.Add(log);
            db.AiCallAudio.Add(new AiCallAudioEntity
            {
                LogId = log.Id,
                Name = $"segment-{step.Index + 1:D5}.wav",
                Format = "wav",
                SizeBytes = bytes.Length,
                Sha256 = Digest(bytes),
                ObjectKey = "synthetic/audio.wav"
            });
            logs[step.Index] = log;
        }
        await db.SaveChangesAsync();
        _ = await AsrStore().UpdateAsync(owner, asrTaskId, state =>
        {
            foreach (var step in state.Steps)
            {
                step.Current.Status = "Succeeded";
                step.Current.LogId = logs[step.Index].Id;
                step.Current.ErrorMessage = null;
            }
            state.CompletionAcknowledged = true;
        }, CancellationToken.None);
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"HTTP {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ApiResponse<T>>(body, Json)!.Data!;
    }

    private sealed class RecordingJobs : Hangfire.IBackgroundJobClient
    {
        public List<Hangfire.Common.Job> Items { get; } = [];
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
        {
            Items.Add(job);
            return Items.Count.ToString();
        }
        public bool ChangeState(string jobId, Hangfire.States.IState state,
            string expectedState) => throw new NotSupportedException();
    }

    public void Dispose() => host.Models.Reset();
}
