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

        var first = await StartAsync(client, material);
        Assert.Equal(NoteProcessingState.Processing, first.State);
        Assert.True(first.ManualStartAvailable == false);
        Assert.True(first.Asr.TaskCreated);
        Assert.True(first.Derived.TaskCreated);
        Assert.Equal(NoteProcessingIdentity.Task(owner.UserId, material.VersionId,
            NoteProcessingTaskKind.Asr), first.Asr.TaskId);
        Assert.Equal(NoteProcessingIdentity.Task(owner.UserId, material.VersionId,
            NoteProcessingTaskKind.Derived), first.Derived.TaskId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));

        var replay = await StartAsync(client, material);
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
    public async Task EnablingAutoProcessingRequiresStandingAuthorizationAndChargeAcknowledgement()
    {
        var owner = await OwnerAsync("standing-ack");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var initial = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));

        var rejected = await client.PutAsJsonAsync(Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(initial.Revision, true));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var unchanged = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        Assert.False(unchanged.AutoProcessingEnabled);
        Assert.False(unchanged.StandingAuthorizationValid);
        Assert.Null(unchanged.StandingAuthorizationRevision);

        var saved = await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(initial.Revision, true,
                ConfirmStandingAuthorization: true, AcknowledgePossibleCharge: true,
                DefaultTargetRevision: initial.CurrentDefaultTargetRevision)));
        Assert.True(saved.AutoProcessingEnabled);
        Assert.True(saved.StandingAuthorizationValid);
        Assert.True(saved.PossibleChargeAcknowledged);
        Assert.NotNull(saved.StandingAuthorizationRevision);
        Assert.Equal(saved.CurrentDefaultTargetRevision,
            saved.StandingAuthorizationTargetRevision);
        Assert.NotEmpty(saved.DefaultTargets);
        Assert.Equal(NoteProcessingAuthorizationPurpose.NativeNoteProcessing, saved.Purpose);
        Assert.Equal(NoteProcessingAuthorizationPurpose.MaterialScope, saved.MaterialScope);
        Assert.Equal(NoteProcessingAuthorizationRules.CostNotice, saved.CostNotice);

        var persisted = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        Assert.Equal(saved.Revision, persisted.Revision);
        Assert.Equal(saved.StandingAuthorizationRevision,
            persisted.StandingAuthorizationRevision);
        Assert.True(persisted.StandingAuthorizationValid);

        var disabled = await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(persisted.Revision, false)));
        Assert.False(disabled.AutoProcessingEnabled);
        Assert.False(disabled.StandingAuthorizationValid);
        Assert.Null(disabled.StandingAuthorizationRevision);
        Assert.False(disabled.PossibleChargeAcknowledged);
    }

    [Fact]
    public async Task DefaultTargetChangesInvalidateStandingAuthorizationAndBlockAutoStart()
    {
        var owner = await OwnerAsync("standing-target-drift");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        var initial = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        var enabled = await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(initial.Revision, true,
                ConfirmStandingAuthorization: true, AcknowledgePossibleCharge: true,
                DefaultTargetRevision: initial.CurrentDefaultTargetRevision)));
        Assert.True(enabled.StandingAuthorizationValid);
        Assert.Contains(enabled.DefaultTargets,
            value => value.Contains("mimo-v2.5-asr", StringComparison.Ordinal) &&
                value.Contains("mimo-asr/1", StringComparison.Ordinal) &&
                value.Contains("cloud-asr", StringComparison.Ordinal));
        Assert.Contains(enabled.DefaultTargets,
            value => value.Contains("synthetic-text", StringComparison.Ordinal) &&
                value.Contains("endpoint 已纳入目标策略", StringComparison.Ordinal));

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id);
            profile.Revision = Guid.NewGuid();
            profile.Model += "-changed";
            await db.SaveChangesAsync();
        }

        var changed = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        Assert.False(changed.StandingAuthorizationValid);
        Assert.Null(changed.StandingAuthorizationRevision);
        Assert.Null(changed.StandingAuthorizationTargetRevision);
        Assert.NotEqual(enabled.CurrentDefaultTargetRevision,
            changed.CurrentDefaultTargetRevision);
        Assert.Contains(changed.DefaultTargets,
            value => value.Contains("changed", StringComparison.Ordinal));
        Assert.NotEqual(enabled.DefaultTargets, changed.DefaultTargets);

        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var pending = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.False(pending.AutoStarted);
        Assert.False(pending.Asr.TaskCreated);
        Assert.False(pending.Derived.TaskCreated);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));

        var reauthorized = await SetAutoProcessingAsync(client, true);
        Assert.True(reauthorized.StandingAuthorizationValid);
        Assert.Equal(reauthorized.CurrentDefaultTargetRevision,
            reauthorized.StandingAuthorizationTargetRevision);
        Assert.NotEqual(enabled.StandingAuthorizationRevision,
            reauthorized.StandingAuthorizationRevision);

        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();
        await dispatch.RunAsync(CancellationToken.None);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(started.AutoStarted);
        Assert.True(started.Asr.TaskCreated);
        Assert.True(started.Derived.TaskCreated);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
    }

    [Fact]
    public async Task EndpointDriftInvalidatesStandingAuthorization()
    {
        var owner = await OwnerAsync("standing-endpoint-drift");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        var initial = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        var enabled = await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(initial.Revision, true,
                ConfirmStandingAuthorization: true, AcknowledgePossibleCharge: true,
                DefaultTargetRevision: initial.CurrentDefaultTargetRevision)));
        Assert.True(enabled.StandingAuthorizationValid);

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id);
            profile.BaseUrl += "changed/";
            await db.SaveChangesAsync();
        }

        var changed = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        Assert.False(changed.StandingAuthorizationValid);
        Assert.Null(changed.StandingAuthorizationRevision);
        Assert.Null(changed.StandingAuthorizationTargetRevision);
        Assert.NotEqual(enabled.CurrentDefaultTargetRevision,
            changed.CurrentDefaultTargetRevision);
    }

    [Fact]
    public async Task StandaloneNoteOrganizationTargetIsVisibleAndEndpointDriftInvalidatesIt()
    {
        var owner = await OwnerAsync("standing-note-organization");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        try
        {
            _ = await ConfigureAsync(client, auto: false);
            var models = await Data<IReadOnlyList<AiModelDto>>(
                await client.GetAsync("/api/v1/ai/models"));
            var organization = await SaveOrResetModelAsync(client, models,
                "整理用途 note-processing", "http://organization.invalid/v1/",
                "organization-text", [AiModelCapability.Text, AiModelCapability.Vision],
                4096, "synthetic-organization-key", null);
            await SetNoteOrganizationDefaultAsync(client, organization.Id);

            var initial = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.Contains(initial.DefaultTargets, value =>
                value.Contains("整理用途 note-processing", StringComparison.Ordinal) &&
                value.Contains("organization-text", StringComparison.Ordinal) &&
                value.Contains("endpoint 已纳入目标策略", StringComparison.Ordinal));
            Assert.DoesNotContain(initial.DefaultTargets, value =>
                value.Contains("整理 note-processing", StringComparison.Ordinal));

            var material = await CompleteMaterialAsync(owner, client, withAudio: false);
            var plan = await PlanAsync(client, material);
            var derived = Assert.Single(plan.Targets.Where(target =>
                target.Kind == NoteProcessingTaskKind.Derived));
            Assert.Equal(organization.Id, derived.ModelId);
            Assert.Equal(organization.Revision, derived.ModelRevision);
            Assert.Equal(organization.BaseUrl, derived.Endpoint);
            Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));

            var enabled = await SetAutoProcessingAsync(client, true);
            Assert.True(enabled.StandingAuthorizationValid);
            Assert.Equal(initial.CurrentDefaultTargetRevision,
                enabled.StandingAuthorizationTargetRevision);

            var changedModels = await Data<IReadOnlyList<AiModelDto>>(
                await client.GetAsync("/api/v1/ai/models"));
            _ = await SaveOrResetModelAsync(client, changedModels,
                "整理用途 note-processing", "http://organization-changed.invalid/v1/",
                "organization-text", [AiModelCapability.Text, AiModelCapability.Vision],
                4096, "synthetic-organization-key", null);

            var changed = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.False(changed.StandingAuthorizationValid);
            Assert.Null(changed.StandingAuthorizationRevision);
            Assert.Null(changed.StandingAuthorizationTargetRevision);
            Assert.NotEqual(enabled.CurrentDefaultTargetRevision,
                changed.CurrentDefaultTargetRevision);
            Assert.Contains(changed.DefaultTargets, value =>
                value.Contains("整理用途 note-processing", StringComparison.Ordinal) &&
                value.Contains("endpoint 已纳入目标策略", StringComparison.Ordinal));
        }
        finally
        {
            await SetNoteOrganizationDefaultAsync(client, Guid.Empty);
        }
    }

    [Fact]
    public async Task StandaloneNoteOrganizationProfileDriftAndUnavailabilityFailClosed()
    {
        var owner = await OwnerAsync("standing-note-organization-drift");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        try
        {
            host.Models.Reset();
            _ = await ConfigureAsync(client, auto: false);
            var organization = await SaveOrganizationProfileAsync(client,
                "整理用途 note-processing", "http://organization.invalid/v1/",
                "organization-text");
            var replacement = await SaveOrganizationProfileAsync(client,
                "整理用途替换 note-processing", "http://organization-replacement.invalid/v1/",
                "organization-replacement");
            await SetNoteOrganizationDefaultAsync(client, organization.Id);
            var enabled = await SetAutoProcessingAsync(client, true);
            Assert.True(enabled.StandingAuthorizationValid);

            using (var scope = host.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
                var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == organization.Id);
                profile.Revision = Guid.NewGuid();
                await db.SaveChangesAsync();
            }
            var revisionDrift = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.False(revisionDrift.StandingAuthorizationValid);
            Assert.Null(revisionDrift.StandingAuthorizationRevision);
            Assert.NotEqual(enabled.CurrentDefaultTargetRevision,
                revisionDrift.CurrentDefaultTargetRevision);

            await SetNoteOrganizationDefaultAsync(client, replacement.Id);
            var idDrift = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.False(idDrift.StandingAuthorizationValid);
            Assert.Contains(idDrift.DefaultTargets, value =>
                value.Contains("organization-replacement", StringComparison.Ordinal));

            var reauthorized = await SetAutoProcessingAsync(client, true);
            Assert.True(reauthorized.StandingAuthorizationValid);
            await SetOrganizationAvailabilityAsync(replacement.Id, enabled: false);
            var disabled = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.False(disabled.StandingAuthorizationValid);
            var disabledAuthorization = await SetAutoProcessingAsync(client, true);
            Assert.True(disabledAuthorization.StandingAuthorizationValid);

            var disabledMaterial = await CompleteMaterialAsync(owner, client, withAudio: false);
            var disabledStatus = await StatusAsync(client, disabledMaterial.NoteId,
                disabledMaterial.VersionId);
            Assert.Equal(NoteProcessingState.PendingProcessing, disabledStatus.State);
            Assert.False(disabledStatus.Derived.TaskCreated);
            Assert.Contains("未启用", disabledStatus.PendingReason);
            Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
            Assert.Equal(0, host.Models.Calls);

            await SetOrganizationAvailabilityAsync(replacement.Id, enabled: true,
                capabilities: AiModelCapabilityFlags.Text);
            var incapable = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.False(incapable.StandingAuthorizationValid);
            var incapableAuthorization = await SetAutoProcessingAsync(client, true);
            Assert.True(incapableAuthorization.StandingAuthorizationValid);

            var incapableMaterial = await CompleteMaterialAsync(owner, client, withAudio: false);
            var incapableStatus = await StatusAsync(client, incapableMaterial.NoteId,
                incapableMaterial.VersionId);
            Assert.Equal(NoteProcessingState.PendingProcessing, incapableStatus.State);
            Assert.False(incapableStatus.Derived.TaskCreated);
            Assert.Contains("未声明视觉能力", incapableStatus.PendingReason);
            Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
            Assert.Equal(0, host.Models.Calls);
        }
        finally
        {
            await SetNoteOrganizationDefaultAsync(client, Guid.Empty);
        }
    }

    [Fact]
    public async Task StandingAuthorizationRejectsAStaleDefaultTargetRevision()
    {
        var owner = await OwnerAsync("standing-target-stale");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        var initial = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id);
            profile.Revision = Guid.NewGuid();
            profile.Model += "-changed";
            await db.SaveChangesAsync();
        }

        var stale = await client.PutAsJsonAsync(Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(initial.Revision, true,
                ConfirmStandingAuthorization: true, AcknowledgePossibleCharge: true,
                DefaultTargetRevision: initial.CurrentDefaultTargetRevision));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var unchanged = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        Assert.False(unchanged.AutoProcessingEnabled);
        Assert.False(unchanged.StandingAuthorizationValid);
        Assert.Null(unchanged.StandingAuthorizationRevision);
    }

    [Fact]
    public async Task LegacyAutoEnabledSettingWithoutStandingAuthorizationStaysPending()
    {
        var owner = await OwnerAsync("standing-missing");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true);
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var config = await db.NoteProcessingPreferences.SingleAsync(x =>
                x.OwnerId == owner.UserId);
            config.StandingAuthorizationRevision = null;
            config.StandingAuthorizationPurpose = null;
            config.StandingAuthorizationMaterialScope = null;
            config.StandingAuthorizationCostNotice = null;
            config.StandingAuthorizationPossibleChargeAcknowledged = false;
            await db.SaveChangesAsync();
        }
        host.Models.Reset();

        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var pending = await StatusAsync(client, material.NoteId, material.VersionId);

        Assert.Equal(NoteProcessingState.PendingProcessing, pending.State);
        Assert.True(pending.AutoProcessingEnabled);
        Assert.False(pending.AutoStarted);
        Assert.Contains("持续授权", pending.PendingReason);
        Assert.False(pending.Asr.TaskCreated);
        Assert.False(pending.Derived.TaskCreated);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));

        var manual = await StartAsync(client, material);
        Assert.Equal(NoteProcessingAuthorizationSource.Manual, manual.Authorization?.Source);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task PlanIsVisibleBeforeStartAndConsentRevisionIsEnforced()
    {
        var owner = await OwnerAsync("plan-consent");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        var missingConsent = await client.PostAsJsonAsync(Processing + "/start",
            new { noteId = material.NoteId, versionId = material.VersionId });
        Assert.Equal(HttpStatusCode.BadRequest, missingConsent.StatusCode);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));

        var plan = await PlanAsync(client, material);
        Assert.Equal(NoteProcessingAuthorizationPurpose.NativeNoteProcessing, plan.Purpose);
        Assert.Equal(NoteProcessingAuthorizationPurpose.MaterialScope, plan.MaterialScope);
        Assert.Equal(NoteProcessingAuthorizationRules.CostNotice, plan.CostNotice);
        Assert.True(plan.PossibleCharge);
        Assert.True(plan.AsrRequired);
        Assert.NotEqual(Guid.Empty, plan.AuthorizationRevision);
        Assert.False(plan.StandingAuthorizationValid);
        Assert.Equal(2, plan.Targets.Count);
        Assert.Contains(plan.Targets, target =>
            target.Kind == NoteProcessingTaskKind.Asr && target.Required && target.Paid);
        Assert.Contains(plan.Targets, target =>
            target.Kind == NoteProcessingTaskKind.Derived && target.Required && target.Paid);
        Assert.All(plan.Targets, target =>
        {
            Assert.False(string.IsNullOrWhiteSpace(target.ModelName));
            Assert.False(string.IsNullOrWhiteSpace(target.Endpoint));
        });
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);

        var stale = Consent(plan) with { AuthorizationRevision = Guid.NewGuid() };
        var rejected = await client.PostAsJsonAsync(Processing + "/start",
            new StartNoteProcessingRequest(material.NoteId, material.VersionId, stale));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));

        var invalidStanding = Consent(plan) with
        {
            StandingAuthorizationRevision = Guid.NewGuid()
        };
        var rejectedStanding = await client.PostAsJsonAsync(Processing + "/start",
            new StartNoteProcessingRequest(material.NoteId, material.VersionId,
                invalidStanding));
        Assert.Equal(HttpStatusCode.Conflict, rejectedStanding.StatusCode);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));

        var started = await StartAsync(client, material);
        Assert.NotNull(started.Authorization);
        Assert.True(started.Authorization!.Confirmed);
        Assert.True(started.Authorization.PossibleChargeAcknowledged);
        Assert.Equal(NoteProcessingAuthorizationSource.Manual, started.Authorization.Source);
        Assert.Equal(plan.AuthorizationRevision,
            started.Authorization.AuthorizationRevision);
        Assert.Equal(2, started.Authorization.Targets.Count);
        Assert.Equal(plan.Targets, started.Authorization.Targets);
        Assert.Equal(plan.PurposeText, started.Authorization.PurposeText);
        Assert.Equal(plan.MaterialScopeText, started.Authorization.MaterialScopeText);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));

        var persisted = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(started.Authorization.AuthorizationRevision,
            persisted.Authorization?.AuthorizationRevision);
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task ManualStartRejectsStaleRouteConsentBeforeCreatingAnything()
    {
        var owner = await OwnerAsync("stale-route-consent");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        var route = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        var planResponse = await client.GetAsync(
            $"{Processing}/plan?noteId={material.NoteId}&versionId={material.VersionId}");
        var planJson = JsonNode.Parse(await planResponse.Content.ReadAsStringAsync())!["data"]!;
        var plan = planJson.Deserialize<NoteProcessingPlanDto>(Json)!;

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var config = await db.NoteAsrRouteConfigs.SingleAsync(x => x.OwnerId == owner.UserId);
            config.ConsentVersion = "synthetic-cloud-consent-v2";
            await db.SaveChangesAsync();
        }

        var rejected = await client.PostAsJsonAsync(Processing + "/start",
            new StartNoteProcessingRequest(material.NoteId, material.VersionId, Consent(plan)));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
        Assert.Equal(0, await AttemptCountAsync(owner.UserId,
            NoteProcessingIdentity.Task(owner.UserId, material.VersionId,
                NoteProcessingTaskKind.Asr)));
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));

        var asrTarget = planJson["targets"]!.AsArray()
            .Single(target => target!["kind"]!.GetValue<string>() == "Asr")!;
        Assert.Equal(route.Revision.ToString("D"),
            asrTarget["routeConfigRevision"]!.GetValue<string>());
        Assert.Equal("synthetic-cloud-consent-v1",
            asrTarget["routeConsentVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task LegacyRandomRequestIdsConvergeAndConflictingTargetsReturn409()
    {
        var owner = await OwnerAsync("legacy-chain");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StartAsync(client, material);
        Assert.NotNull(started.Asr.TaskId);
        Assert.NotNull(started.Derived.TaskId);

        using var scope = host.Factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var asrTemplate = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, confirmed: true, CancellationToken.None);
        var asrRequests = Enumerable.Range(0, 3)
            .Select(_ => asrTemplate with { TaskId = Guid.NewGuid() })
            .ToArray();
        var asrResponses = await Task.WhenAll(asrRequests.Select(request =>
            client.PostAsJsonAsync("/api/v1/native-notes/asr-tasks", request)));
        var asrTasks = new List<NoteAsrTaskDto>();
        foreach (var response in asrResponses)
            asrTasks.Add(await Data<NoteAsrTaskDto>(response));
        Assert.All(asrTasks, task => Assert.Equal(started.Asr.TaskId, task.TaskId));
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));

        var derivedState = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId.Value);
        var derivedRequests = Enumerable.Range(0, 3).Select(_ =>
            derivedState.Request with { TaskId = Guid.NewGuid() }).ToArray();
        var derivedResponses = await Task.WhenAll(derivedRequests.Select(request =>
            client.PostAsJsonAsync("/api/v1/native-notes/derived-tasks", request)));
        var derivedTasks = new List<NoteDerivedTaskDto>();
        foreach (var response in derivedResponses)
            derivedTasks.Add(await Data<NoteDerivedTaskDto>(response));
        Assert.All(derivedTasks, task => Assert.Equal(started.Derived.TaskId, task.TaskId));
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));

        var otherAsr = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Other ASR " + Guid.NewGuid().ToString("N"),
                "http://other-asr.invalid/v1/", "mimo-v2.5-asr", true,
                [AiModelCapability.Transcription], 30, 1024, "other-asr-key",
                AsrPresetId: "mimo-asr")));
        var asrDestination = (await Data<NoteAsrDestinationsDto>(
                await client.GetAsync("/api/v1/native-notes/asr-tasks/destinations")))
            .Items.Single(item => item.ModelId == otherAsr.Id);
        var conflictingAsr = asrTemplate with
        {
            TaskId = Guid.NewGuid(),
            Consent = new AiAsrDestinationConsent(asrDestination.ModelId,
                asrDestination.ModelRevision, asrDestination.Endpoint,
                asrDestination.PresetId, asrDestination.PresetVersion, true)
        };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(
            "/api/v1/native-notes/asr-tasks", conflictingAsr)).StatusCode);

        var otherDerived = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Other derived " + Guid.NewGuid().ToString("N"),
                "http://other-derived.invalid/v1/", "synthetic-other-text", true,
                [AiModelCapability.Text, AiModelCapability.Vision], 30, 4096,
                "other-derived-key")));
        Assert.NotEqual(defaults.Derived.Id, otherDerived.Id);
        var conflictingDerived = derivedState.Request with
        {
            TaskId = Guid.NewGuid(),
            Consent = new NoteDerivedGenerationConsent(otherDerived.Id,
                otherDerived.Revision, otherDerived.BaseUrl, true)
        };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(
            "/api/v1/native-notes/derived-tasks", conflictingDerived)).StatusCode);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
    }

    [Fact]
    public async Task LegacyTasksWithoutAuthorizationRequireConsentAndCanPersistMatchingSnapshot()
    {
        var owner = await OwnerAsync("legacy-processing-snapshot");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StartAsync(client, material);
        var processingId = NoteProcessingIdentity.Processing(owner.UserId, material.VersionId);
        var processingStore = host.Factory.Services.GetRequiredService<NoteProcessingStore>();
        await processingStore.WithLockAsync(owner.UserId, async ct =>
        {
            var state = await processingStore.ReadAsync(owner.UserId, processingId, ct);
            state.Authorization = null;
            _ = await processingStore.SaveLockedAsync(state, ct);
            return true;
        }, CancellationToken.None);

        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.False(status.ManualStartAvailable);
        Assert.Null(status.Authorization);
        var missingConsent = await client.PostAsJsonAsync(Processing + "/start",
            new { noteId = material.NoteId, versionId = material.VersionId });
        Assert.Equal(HttpStatusCode.BadRequest, missingConsent.StatusCode);

        var replay = await StartAsync(client, material);
        Assert.Equal(started.Asr.TaskId, replay.Asr.TaskId);
        Assert.Equal(started.Derived.TaskId, replay.Derived.TaskId);
        Assert.Equal(NoteProcessingAuthorizationSource.Manual, replay.Authorization?.Source);
        Assert.NotNull(replay.Authorization);
        var persisted = await processingStore.ReadAsync(owner.UserId, processingId,
            CancellationToken.None);
        Assert.NotNull(persisted.Authorization);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
    }

    [Fact]
    public async Task ManualReplayWithChangedDefaultTargetReturns409ForExistingTaskPair()
    {
        var owner = await OwnerAsync("processing-target-drift");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StartAsync(client, material);

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id);
            profile.Revision = Guid.NewGuid();
            profile.Model += "-changed";
            await db.SaveChangesAsync();
        }

        var currentPlan = await PlanAsync(client, material);
        var response = await client.PostAsJsonAsync(Processing + "/start",
            new StartNoteProcessingRequest(material.NoteId, material.VersionId,
                Consent(currentPlan)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var persisted = await host.Factory.Services.GetRequiredService<NoteProcessingStore>()
            .ReadAsync(owner.UserId,
                NoteProcessingIdentity.Processing(owner.UserId, material.VersionId),
                CancellationToken.None);
        Assert.Equal(started.Authorization?.AuthorizationRevision,
            persisted.Authorization?.AuthorizationRevision);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
    }

    [Fact]
    public async Task DispatchIgnoresDuplicateLegacyInitialTasksForOneMaterialVersion()
    {
        var owner = await OwnerAsync("legacy-duplicate-dispatch");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StartAsync(client, material);
        var asrId = started.Asr.TaskId!.Value;
        var derivedId = started.Derived.TaskId!.Value;
        var duplicateAsrId = Guid.NewGuid();
        var duplicateDerivedId = Guid.NewGuid();
        await AddLegacyDuplicateAsync(owner.UserId, asrId, duplicateAsrId,
            material.VersionId, derived: false);
        await AddLegacyDuplicateAsync(owner.UserId, derivedId, duplicateDerivedId,
            material.VersionId, derived: true);

        var duplicateAsrBefore = await AsrTaskAsync(owner.UserId, duplicateAsrId);
        var duplicateDerivedBefore = await DerivedTaskAsync(owner.UserId, duplicateDerivedId);
        var duplicateAsrView = await Data<NoteAsrTaskDto>(await client.GetAsync(
            $"/api/v1/native-notes/asr-tasks/{duplicateAsrId}"));
        var duplicateDerivedView = await Data<NoteDerivedTaskDto>(await client.GetAsync(
            $"/api/v1/native-notes/derived-tasks/{duplicateDerivedId}"));
        Assert.False(duplicateAsrView.IsCanonicalInitial);
        Assert.Equal(asrId, duplicateAsrView.CanonicalInitialTaskId);
        Assert.False(duplicateDerivedView.IsCanonicalInitial);
        Assert.Equal(derivedId, duplicateDerivedView.CanonicalInitialTaskId);
        var canonicalAsrView = await Data<NoteAsrTaskDto>(await client.GetAsync(
            $"/api/v1/native-notes/asr-tasks/{asrId}"));
        var canonicalDerivedView = await Data<NoteDerivedTaskDto>(await client.GetAsync(
            $"/api/v1/native-notes/derived-tasks/{derivedId}"));
        Assert.True(canonicalAsrView.IsCanonicalInitial);
        Assert.True(canonicalDerivedView.IsCanonicalInitial);

        var jobs = new RecordingJobs();
        var asrService = host.Factory.Services.GetRequiredService<NoteAsrService>();
        var asrDispatch = new NoteAsrDispatchJob(AsrStore(), asrService, jobs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NoteAsrDispatchJob>.Instance);
        await asrDispatch.RunAsync(CancellationToken.None);
        var asrJob = Assert.Single(jobs.Items.Where(item => item.Type == typeof(NoteAsrJob) &&
            item.Args.Count >= 2 && Equals(item.Args[0], owner.UserId) &&
            Equals(item.Args[1], asrId)));
        Assert.Equal(asrId, asrJob.Args[1]);

        jobs.Items.Clear();
        var derivedDispatch = new NoteDerivedDispatchJob(DerivedStore(), jobs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NoteDerivedDispatchJob>.Instance);
        await derivedDispatch.RunAsync(CancellationToken.None);
        var derivedJob = Assert.Single(jobs.Items.Where(item => item.Type == typeof(NoteDerivedJob) &&
            item.Args.Count >= 2 && Equals(item.Args[0], owner.UserId) &&
            Equals(item.Args[1], derivedId)));
        Assert.Equal(derivedId, derivedJob.Args[1]);

        await host.Factory.Services.GetRequiredService<NoteAsrJob>().RunAsync(owner.UserId,
            duplicateAsrId, CancellationToken.None);
        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(owner.UserId,
            duplicateDerivedId, CancellationToken.None);
        var duplicateAsrAfter = await AsrTaskAsync(owner.UserId, duplicateAsrId);
        var duplicateDerivedAfter = await DerivedTaskAsync(owner.UserId, duplicateDerivedId);
        Assert.Equal(duplicateAsrBefore.Revision, duplicateAsrAfter.Revision);
        Assert.Equal(duplicateDerivedBefore.Revision, duplicateDerivedAfter.Revision);
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task ChainIndexWinsOverEarlierLegacyDuplicateAndKeepsStatusAuditable()
    {
        var owner = await OwnerAsync("legacy-chain-precedence");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StartAsync(client, material);
        var asrId = started.Asr.TaskId!.Value;
        var derivedId = started.Derived.TaskId!.Value;
        var earlierAsrId = Guid.NewGuid();
        var earlierDerivedId = Guid.NewGuid();
        await AddLegacyDuplicateAsync(owner.UserId, asrId, earlierAsrId,
            material.VersionId, derived: false, minuteOffset: -30, clearChain: false);
        await AddLegacyDuplicateAsync(owner.UserId, derivedId, earlierDerivedId,
            material.VersionId, derived: true, minuteOffset: -30, clearChain: false);

        var duplicateAsr = await Data<NoteAsrTaskDto>(await client.GetAsync(
            $"/api/v1/native-notes/asr-tasks/{earlierAsrId}"));
        var duplicateDerived = await Data<NoteDerivedTaskDto>(await client.GetAsync(
            $"/api/v1/native-notes/derived-tasks/{earlierDerivedId}"));
        Assert.False(duplicateAsr.IsCanonicalInitial);
        Assert.Equal(asrId, duplicateAsr.CanonicalInitialTaskId);
        Assert.False(duplicateDerived.IsCanonicalInitial);
        Assert.Equal(derivedId, duplicateDerived.CanonicalInitialTaskId);

        var jobs = new RecordingJobs();
        var asrDispatch = new NoteAsrDispatchJob(AsrStore(),
            host.Factory.Services.GetRequiredService<NoteAsrService>(), jobs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NoteAsrDispatchJob>.Instance);
        await asrDispatch.RunAsync(CancellationToken.None);
        var asrJob = Assert.Single(jobs.Items.Where(item => item.Type == typeof(NoteAsrJob) &&
            item.Args.Count >= 2 && Equals(item.Args[0], owner.UserId) &&
            Equals(item.Args[1], asrId)));
        Assert.Equal(asrId, asrJob.Args[1]);

        jobs.Items.Clear();
        var derivedDispatch = new NoteDerivedDispatchJob(DerivedStore(), jobs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NoteDerivedDispatchJob>.Instance);
        await derivedDispatch.RunAsync(CancellationToken.None);
        var derivedJob = Assert.Single(jobs.Items.Where(item => item.Type == typeof(NoteDerivedJob) &&
            item.Args.Count >= 2 && Equals(item.Args[0], owner.UserId) &&
            Equals(item.Args[1], derivedId)));
        Assert.Equal(derivedId, derivedJob.Args[1]);

        await host.Factory.Services.GetRequiredService<NoteAsrJob>().RunAsync(owner.UserId,
            earlierAsrId, CancellationToken.None);
        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(owner.UserId,
            earlierDerivedId, CancellationToken.None);
        Assert.Null((await AsrTaskAsync(owner.UserId, earlierAsrId)).AttemptId);
        Assert.Equal((Asr: 2, Derived: 2), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task ProcessingStatePointingAtLegacyDuplicatesFailsClosedToCanonicalHeads()
    {
        var owner = await OwnerAsync("legacy-state-duplicates");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StartAsync(client, material);
        var asrId = started.Asr.TaskId!.Value;
        var derivedId = started.Derived.TaskId!.Value;
        var duplicateAsrId = Guid.NewGuid();
        var duplicateDerivedId = Guid.NewGuid();
        await AddLegacyDuplicateAsync(owner.UserId, asrId, duplicateAsrId,
            material.VersionId, derived: false);
        await AddLegacyDuplicateAsync(owner.UserId, derivedId, duplicateDerivedId,
            material.VersionId, derived: true);

        var processingStore = host.Factory.Services.GetRequiredService<NoteProcessingStore>();
        var processingId = NoteProcessingIdentity.Processing(owner.UserId, material.VersionId);
        await processingStore.WithLockAsync(owner.UserId, async ct =>
        {
            var state = await processingStore.ReadAsync(owner.UserId, processingId, ct);
            state.AsrTaskId = duplicateAsrId;
            state.DerivedTaskId = duplicateDerivedId;
            _ = await processingStore.SaveLockedAsync(state, ct);
            return true;
        }, CancellationToken.None);

        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(asrId, status.Asr.TaskId);
        Assert.Equal(derivedId, status.Derived.TaskId);
        Assert.True(status.Asr.TaskCreated);
        Assert.True(status.Derived.TaskCreated);
        Assert.False(status.ManualStartAvailable);

        var replay = await StartAsync(client, material);
        Assert.Equal(asrId, replay.Asr.TaskId);
        Assert.Equal(derivedId, replay.Derived.TaskId);
        var persisted = await processingStore.ReadAsync(owner.UserId, processingId,
            CancellationToken.None);
        Assert.Equal(asrId, persisted.AsrTaskId);
        Assert.Equal(derivedId, persisted.DerivedTaskId);
        Assert.Equal((Asr: 2, Derived: 2), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task AutoStartRevalidatesCurrentMaterialVersionInsideTheProcessingLock()
    {
        var owner = await OwnerAsync("auto-version-race");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var first = await CompleteMaterialAsync(owner, client, withAudio: true);
        _ = await SetAutoProcessingAsync(client, true);
        var next = CreateNextMaterial(first);
        var processingStore = host.Factory.Services.GetRequiredService<NoteProcessingStore>();
        var lockHeld = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = Task.Run(() => processingStore.WithLockAsync(owner.UserId, async ct =>
        {
            lockHeld.SetResult(true);
            await release.Task.WaitAsync(ct);
            return true;
        }, CancellationToken.None));
        await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var autoStart = Task.Run(() => host.Factory.Services
            .GetRequiredService<NoteProcessingService>()
            .TryAutoStartAfterCompletionAsync(owner.UserId, first.VersionId,
                CancellationToken.None));
        await Task.Delay(250);
        Assert.False(autoStart.IsCompleted,
            "自动启动必须先完成锁外预检，再等待 owner/processing 锁");

        await NoteDerivedFixture.UploadAsync(
            host.Factory.Services.GetRequiredService<NoteMaterialUploadStore>(),
            owner.UserId, next);
        release.SetResult(true);
        await hold.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(await autoStart.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));

        await host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>()
            .RunAsync(CancellationToken.None);
        var current = await StatusAsync(client, next.NoteId, next.VersionId);
        Assert.True(current.AutoStarted);
        Assert.Equal(next.VersionId, current.VersionId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
    }

    [Fact]
    public async Task LegacyAsrStartCannotForgeAChainParent()
    {
        var owner = await OwnerAsync("legacy-parent-forge");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var template = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, confirmed: true, CancellationToken.None);
        var forged = template with
        {
            TaskId = Guid.NewGuid(),
            ParentTaskId = Guid.NewGuid(),
            ReprocessReason = "forged"
        };

        var response = await client.PostAsJsonAsync("/api/v1/native-notes/asr-tasks", forged);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task HighLevelStartAdoptsAnExistingLegacyRandomAsrChain()
    {
        var owner = await OwnerAsync("legacy-first");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var legacyRequest = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, confirmed: true, CancellationToken.None) with { TaskId = Guid.NewGuid() };
        var legacy = await Data<NoteAsrTaskDto>(await client.PostAsJsonAsync(
            "/api/v1/native-notes/asr-tasks", legacyRequest));

        var started = await StartAsync(client, material);
        Assert.Equal(legacy.TaskId, started.Asr.TaskId);
        var derived = await DerivedTaskAsync(owner.UserId, started.Derived.TaskId!.Value);
        Assert.Equal(legacy.TaskId, derived.Request.AsrTaskId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task HighLevelStartRejectsPartialLegacyChainAfterTheAsrTargetChanges()
    {
        var owner = await OwnerAsync("legacy-target-change");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var legacyRequest = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, confirmed: true, CancellationToken.None) with { TaskId = Guid.NewGuid() };
        _ = await Data<NoteAsrTaskDto>(await client.PostAsJsonAsync(
            "/api/v1/native-notes/asr-tasks", legacyRequest));

        var otherAsr = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("Changed ASR " + Guid.NewGuid().ToString("N"),
                "http://changed-asr.invalid/v1/", "mimo-v2.5-asr", true,
                [AiModelCapability.Transcription], 30, 1024, "changed-asr-key",
                AsrPresetId: "mimo-asr")));
        var settings = await Data<AiModelSettingsDto>(
            await client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
            "/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, settings.Enabled,
                settings.TextModelId, settings.VisionModelId, otherAsr.Id)));

        var plan = await PlanAsync(client, material);
        var rejected = await client.PostAsJsonAsync(Processing + "/start",
            new StartNoteProcessingRequest(material.NoteId, material.VersionId,
                Consent(plan)));

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal((Asr: 1, Derived: 0), TaskCounts(owner));
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
    public async Task RepeatedCompensationScansKeepBlockingIssueStableWithoutRevisionRotation()
    {
        var owner = await OwnerAsync("dispatch-blocked-stable");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "dispatch-blocked-admin-" + Guid.NewGuid().ToString("N"), "admin");
        using var adminClient = AuthRegistrationTestHost.Authed(host.Factory, admin.AccessToken);
        var settings = await Data<AiModelSettingsDto>(
            await adminClient.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await adminClient.PutAsJsonAsync(
            "/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, true, null, null, null)));
        _ = await SetAutoProcessingAsync(client, true);
        host.Models.Reset();

        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var first = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingState.PendingProcessing, first.State);
        var store = host.Factory.Services.GetRequiredService<NoteProcessingStore>();
        var before = await store.ReadAsync(owner.UserId, first.ProcessingId, default);
        var callsBefore = host.Models.Calls;
        var aiCallsBefore = await AiCallCountAsync(owner.UserId);
        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();

        await dispatch.RunAsync(default);
        await dispatch.RunAsync(default);

        var after = await store.ReadAsync(owner.UserId, first.ProcessingId, default);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.PendingReason, after.PendingReason);
        Assert.Equal(before.Issues, after.Issues);
        Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
        Assert.Equal(callsBefore, host.Models.Calls);
        Assert.Equal(aiCallsBefore, await AiCallCountAsync(owner.UserId));
        var last = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(first.PendingReason, last.PendingReason);
        Assert.Equal(first.Issues, last.Issues);
        Assert.Equal(first.UpdatedAt, last.UpdatedAt);
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
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
            new StartNoteProcessingRequest(material.NoteId, material.VersionId,
                new NoteProcessingConsent("native-note-processing", "FullVersion", "", true,
                    true, Guid.NewGuid())))).StatusCode);
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

        Assert.Equal(NoteProcessingAuthorizationSource.Standing,
            status.Authorization?.Source);
        Assert.NotNull(status.Authorization?.StandingAuthorizationRevision);
        Assert.NotNull(status.Authorization?.AuthorizationRevision);
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
    public async Task TargetDriftDoesNotRegressCreatedTasksToPendingAcrossDispatch()
    {
        var owner = await OwnerAsync("dispatch-target-drift");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: true);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(started.Asr.TaskCreated);
        Assert.True(started.Derived.TaskCreated);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id);
            profile.Revision = Guid.NewGuid();
            profile.Model += "-changed-after-start";
            await db.SaveChangesAsync();
        }
        var driftedPreferences = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        Assert.False(driftedPreferences.StandingAuthorizationValid);

        var store = host.Factory.Services.GetRequiredService<NoteProcessingStore>();
        var before = await store.ReadAsync(owner.UserId, started.ProcessingId, default);
        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();
        await dispatch.RunAsync(default);
        await dispatch.RunAsync(default);

        var after = await store.ReadAsync(owner.UserId, started.ProcessingId, default);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Null(after.PendingReason);
        Assert.Equal(started.Asr.TaskId, after.AsrTaskId);
        Assert.Equal(started.Derived.TaskId, after.DerivedTaskId);
        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.NotEqual(NoteProcessingState.PendingProcessing, status.State);
        Assert.True(status.Asr.TaskCreated);
        Assert.True(status.Derived.TaskCreated);
        Assert.Equal(started.Asr.TaskId, status.Asr.TaskId);
        Assert.Equal(started.Derived.TaskId, status.Derived.TaskId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id);
            profile.Revision = defaults.Derived.Revision;
            profile.Model = defaults.Derived.Model;
            await db.SaveChangesAsync();
        }
        await MarkAsrSucceededWithSyntheticReceiptsAsync(owner.UserId, started.Asr.TaskId!.Value,
            material);
        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(
            owner.UserId, started.Derived.TaskId!.Value, CancellationToken.None);
        var completed = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingState.Completed, completed.State);

        var completedRecord = await store.ReadAsync(owner.UserId, started.ProcessingId, default);
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id);
            profile.Revision = Guid.NewGuid();
            profile.Model += "-changed-after-completion";
            await db.SaveChangesAsync();
        }
        await dispatch.RunAsync(default);
        var stillCompleted = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Equal(NoteProcessingState.Completed, stillCompleted.State);
        Assert.Equal(started.Asr.TaskId, stillCompleted.Asr.TaskId);
        Assert.Equal(started.Derived.TaskId, stillCompleted.Derived.TaskId);
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(completedRecord.Revision,
            (await store.ReadAsync(owner.UserId, started.ProcessingId, default)).Revision);
    }

    [Fact]
    public async Task RoutedAsrTaskWithMissingAttemptIsRecoveredByAutomaticDispatch()
    {
        var owner = await OwnerAsync("routed-attempt-recovery");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        _ = await ConfigureAsync(client, auto: true, cloudRoute: true);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(started.AutoStarted);
        Assert.True(started.Asr.TaskCreated);
        Assert.True(started.Derived.TaskCreated);
        var asrBefore = await AsrTaskAsync(owner.UserId, started.Asr.TaskId!.Value);
        Assert.NotNull(asrBefore.Route);
        Assert.NotNull(asrBefore.AttemptId);
        var processingRevision = (await host.Factory.Services
            .GetRequiredService<NoteProcessingStore>()
            .ReadAsync(owner.UserId, started.ProcessingId, default)).Revision;

        // Crash window: the routed task and its authorization exist, but the durable attempt insert
        // never landed. The compensation scan must recreate exactly that attempt.
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.NoteAsrAttempts.RemoveRange(await db.NoteAsrAttempts.Where(x =>
                x.OwnerId == owner.UserId && x.TaskId == started.Asr.TaskId!.Value)
                .ToListAsync());
            await db.SaveChangesAsync();
        }
        await AsrStore().UpdateAsync(owner.UserId, started.Asr.TaskId!.Value, state =>
        {
            state.AttemptId = null;
        }, CancellationToken.None);
        Assert.Equal(0, await AttemptCountAsync(owner.UserId, started.Asr.TaskId!.Value));

        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();
        await dispatch.RunAsync(default);

        var recovered = await AsrTaskAsync(owner.UserId, started.Asr.TaskId!.Value);
        Assert.NotNull(recovered.AttemptId);
        Assert.Equal(1, await AttemptCountAsync(owner.UserId, started.Asr.TaskId!.Value));
        var status = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(status.Asr.TaskCreated);
        Assert.True(status.Derived.TaskCreated);
        Assert.Equal(started.Asr.TaskId, status.Asr.TaskId);
        Assert.Equal(started.Derived.TaskId, status.Derived.TaskId);
        Assert.NotEqual(NoteProcessingState.PendingProcessing, status.State);
        Assert.DoesNotContain(status.Issues, issue => issue.Code == "asr_attempt_recovery");
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(processingRevision,
            (await host.Factory.Services.GetRequiredService<NoteProcessingStore>()
                .ReadAsync(owner.UserId, started.ProcessingId, default)).Revision);
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));

        // A second scan must not mint another chain or attempt.
        await dispatch.RunAsync(default);
        await dispatch.RunAsync(default);
        Assert.Equal(1, await AttemptCountAsync(owner.UserId, started.Asr.TaskId!.Value));
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal((await AsrTaskAsync(owner.UserId, started.Asr.TaskId!.Value)).Revision,
            recovered.Revision);
    }

    [Fact]
    public async Task RoutedAsrTaskWithUnrecoverableAttemptStaysVisibleAndRetryable()
    {
        var owner = await OwnerAsync("routed-attempt-blocked");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true, cloudRoute: true);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.NotNull(started.Asr.TaskId);

        // Ambiguous checkpoint: the attempt is gone but a durable ASR call exists, so the attempt
        // must not be minted again. The material keeps its created chains and says why.
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.NoteAsrAttempts.RemoveRange(await db.NoteAsrAttempts.Where(x =>
                x.OwnerId == owner.UserId && x.TaskId == started.Asr.TaskId!.Value)
                .ToListAsync());
            db.AiRequestLogs.Add(new AiRequestLogEntity
            {
                ClientRequestId = Guid.NewGuid(),
                UserId = owner.UserId,
                Module = "notes",
                Purpose = "ai.asr-call",
                SourceObjectType = "native-note-asr-task",
                SourceObjectId = started.Asr.TaskId!.Value.ToString("D"),
                Provider = "synthetic",
                Model = models.Asr.Model,
                CorrelationId = "synthetic-stale-asr",
                Status = "timed_out",
                AttemptNumber = 1,
                MaxAttempts = 1
            });
            await db.SaveChangesAsync();
        }
        await AsrStore().UpdateAsync(owner.UserId, started.Asr.TaskId!.Value, state =>
        {
            state.AttemptId = null;
        }, CancellationToken.None);
        var callsBefore = await AiCallCountAsync(owner.UserId);

        var dispatch = host.Factory.Services.GetRequiredService<NoteProcessingDispatchJob>();
        await dispatch.RunAsync(default);

        var blocked = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.True(blocked.Asr.TaskCreated);
        Assert.True(blocked.Derived.TaskCreated);
        Assert.Equal(started.Asr.TaskId, blocked.Asr.TaskId);
        Assert.NotEqual(NoteProcessingState.PendingProcessing, blocked.State);
        Assert.Contains(blocked.Issues, issue => issue.Code == "asr_attempt_recovery" &&
            issue.Task == NoteProcessingTaskKind.Asr);
        Assert.Equal(0, await AttemptCountAsync(owner.UserId, started.Asr.TaskId!.Value));
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(callsBefore, await AiCallCountAsync(owner.UserId));

        // The refusal is retried by later compensation scans and stays visible instead of
        // silently creating a second chain or attempting a paid call.
        await dispatch.RunAsync(default);
        var stillBlocked = await StatusAsync(client, material.NoteId, material.VersionId);
        Assert.Contains(stillBlocked.Issues, issue => issue.Code == "asr_attempt_recovery" &&
            issue.Task == NoteProcessingTaskKind.Asr);
        Assert.Equal(0, await AttemptCountAsync(owner.UserId, started.Asr.TaskId!.Value));
        Assert.Equal((Asr: 1, Derived: 1), TaskCounts(owner));
        Assert.Equal(callsBefore, await AiCallCountAsync(owner.UserId));
    }

    [Fact]
    public async Task UnconfiguredRouteWithMimoGlobalModelShowsTheEffectiveMultimodalRoute()
    {
        var owner = await OwnerAsync("unconfigured-route-mimo");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        try
        {
            // No route config row at all: the legacy shape, where only the global transcription
            // preset decides whether the audio goes to dedicated ASR or the multimodal route.
            using (var scope = host.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
                Assert.Empty(await db.NoteAsrRouteConfigs.Where(x =>
                    x.OwnerId == owner.UserId).ToListAsync());
            }

            var models = await Data<IReadOnlyList<AiModelDto>>(
                await client.GetAsync("/api/v1/ai/models"));
            var multimodal = await SaveOrResetModelAsync(client, models,
                "多模态无路由 note-processing", "http://multimodal-unrouted.invalid/v1/",
                "mimo-v2.6-flash", [AiModelCapability.Transcription], 1024,
                "synthetic-multimodal-unrouted-key", "mimo-multimodal");
            var settings = await Data<AiModelSettingsDto>(
                await client.GetAsync("/api/v1/ai/model-settings"));
            _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
                "/api/v1/ai/model-settings",
                new SaveAiModelSettingsRequest(settings.Revision, true, defaults.Derived.Id,
                    defaults.Derived.Id, multimodal.Id)));

            var preferences = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.Contains(preferences.DefaultTargets, value =>
                value.Contains(NoteAsrRoute.CloudMultimodal, StringComparison.Ordinal) &&
                value.Contains("多模态无路由 note-processing", StringComparison.Ordinal));
            Assert.DoesNotContain(preferences.DefaultTargets, value =>
                value.Contains(NoteAsrRoute.CloudAsr, StringComparison.Ordinal));

            // Same source as the route settings' effective-route resolution.
            var routeConfig = await Data<NoteAsrRouteConfigDto>(
                await client.GetAsync("/api/v1/native-notes/asr-routing/config"));
            Assert.Null(routeConfig.CloudModelId);
            Assert.Equal(NoteAsrRoute.CloudMultimodal, routeConfig.EffectiveCloudRoute);

            var material = await CompleteMaterialAsync(owner, client, withAudio: true);
            var plan = await PlanAsync(client, material);
            var asr = Assert.Single(plan.Targets.Where(target =>
                target.Kind == NoteProcessingTaskKind.Asr));
            Assert.Equal(multimodal.Id, asr.ModelId);

            // The legacy no-route-config shape still has no route consent on the task, but the
            // consent target is the multimodal profile the target policy reports.
            using var asrScope = host.Factory.Services.CreateScope();
            var asrService = asrScope.ServiceProvider.GetRequiredService<NoteAsrService>();
            var materials = asrScope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
            var version = await materials.GetAsync(owner.UserId, material.VersionId,
                CancellationToken.None);
            var request = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
                version, confirmed: false, CancellationToken.None);
            Assert.Null(request.Route);
            Assert.Equal(multimodal.Id, request.Consent?.ModelId);
            Assert.Equal("mimo-multimodal", request.Consent?.PresetId);
        }
        finally
        {
            var restored = await Data<AiModelSettingsDto>(
                await client.GetAsync("/api/v1/ai/model-settings"));
            _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
                "/api/v1/ai/model-settings",
                new SaveAiModelSettingsRequest(restored.Revision, restored.Enabled,
                    defaults.Derived.Id, defaults.Derived.Id, defaults.Asr.Id)
                {
                    NoteOrganizationModelId = Guid.Empty
                }));
        }
    }

    [Fact]
    public async Task EffectiveAsrRouteMatchesGlobalTranscriptionPresetInTargetPolicy()
    {
        var owner = await OwnerAsync("effective-route-drift");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        try
        {
            var current = await Data<NoteAsrRouteConfigDto>(
                await client.GetAsync("/api/v1/native-notes/asr-routing/config"));
            var route = await Data<NoteAsrRouteConfigDto>(await client.PutAsJsonAsync(
                "/api/v1/native-notes/asr-routing/config",
                new UpdateNoteAsrRouteConfigRequest(current.Revision, NoteAsrRoute.CloudAsr,
                    null, null, null, "synthetic-cloud-consent-v1")));
            Assert.Null(route.CloudModelId);
            Assert.Equal(NoteAsrRoute.CloudAsr, route.DefaultRoute);

            var models = await Data<IReadOnlyList<AiModelDto>>(
                await client.GetAsync("/api/v1/ai/models"));
            var multimodal = await SaveOrResetModelAsync(client, models,
                "多模态默认 note-processing", "http://multimodal.invalid/v1/",
                "mimo-v2.6-flash", [AiModelCapability.Transcription], 1024,
                "synthetic-multimodal-key", "mimo-multimodal");
            var settings = await Data<AiModelSettingsDto>(
                await client.GetAsync("/api/v1/ai/model-settings"));
            _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
                "/api/v1/ai/model-settings",
                new SaveAiModelSettingsRequest(settings.Revision, true, defaults.Derived.Id,
                    defaults.Derived.Id, multimodal.Id)));
            var replannedRoute = await Data<NoteAsrRouteConfigDto>(
                await client.GetAsync("/api/v1/native-notes/asr-routing/config"));
            Assert.Null(replannedRoute.CloudModelId);
            Assert.Equal(NoteAsrRoute.CloudMultimodal, replannedRoute.DefaultRoute);
            Assert.Equal(NoteAsrRoute.CloudMultimodal, replannedRoute.EffectiveCloudRoute);

            var preferences = await Data<NoteProcessingPreferencesDto>(
                await client.GetAsync(Processing + "/preferences"));
            Assert.Contains(preferences.DefaultTargets, value =>
                value.Contains("多模态默认 note-processing", StringComparison.Ordinal) &&
                value.Contains(NoteAsrRoute.CloudMultimodal, StringComparison.Ordinal));

            var material = await CompleteMaterialAsync(owner, client, withAudio: true);
            var plan = await PlanAsync(client, material);
            var asr = Assert.Single(plan.Targets.Where(target =>
                target.Kind == NoteProcessingTaskKind.Asr));
            Assert.Equal(NoteAsrRoute.CloudMultimodal, asr.Route);
            Assert.Equal(multimodal.Id, asr.ModelId);
        }
        finally
        {
            var restored = await Data<AiModelSettingsDto>(
                await client.GetAsync("/api/v1/ai/model-settings"));
            _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
                "/api/v1/ai/model-settings",
                new SaveAiModelSettingsRequest(restored.Revision, restored.Enabled,
                    defaults.Derived.Id, defaults.Derived.Id, defaults.Asr.Id)
                {
                    NoteOrganizationModelId = Guid.Empty
                }));
        }
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
        var replay = await StartAsync(client, material);
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
            version, confirmed: true, CancellationToken.None);
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
            version, confirmed: true, CancellationToken.None);
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
    public async Task StoppedUnboundAttemptIsBlockedInsteadOfBeingReboundAndDispatched()
    {
        var owner = await OwnerAsync("asr-stopped-unbound");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrStore = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var request = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, confirmed: true, CancellationToken.None);
        var started = await asrService.StartForOwnerAsync(owner.UserId, request,
            CancellationToken.None);
        Assert.NotNull(started.AttemptId);
        var attemptId = started.AttemptId!.Value;
        await asrStore.UpdateAsync(owner.UserId, request.TaskId, state =>
        {
            state.AttemptId = null;
        }, CancellationToken.None);
        var attempt = await db.NoteAsrAttempts.SingleAsync(x =>
            x.Id == attemptId && x.OwnerId == owner.UserId);
        attempt.State = "Failed";
        attempt.StopRequested = true;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            asrService.EnsureAttemptForOwnerAsync(owner.UserId, request.TaskId,
                request.VersionId, CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        var blocked = await asrStore.ReadAsync(owner.UserId, request.TaskId,
            CancellationToken.None);
        Assert.All(blocked.Steps, step => Assert.Equal("Blocked", step.Current.Status));
        Assert.Equal(1, await AttemptCountAsync(owner.UserId, request.TaskId));
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task BoundTerminalAttemptIsNotDispatchedOrReused()
    {
        var owner = await OwnerAsync("asr-stopped-bound");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var asrJob = scope.ServiceProvider.GetRequiredService<NoteAsrJob>();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var request = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, confirmed: true, CancellationToken.None);
        var started = await asrService.StartForOwnerAsync(owner.UserId, request,
            CancellationToken.None);
        var attempt = await db.NoteAsrAttempts.SingleAsync(x =>
            x.Id == started.AttemptId && x.OwnerId == owner.UserId);
        attempt.State = "Failed";
        attempt.StopRequested = true;
        await db.SaveChangesAsync();
        host.Models.Reset();

        await asrJob.RunAsync(owner.UserId, request.TaskId, CancellationToken.None);

        Assert.Equal(0, host.Models.Calls);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            asrService.EnsureAttemptForOwnerAsync(owner.UserId, request.TaskId,
                request.VersionId, CancellationToken.None));
        Assert.Equal(40901, error.ErrorCode);
        var blocked = await host.Factory.Services.GetRequiredService<NoteAsrTaskStore>()
            .ReadAsync(owner.UserId, request.TaskId, CancellationToken.None);
        Assert.All(blocked.Steps, step => Assert.Equal("Blocked", step.Current.Status));
    }

    [Fact]
    public async Task CloudRouteConsentDriftRejectsExistingAttemptAndRetryWithoutProvider()
    {
        var owner = await OwnerAsync("asr-route-drift-existing");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        host.Models.Reset();

        using var scope = host.Factory.Services.CreateScope();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var started = await StartAsync(client, material);
        var taskId = started.Asr.TaskId!.Value;
        var before = await AsrTaskAsync(owner.UserId, taskId);
        var attemptId = Assert.IsType<Guid>(before.AttemptId);

        using (var configScope = host.Factory.Services.CreateScope())
        {
            var configDb = configScope.ServiceProvider.GetRequiredService<PimDbContext>();
            var config = await configDb.NoteAsrRouteConfigs.SingleAsync(x =>
                x.OwnerId == owner.UserId);
            config.ConsentVersion = "synthetic-cloud-consent-v2";
            await configDb.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            asrService.EnsureAttemptForOwnerAsync(owner.UserId, taskId,
                before.Request.VersionId, CancellationToken.None));
        Assert.Equal(40901, error.ErrorCode);

        var revoked = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Failed", revoked.State);
        Assert.True(revoked.StopRequested);
        Assert.Equal("route_consent_drift", revoked.LastErrorCode);
        var afterEnsure = await AsrTaskAsync(owner.UserId, taskId);
        Assert.True(afterEnsure.StopRequested);
        Assert.All(afterEnsure.Steps, step => Assert.Equal("Blocked", step.Current.Status));

        var rejected = await client.PostAsJsonAsync(
            $"/api/v1/native-notes/asr-tasks/{taskId}/retry",
            new RetryNoteAsrRequest(Guid.NewGuid(), owner.UserId, afterEnsure.Revision, [0],
                afterEnsure.Request.Consent, true));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var afterRetry = await AsrTaskAsync(owner.UserId, taskId);
        Assert.Equal(afterEnsure.Revision, afterRetry.Revision);
        Assert.Empty(afterRetry.Retries);
        Assert.Empty(afterRetry.RetryIntents);
        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
    }

    [Fact]
    public async Task CloudRouteConsentDriftAfterJobEntryStopsBeforeProviderCall()
    {
        var owner = await OwnerAsync("asr-route-drift-job");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        host.Models.Reset();
        var started = await StartAsync(client, material);
        var taskId = started.Asr.TaskId!.Value;
        var before = await AsrTaskAsync(owner.UserId, taskId);
        var attemptId = Assert.IsType<Guid>(before.AttemptId);

        using var scope = host.Factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<NoteAsrJob>();
        job.BeforeProviderCallForTests = async ct =>
        {
            using var configScope = host.Factory.Services.CreateScope();
            var configDb = configScope.ServiceProvider.GetRequiredService<PimDbContext>();
            var config = await configDb.NoteAsrRouteConfigs.SingleAsync(x =>
                x.OwnerId == owner.UserId, ct);
            config.ConsentVersion = "synthetic-cloud-consent-v2";
            await configDb.SaveChangesAsync(ct);
        };
        try
        {
            await job.RunAsync(owner.UserId, taskId, CancellationToken.None);
        }
        finally
        {
            job.BeforeProviderCallForTests = null;
        }

        Assert.Equal(0, host.Models.Calls);
        Assert.Equal(0, await AiCallCountAsync(owner.UserId));
        using var verifyScope = host.Factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var attempt = await db.NoteAsrAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Failed", attempt.State);
        Assert.True(attempt.StopRequested);
        Assert.Equal("route_consent_drift", attempt.LastErrorCode);
        var task = await AsrTaskAsync(owner.UserId, taskId);
        Assert.True(task.StopRequested);
        Assert.Equal("Blocked", task.Steps[0].Current.Status);
    }

    [Fact]
    public async Task StoppingTaskAlsoStopsAnUnboundQueuedAttempt()
    {
        var owner = await OwnerAsync("asr-stop-orphan");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: false);
        _ = await ConfigureCloudRouteAsync(client, models.Asr.Id);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);

        using var scope = host.Factory.Services.CreateScope();
        var asrService = scope.ServiceProvider.GetRequiredService<NoteAsrService>();
        var materials = scope.ServiceProvider.GetRequiredService<NoteMaterialUploadStore>();
        var asrStore = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var version = await materials.GetAsync(owner.UserId, material.VersionId,
            CancellationToken.None);
        var request = await asrService.BuildDefaultRequestForProcessingAsync(owner.UserId,
            version, confirmed: true, CancellationToken.None);
        var started = await asrService.StartForOwnerAsync(owner.UserId, request,
            CancellationToken.None);
        var attemptId = Assert.IsType<Guid>(started.AttemptId);
        await asrStore.UpdateAsync(owner.UserId, request.TaskId, state =>
        {
            state.AttemptId = null;
        }, CancellationToken.None);

        _ = await Data<NoteAsrTaskDto>(await client.PostAsync(
            $"/api/v1/native-notes/asr-tasks/{request.TaskId}/stop", null));

        var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x =>
            x.Id == attemptId && x.OwnerId == owner.UserId);
        Assert.True(attempt.StopRequested);
        Assert.Equal("Unknown", attempt.State);
        var stopped = await asrStore.ReadAsync(owner.UserId, request.TaskId,
            CancellationToken.None);
        Assert.True(stopped.StopRequested);
        Assert.Null(stopped.AttemptId);
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task ChangedAsrSegmentIdentityIsBlockedBeforeAttemptCreation()
    {
        var owner = await OwnerAsync("asr-segment-identity");
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
            version, confirmed: true, CancellationToken.None);
        await asrStore.CreateAsync(owner.UserId, request, version,
            NoteAsrPlan.Build(version), request.Route, request.Route?.ExecutionMode,
            NoteAsrPlan.SamplesPerCall, CancellationToken.None);
        await asrStore.UpdateAsync(owner.UserId, request.TaskId, state =>
        {
            state.Steps[0].SourceOffset++;
        }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            asrService.EnsureAttemptForOwnerAsync(owner.UserId, request.TaskId,
                request.VersionId, CancellationToken.None));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Equal(0, await AttemptCountAsync(owner.UserId, request.TaskId));
        var blocked = await asrStore.ReadAsync(owner.UserId, request.TaskId,
            CancellationToken.None);
        Assert.All(blocked.Steps, step => Assert.Equal("Blocked", step.Current.Status));
    }

    [Fact]
    public async Task LegacyDerivedPayloadWithoutAwaitingAsrMigratesToWaitingWithoutEmptyTranscriptSuccess()
    {
        var owner = await OwnerAsync("legacy-derived-wait");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true, cloudRoute: true);
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
    public async Task StoppedLegacyDerivedPayloadCannotAppearSucceededWithEmptyTranscripts()
    {
        var owner = await OwnerAsync("legacy-derived-stopped");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true, cloudRoute: true);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        var taskId = started.Derived.TaskId!.Value;
        await RemoveAwaitingAsrPropertyAsync(owner.UserId, taskId);
        await DerivedStore().UpdateTaskAsync(owner.UserId, taskId, state =>
        {
            state.StopRequested = true;
            foreach (var step in state.Steps)
                step.Current.Status = "Succeeded";
        }, CancellationToken.None);

        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(
            owner.UserId, taskId, CancellationToken.None);

        var task = await DerivedTaskAsync(owner.UserId, taskId);
        Assert.True(task.StopRequested);
        Assert.True(task.Blocked);
        Assert.Equal("Stopped", NoteDerivedStore.DescribePhase(task));
        Assert.Empty(task.Plan.Input.Transcripts);
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task StoppedLegacyDerivedPayloadWithoutPlanIsVisibleAsStopped()
    {
        var owner = await OwnerAsync("legacy-derived-no-plan");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true, cloudRoute: true);
        host.Models.Reset();
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        var started = await StatusAsync(client, material.NoteId, material.VersionId);
        var taskId = started.Derived.TaskId!.Value;
        await RemoveAwaitingAsrPropertyAsync(owner.UserId, taskId);
        await DerivedStore().UpdateTaskAsync(owner.UserId, taskId, state =>
        {
            state.Plan = null!;
            state.StopRequested = true;
            foreach (var step in state.Steps)
                step.Current.Status = "Succeeded";
        }, CancellationToken.None);

        await host.Factory.Services.GetRequiredService<NoteDerivedJob>().RunAsync(
            owner.UserId, taskId, CancellationToken.None);

        var task = await DerivedTaskAsync(owner.UserId, taskId);
        Assert.True(task.StopRequested);
        Assert.True(task.Blocked);
        Assert.Equal("Stopped", NoteDerivedStore.DescribePhase(task));
        Assert.Null(task.Plan);
        Assert.Equal(0, host.Models.Calls);
    }

    [Fact]
    public async Task AsrStoppedBeforeDerivedMaterializationBlocksWithoutEmptyTranscriptSuccess()
    {
        var owner = await OwnerAsync("asr-stopped-block");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var models = await ConfigureAsync(client, auto: true, cloudRoute: true);
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
        var models = await ConfigureAsync(client, auto: true, cloudRoute: true);
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
        var models = await ConfigureAsync(client, auto: true, cloudRoute: true);
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
        _ = await ConfigureAsync(client, auto: false);
        var preferences = await Data<NoteProcessingPreferencesDto>(
            await client.GetAsync(Processing + "/preferences"));
        var saved = await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(preferences.Revision, true,
                ConfirmStandingAuthorization: true, AcknowledgePossibleCharge: true,
                DefaultTargetRevision: preferences.CurrentDefaultTargetRevision)));
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

    [Fact]
    public async Task TargetPolicyDriftBetweenPreferencesAndPlanAbortsBeforeCreatingPaidTasks()
    {
        var owner = await OwnerAsync("target-policy-plan-race");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var defaults = await ConfigureAsync(client, auto: false);
        var material = await CompleteMaterialAsync(owner, client, withAudio: true);
        _ = await SetAutoProcessingAsync(client, true);

        var service = host.Factory.Services.GetRequiredService<NoteProcessingService>();
        service.BeforePlanResolutionForTests = async ct =>
        {
            using var scope = host.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == defaults.Derived.Id, ct);
            profile.Revision = Guid.NewGuid();
            profile.Model += "-changed-during-plan";
            await db.SaveChangesAsync(ct);
        };
        try
        {
            var status = await service.TryAutoStartAfterCompletionAsync(owner.UserId,
                material.VersionId, CancellationToken.None);
            Assert.NotNull(status);
            Assert.False(status!.Asr.TaskCreated);
            Assert.False(status.Derived.TaskCreated);
            Assert.Contains("生成处理计划时发生变化", status.PendingReason);
            Assert.Equal((Asr: 0, Derived: 0), TaskCounts(owner));
            Assert.Equal(0, host.Models.Calls);
        }
        finally
        {
            service.BeforePlanResolutionForTests = null;
        }
    }

    [Fact]
    public async Task OrdinaryUserPlanAndStatusHideTargetEndpointsWhileAdminKeepsDiagnostics()
    {
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "endpoint-admin-" + Guid.NewGuid().ToString("N"), "admin");
        using var adminClient = AuthRegistrationTestHost.Authed(host.Factory, admin.AccessToken);
        _ = await ConfigureAsync(adminClient, auto: false);

        var user = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "endpoint-user-" + Guid.NewGuid().ToString("N"), "user");
        using var userClient = AuthRegistrationTestHost.Authed(host.Factory, user.AccessToken);
        var userMaterial = await CompleteMaterialAsync(user, userClient, withAudio: true);
        var userPlan = await PlanAsync(userClient, userMaterial);
        Assert.NotEmpty(userPlan.Targets);
        Assert.All(userPlan.Targets, target => Assert.Null(target.Endpoint));
        var userStarted = await StartAsync(userClient, userMaterial);
        Assert.NotNull(userStarted.Authorization);
        Assert.All(userStarted.Authorization!.Targets, target => Assert.Null(target.Endpoint));

        var adminMaterial = await CompleteMaterialAsync(admin, adminClient, withAudio: true);
        var adminPlan = await PlanAsync(adminClient, adminMaterial);
        Assert.Contains(adminPlan.Targets, target => !string.IsNullOrWhiteSpace(target.Endpoint));
    }

    private async Task<AuthRegistrationTestHost.Session> OwnerAsync(string prefix) =>
        await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            $"{prefix}-{Guid.NewGuid():N}", "admin");

    private async Task<(AiModelDto Asr, AiModelDto Derived)> ConfigureAsync(
        HttpClient client, bool auto, bool cloudRoute = false)
    {
        var models = await Data<IReadOnlyList<AiModelDto>>(
            await client.GetAsync("/api/v1/ai/models"));
        var asr = await SaveOrResetModelAsync(client, models, "ASR note-processing",
            "http://asr.invalid/v1/", "mimo-v2.5-asr", [AiModelCapability.Transcription],
            1024, "synthetic-asr-key", "mimo-asr");
        var derived = await SaveOrResetModelAsync(client, models, "整理 note-processing",
            "http://derived.invalid/v1/", "synthetic-text",
            [AiModelCapability.Text, AiModelCapability.Vision], 4096,
            "synthetic-derived-key", null);
        var settings = await Data<AiModelSettingsDto>(
            await client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
            "/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, true, derived.Id, derived.Id,
                asr.Id)));
        if (cloudRoute) _ = await ConfigureCloudRouteAsync(client, asr.Id);
        _ = await SetAutoProcessingAsync(client, auto);
        return (asr, derived);
    }

    private static async Task<AiModelDto> SaveOrResetModelAsync(HttpClient client,
        IReadOnlyList<AiModelDto> models, string name, string baseUrl, string model,
        IReadOnlyList<AiModelCapability> capabilities, int maxOutputTokens, string apiKey,
        string? presetId)
    {
        var existing = models.FirstOrDefault(candidate => candidate.Name == name);
        var request = new SaveAiModelRequest(name, baseUrl, model, true, capabilities, 30,
            maxOutputTokens, apiKey, existing?.Revision, presetId);
        return existing is null
            ? await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models", request))
            : await Data<AiModelDto>(await client.PutAsJsonAsync(
                $"/api/v1/ai/models/{existing.Id}", request));
    }

    private static async Task<AiModelDto> SaveOrganizationProfileAsync(HttpClient client,
        string name, string baseUrl, string model)
    {
        var models = await Data<IReadOnlyList<AiModelDto>>(
            await client.GetAsync("/api/v1/ai/models"));
        return await SaveOrResetModelAsync(client, models, name, baseUrl, model,
            [AiModelCapability.Text, AiModelCapability.Vision], 4096,
            $"synthetic-{model}-key", null);
    }

    private async Task SetOrganizationAvailabilityAsync(Guid modelId, bool enabled,
        int? capabilities = null)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var profile = await db.AiModelProfiles.SingleAsync(x => x.Id == modelId);
        profile.Enabled = enabled;
        if (capabilities is { } value) profile.Capabilities = value;
        await db.SaveChangesAsync();
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
        var confirm = enabled && !current.StandingAuthorizationValid;
        return await Data<NoteProcessingPreferencesDto>(await client.PutAsJsonAsync(
            Processing + "/preferences",
            new SaveNoteProcessingPreferencesRequest(current.Revision, enabled,
                ConfirmStandingAuthorization: confirm,
                AcknowledgePossibleCharge: confirm,
                DefaultTargetRevision: enabled
                    ? current.CurrentDefaultTargetRevision
                    : null)));
    }

    private static async Task SetNoteOrganizationDefaultAsync(HttpClient client, Guid modelId)
    {
        var settings = await Data<AiModelSettingsDto>(
            await client.GetAsync("/api/v1/ai/model-settings"));
        _ = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync(
            "/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, settings.Enabled,
                settings.TextModelId, settings.VisionModelId, settings.TranscriptionModelId)
            {
                NoteOrganizationModelId = modelId
            }));
    }

    private static async Task<NoteProcessingStatusDto> StartAsync(HttpClient client,
        NoteDerivedFixture.Material material)
    {
        var plan = await PlanAsync(client, material);
        return await Data<NoteProcessingStatusDto>(await client.PostAsJsonAsync(
            Processing + "/start", new StartNoteProcessingRequest(material.NoteId,
                material.VersionId, Consent(plan))));
    }

    private static async Task<NoteProcessingPlanDto> PlanAsync(HttpClient client,
        NoteDerivedFixture.Material material) =>
        await Data<NoteProcessingPlanDto>(await client.GetAsync(
            $"{Processing}/plan?noteId={material.NoteId}&versionId={material.VersionId}"));

    private static NoteProcessingConsent Consent(NoteProcessingPlanDto plan) =>
        new(plan.Purpose, plan.MaterialScope, plan.CostNotice,
            PossibleChargeAcknowledged: true, Confirmed: true,
            plan.AuthorizationRevision, plan.StandingAuthorizationRevision);

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

    private async Task AddLegacyDuplicateAsync(Guid owner, Guid sourceId, Guid duplicateId,
        Guid versionId, bool derived, int minuteOffset = 5, bool clearChain = true)
    {
        var ownerFolder = Path.Combine(host.NativeNotesRoot,
            derived ? "derived" : "asr-tasks", owner.ToString("D"));
        var taskFolder = derived ? Path.Combine(ownerFolder, "tasks") : ownerFolder;
        var sourcePath = Path.Combine(taskFolder, sourceId.ToString("D") + ".json");
        var duplicatePath = Path.Combine(taskFolder, duplicateId.ToString("D") + ".json");
        var duplicate = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath))!.AsObject();
        var createdAt = DateTimeOffset.Parse(duplicate["createdAt"]!.GetValue<string>());
        duplicate["createdAt"] = createdAt.AddMinutes(minuteOffset).ToString("O");
        duplicate["request"]!.AsObject()["taskId"] = duplicateId.ToString("D");
        if (!derived) duplicate["attemptId"] = null;
        await File.WriteAllTextAsync(duplicatePath, duplicate.ToJsonString(Json));

        if (clearChain)
        {
            var chainPath = Path.Combine(ownerFolder, "chains", versionId.ToString("D") + ".json");
            File.Delete(chainPath);
        }
    }

    private static NoteDerivedFixture.Material CreateNextMaterial(
        NoteDerivedFixture.Material source)
    {
        var versionId = Guid.NewGuid();
        var input = JsonNode.Parse(source.Request.InputJson)!.AsObject();
        input["id"] = versionId.ToString("D");
        var inputJson = input.ToJsonString(Json);
        var manifest = JsonNode.Parse(source.Request.ManifestJson)!.AsObject();
        manifest["versionId"] = versionId.ToString("D");
        manifest["inputSha256"] = Digest(Encoding.UTF8.GetBytes(inputJson));
        var manifestJson = manifest.ToJsonString(Json);
        return new(new BeginNoteMaterialUploadRequest(versionId, source.NoteId,
                source.VersionId, inputJson, manifestJson, true),
            source.Content.Select(bytes => bytes.ToArray()).ToList(), source.PhotoIndex,
            source.InkIndex, versionId, source.NoteId, source.RecordingId, source.Markdown);
    }

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
