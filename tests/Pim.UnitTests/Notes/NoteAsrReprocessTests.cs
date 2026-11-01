using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pim.Api.Endpoints;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteAsrReprocessTests : IAsyncLifetime
{
    private const string Route = "/api/v1/native-notes/asr-tasks";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-note-reprocess", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Guid _owner;
    private NoteAsrRouteConfigDto _routeConfig = null!;

    public async Task InitializeAsync()
    {
        _factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("note-reprocess"))
            .WithWebHostBuilder(b => b.UseEnvironment("Test")
                .UseSetting("NativeNotes:StorageRoot", Path.Combine(_root, "materials"))
                .UseSetting("ManagedStorage:Root", Path.Combine(_root, "binary"))
                .UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys")));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "note-reprocess-owner", "admin");
        _owner = session.UserId;
        _client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);

        var current = await Data<NoteAsrRouteConfigDto>(await _client.GetAsync(
            "/api/v1/native-notes/asr-routing/config"));
        _routeConfig = await Data<NoteAsrRouteConfigDto>(await _client.PutAsJsonAsync(
            "/api/v1/native-notes/asr-routing/config", new UpdateNoteAsrRouteConfigRequest(
                current.Revision, NoteAsrRoute.Local, "local-model", "r1", null, "local-v1")));
    }

    [Fact]
    public async Task CompletedSourceCreatesFullNewTaskWithNewAttemptAndDoesNotRewriteSource()
    {
        var (fixture, source) = await CreateSourceAsync(SourceOutcome.Completed);
        var before = await File.ReadAllBytesAsync(TaskPath(source.TaskId));
        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);

        var result = await services.Reprocess.ReprocessAsync(source.TaskId,
            Request(Guid.NewGuid(), source.Revision, "manual", "operator requested full rerun"), default);

        Assert.Equal(source.TaskId, result.ParentTaskId);
        Assert.Equal("manual: operator requested full rerun", result.Reason);
        Assert.NotEqual(source.TaskId, result.Task.TaskId);
        Assert.Equal(fixture.Request.VersionId, result.Task.VersionId);
        Assert.Equal(source.InputSha256, result.Task.InputSha256);
        Assert.Equal(source.ManifestSha256, result.Task.ManifestSha256);
        Assert.Equal(source.TotalSegments, result.Task.TotalSegments);
        Assert.Equal(NoteAsrRoute.Local, result.Task.Route);
        Assert.Null(result.Task.Destination);
        Assert.NotNull(result.Task.AttemptId);
        Assert.NotEqual(source.AttemptId, result.Task.AttemptId);
        Assert.Equal("Queued", result.Task.Phase);

        var child = await services.Tasks.ReadAsync(_owner, result.Task.TaskId, default);
        Assert.Equal(source.TaskId, child.ParentTaskId);
        Assert.Equal("manual: operator requested full rerun", child.ReprocessReason);
        Assert.Null(child.Request.Consent);
        Assert.All(child.Steps, step => Assert.Equal("Queued", step.Current.Status));
        Assert.Equal(source.AttemptId, (await services.Tasks.ReadAsync(_owner, source.TaskId, default)).AttemptId);
        Assert.Equal(before, await File.ReadAllBytesAsync(TaskPath(source.TaskId)));
    }

    [Fact]
    public async Task LocalReprocessDoesNotInheritSourceCloudConsentAndRejectsANewOne()
    {
        var (_, source) = await CreateSourceAsync(SourceOutcome.Completed);
        var sourceState = await ReadSourceAsync(source.TaskId);
        Assert.NotNull(sourceState.Request.Consent);

        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);
        var local = Request(Guid.NewGuid(), source.Revision, "quality", "local only");
        var result = await services.Reprocess.ReprocessAsync(source.TaskId, local, default);

        Assert.Equal(NoteAsrRoute.Local, result.Task.Route);
        Assert.Null(result.Task.Destination);
        var child = await services.Tasks.ReadAsync(_owner, result.Task.TaskId, default);
        Assert.Null(child.Request.Consent);

        var withConsent = Request(Guid.NewGuid(), source.Revision, "manual", "wrong consent") with
        {
            Consent = Destination
        };
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, withConsent, default));
        Assert.Equal(40001, error.ErrorCode);
        Assert.Equal(2, services.Tasks.Enumerate(_owner).Count());
    }

    [Fact]
    public async Task ReplayIsSameTaskButDifferentReasonOrRouteIsRejected()
    {
        var (_, source) = await CreateSourceAsync(SourceOutcome.Completed);
        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);
        var clientTaskId = Guid.NewGuid();
        var request = Request(clientTaskId, source.Revision, "quality", "cover the full note again");

        var first = await services.Reprocess.ReprocessAsync(source.TaskId, request, default);
        var firstState = await services.Tasks.ReadAsync(_owner, clientTaskId, default);
        var replay = await services.Reprocess.ReprocessAsync(source.TaskId, request, default);

        Assert.Equal(first.Task.TaskId, replay.Task.TaskId);
        Assert.Equal(first.Task.AttemptId, replay.Task.AttemptId);
        Assert.Equal(firstState.Revision, (await services.Tasks.ReadAsync(_owner, clientTaskId, default)).Revision);
        Assert.Equal(2, services.Tasks.Enumerate(_owner).Count());

        var reasonMismatch = request with { Reason = new(NoteAsrReprocessReasonKind.Manual, "different") };
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(
            () => services.Reprocess.ReprocessAsync(source.TaskId, reasonMismatch, default))).ErrorCode);

        var routeMismatch = request with
        {
            Route = request.Route with { Route = NoteAsrRoute.CloudAsr },
            Consent = Destination
        };
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(
            () => services.Reprocess.ReprocessAsync(source.TaskId, routeMismatch, default))).ErrorCode);
        Assert.Equal(2, services.Tasks.Enumerate(_owner).Count());
    }

    [Theory]
    [InlineData(SourceOutcome.Failed)]
    [InlineData(SourceOutcome.Stopped)]
    public async Task FailedAndStoppedSourcesCanRunAFullNewExecution(SourceOutcome outcome)
    {
        var (_, source) = await CreateSourceAsync(outcome);
        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);

        var result = await services.Reprocess.ReprocessAsync(source.TaskId,
            Request(Guid.NewGuid(), source.Revision, "manual", "full rerun"), default);

        Assert.Equal("Queued", result.Task.Phase);
        Assert.Equal(source.TotalSegments, result.Task.TotalSegments);
        Assert.Equal(0, result.Task.CompletedSegments);
    }

    [Fact]
    public async Task RunningSourceCannotBeCopied()
    {
        var (_, source) = await CreateSourceAsync(SourceOutcome.Running);
        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId,
                Request(Guid.NewGuid(), source.Revision, "manual", "should not copy"), default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Equal(1, services.Tasks.Enumerate(_owner).Count());
    }

    [Fact]
    public async Task StaleConfigRevisionAndMissingCloudConsentAreRejectedBeforeCreation()
    {
        var (_, source) = await CreateSourceAsync(SourceOutcome.Completed);
        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);

        var stale = Request(Guid.NewGuid(), source.Revision, "manual", "stale config") with
        {
            Route = new(NoteAsrRoute.Local, Guid.NewGuid(), _routeConfig.ConsentVersion, null, true)
        };
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, stale, default))).ErrorCode);

        var noCloudConsent = Request(Guid.NewGuid(), source.Revision, "manual", "cloud") with
        {
            Route = new(NoteAsrRoute.CloudAsr, _routeConfig.Revision, _routeConfig.ConsentVersion, null, true),
            Consent = null
        };
        Assert.Equal(40001, (await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, noCloudConsent, default))).ErrorCode);
        Assert.Equal(1, services.Tasks.Enumerate(_owner).Count());
    }

    [Fact]
    public async Task HistoryIsParentBoundStableAndPageable()
    {
        var (_, source) = await CreateSourceAsync(SourceOutcome.Completed);
        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);

        var first = await services.Reprocess.ReprocessAsync(source.TaskId,
            Request(Guid.NewGuid(), source.Revision, "manual", "first pass"), default);
        var second = await services.Reprocess.ReprocessAsync(source.TaskId,
            Request(Guid.NewGuid(), source.Revision, "quality", "second pass"), default);
        var secondState = await services.Tasks.ReadAsync(_owner, second.Task.TaskId, default);
        await services.Tasks.UpdateAsync(_owner, first.Task.TaskId,
            state => state.CreatedAt = secondState.CreatedAt.AddSeconds(-10), default);

        var page = await services.Reprocess.HistoryAsync(source.TaskId, _owner, 0, 1, default);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal(second.Task.TaskId, page.Items[0].TaskId);
        Assert.Equal("quality: second pass", page.Items[0].Reason);
        Assert.DoesNotContain(page.Items, x => x.TaskId == source.TaskId || x.TaskId == first.Task.TaskId);

        var next = await services.Reprocess.HistoryAsync(source.TaskId, _owner, 1, 1, default);
        Assert.Single(next.Items);
        Assert.Equal(first.Task.TaskId, next.Items[0].TaskId);
        Assert.Equal("manual: first pass", next.Items[0].Reason);
    }

    [Fact]
    public async Task EndpointMapsReprocessRequestToActualService()
    {
        var (_, source) = await CreateSourceAsync(SourceOutcome.Completed);
        using var scope = _factory.Services.CreateScope();
        var services = NewServices(scope.ServiceProvider, _owner);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddSingleton(services.Reprocess);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNoteAsrReprocessEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync($"{Route}/{source.TaskId}/reprocess",
            Request(Guid.NewGuid(), source.Revision, "manual", "endpoint"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<NoteAsrReprocessResultDto>>();
        Assert.NotNull(envelope?.Data);
        Assert.Equal(source.TaskId, envelope.Data.ParentTaskId);
        Assert.Equal("manual: endpoint", envelope.Data.Reason);
        await app.StopAsync();
    }

    private async Task<(NoteMaterialUploadStoreTests.Fixture Fixture, NoteAsrTaskDto Source)> CreateSourceAsync(
        SourceOutcome outcome)
    {
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 61);
        await Upload(fixture);
        var start = new StartNoteAsrRequest(Guid.NewGuid(), _owner, fixture.Request.VersionId,
            Destination, "zh", new NoteAsrRouteConsent(NoteAsrRoute.Local, _routeConfig.Revision,
                _routeConfig.ConsentVersion, true));
        var started = await Data<NoteAsrTaskDto>(await _client.PostAsJsonAsync(Route, start));

        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
        var source = await tasks.UpdateAsync(_owner, started.TaskId, state =>
        {
            state.CompletionAcknowledged = outcome == SourceOutcome.Completed;
            state.StopRequested = outcome == SourceOutcome.Stopped;
            for (var index = 0; index < state.Steps.Count; index++)
            {
                state.Steps[index].Current.Status = outcome switch
                {
                    SourceOutcome.Completed => "Succeeded",
                    SourceOutcome.Failed => index == 0 ? "Failed" : "Succeeded",
                    SourceOutcome.Stopped => "Queued",
                    SourceOutcome.Running => index == 0 ? "Running" : "Queued",
                    _ => throw new ArgumentOutOfRangeException(nameof(outcome))
                };
            }
        }, default);
        return (fixture, NoteAsrTaskStore.Describe(source));
    }

    private async Task<NoteAsrState> ReadSourceAsync(Guid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
        return await tasks.ReadAsync(_owner, taskId, default);
    }

    private ReprocessNoteAsrRequest Request(Guid clientTaskId, Guid sourceRevision,
        string reasonKind, string reasonText) =>
        new(clientTaskId, _owner, sourceRevision,
            new NoteAsrReprocessReason(reasonKind, reasonText),
            new NoteAsrReprocessRoute(NoteAsrRoute.Local, _routeConfig.Revision,
                _routeConfig.ConsentVersion, NoteAsrExecutionMode.Realtime, true));

    private ReprocessServices NewServices(IServiceProvider services, Guid owner)
    {
        var db = services.GetRequiredService<PimDbContext>();
        var tasks = services.GetRequiredService<NoteAsrTaskStore>();
        var materials = services.GetRequiredService<NoteMaterialUploadStore>();
        var presets = services.GetRequiredService<AiAsrPresetRegistry>();
        var secrets = services.GetRequiredService<ISecretProtector>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var fixedUser = new FixedUser(owner);
        var routing = new NoteAsrRoutingService(db, fixedUser, materials, presets, tasks,
            new NoteAsrEvidenceService(db, tasks, fixedUser), secrets,
            Options.Create(new NoteAsrRoutingOptions()));
        var noteAsr = new NoteAsrService(tasks, materials, db, fixedUser, presets,
            configuration, secrets, routing);
        return new(new NoteAsrReprocessService(db, tasks, materials, noteAsr, fixedUser), tasks);
    }

    private async Task Upload(NoteMaterialUploadStoreTests.Fixture fixture)
    {
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await store.BeginAsync(_owner, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
            for (var position = 0; position < fixture.Content[file].Length; position += NoteMaterialUploadOptions.PartSizeBytes)
            {
                var part = fixture.Content[file].AsSpan(position,
                    Math.Min(NoteMaterialUploadOptions.PartSizeBytes, fixture.Content[file].Length - position)).ToArray();
                await store.UploadPartAsync(_owner, fixture.Request.VersionId, file,
                    position / NoteMaterialUploadOptions.PartSizeBytes,
                    Convert.ToHexString(SHA256.HashData(part)).ToLowerInvariant(), new MemoryStream(part));
            }
        await store.CompleteAsync(_owner, fixture.Request.VersionId);
    }

    private string TaskPath(Guid taskId) =>
        Path.Combine(_root, "materials", "asr-tasks", _owner.ToString("D"), taskId.ToString("D") + ".json");

    private static AiAsrDestinationConsent Destination =>
        new(Guid.NewGuid(), Guid.NewGuid(), "http://127.0.0.1/unused", "unused", "1", true);

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, response.StatusCode + ": " + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public enum SourceOutcome { Completed, Failed, Stopped, Running }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "ReprocessTestScheme";

        public TestAuthHandler(
            Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override System.Threading.Tasks.Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "reprocess-test")], SchemeName);
            return System.Threading.Tasks.Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed record ReprocessServices(NoteAsrReprocessService Reprocess, NoteAsrTaskStore Tasks);

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId { get; } = id;
        public string? Role => "admin";
    }
}
