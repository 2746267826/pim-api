using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notes;

/// <summary>
/// The Reprocess contract on a relational PostgreSQL provider. This deliberately does not reuse
/// the existing InMemory host: the dedicated database is created by TempMigrationDatabase and
/// migrated through the real EF migration chain before any API request is sent.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class NoteAsrReprocessRealDbTests
{
    private const string Route = "/api/v1/native-notes/asr-tasks";

    [SkippableFact]
    public async Task CompletedSourceCreatesFullNewTaskWithNewAttemptAndDoesNotRewriteSource()
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (fixture, source) = await host.CreateSourceAsync(SourceOutcome.Completed);
        var before = await File.ReadAllBytesAsync(host.TaskPath(source.TaskId));
        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);

        var result = await services.Reprocess.ReprocessAsync(source.TaskId,
            host.Request(Guid.NewGuid(), source.Revision, "manual", "operator requested full rerun"), default);

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

        var child = await services.Tasks.ReadAsync(host.Owner, result.Task.TaskId, default);
        Assert.Equal(source.TaskId, child.ParentTaskId);
        Assert.Equal("manual: operator requested full rerun", child.ReprocessReason);
        Assert.Null(child.Request.Consent);
        Assert.All(child.Steps, step => Assert.Equal("Queued", step.Current.Status));
        Assert.Equal(source.AttemptId,
            (await services.Tasks.ReadAsync(host.Owner, source.TaskId, default)).AttemptId);
        Assert.Equal(before, await File.ReadAllBytesAsync(host.TaskPath(source.TaskId)));
    }

    [SkippableFact]
    public async Task LocalReprocessDoesNotInheritSourceCloudConsentAndRejectsANewOne()
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (_, source) = await host.CreateSourceAsync(SourceOutcome.Completed);
        var sourceState = await host.ReadSourceAsync(source.TaskId);
        Assert.NotNull(sourceState.Request.Consent);

        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);
        var result = await services.Reprocess.ReprocessAsync(source.TaskId,
            host.Request(Guid.NewGuid(), source.Revision, "quality", "local only"), default);

        Assert.Equal(NoteAsrRoute.Local, result.Task.Route);
        Assert.Null(result.Task.Destination);
        var child = await services.Tasks.ReadAsync(host.Owner, result.Task.TaskId, default);
        Assert.Null(child.Request.Consent);

        var withConsent = host.Request(Guid.NewGuid(), source.Revision, "manual", "wrong consent") with
        {
            Consent = SourceDestination
        };
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, withConsent, default));
        Assert.Equal(40001, error.ErrorCode);
        Assert.Equal(2, services.Tasks.Enumerate(host.Owner).Count());
    }

    [SkippableFact]
    public async Task ReplayIsSameTaskButDifferentReasonOrRouteIsRejected()
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (_, source) = await host.CreateSourceAsync(SourceOutcome.Completed);
        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);
        var clientTaskId = Guid.NewGuid();
        var request = host.Request(clientTaskId, source.Revision, "quality", "cover the full note again");

        var first = await services.Reprocess.ReprocessAsync(source.TaskId, request, default);
        var firstState = await services.Tasks.ReadAsync(host.Owner, clientTaskId, default);
        var replay = await services.Reprocess.ReprocessAsync(source.TaskId, request, default);

        Assert.Equal(first.Task.TaskId, replay.Task.TaskId);
        Assert.Equal(first.Task.AttemptId, replay.Task.AttemptId);
        Assert.Equal(firstState.Revision,
            (await services.Tasks.ReadAsync(host.Owner, clientTaskId, default)).Revision);
        Assert.Equal(2, services.Tasks.Enumerate(host.Owner).Count());

        var reasonMismatch = request with
        {
            Reason = new NoteAsrReprocessReason(NoteAsrReprocessReasonKind.Manual, "different")
        };
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, reasonMismatch, default))).ErrorCode);

        var routeMismatch = request with
        {
            Route = request.Route with { Route = NoteAsrRoute.CloudAsr },
            Consent = SourceDestination
        };
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, routeMismatch, default))).ErrorCode);
        Assert.Equal(2, services.Tasks.Enumerate(host.Owner).Count());
    }

    [SkippableTheory]
    [InlineData(SourceOutcome.Failed)]
    [InlineData(SourceOutcome.Stopped)]
    public async Task FailedAndStoppedSourcesCanRunAFullNewExecution(SourceOutcome outcome)
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (_, source) = await host.CreateSourceAsync(outcome);
        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);

        var result = await services.Reprocess.ReprocessAsync(source.TaskId,
            host.Request(Guid.NewGuid(), source.Revision, "manual", "full rerun"), default);

        Assert.Equal("Queued", result.Task.Phase);
        Assert.Equal(source.TotalSegments, result.Task.TotalSegments);
        Assert.Equal(0, result.Task.CompletedSegments);
    }

    [SkippableFact]
    public async Task RunningSourceCannotBeCopied()
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (_, source) = await host.CreateSourceAsync(SourceOutcome.Running);
        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId,
                host.Request(Guid.NewGuid(), source.Revision, "manual", "should not copy"), default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Equal(1, services.Tasks.Enumerate(host.Owner).Count());
    }

    [SkippableFact]
    public async Task StaleConfigRevisionAndMissingCloudConsentAreRejectedBeforeCreation()
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (_, source) = await host.CreateSourceAsync(SourceOutcome.Completed);
        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);

        var stale = host.Request(Guid.NewGuid(), source.Revision, "manual", "stale config") with
        {
            Route = new(NoteAsrRoute.Local, Guid.NewGuid(), host.RouteConfig.ConsentVersion, null, true)
        };
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, stale, default))).ErrorCode);

        var noCloudConsent = host.Request(Guid.NewGuid(), source.Revision, "manual", "cloud") with
        {
            Route = new(NoteAsrRoute.CloudAsr, host.RouteConfig.Revision,
                host.RouteConfig.ConsentVersion, null, true),
            Consent = null
        };
        Assert.Equal(40001, (await Assert.ThrowsAsync<DomainException>(() =>
            services.Reprocess.ReprocessAsync(source.TaskId, noCloudConsent, default))).ErrorCode);
        Assert.Equal(1, services.Tasks.Enumerate(host.Owner).Count());
    }

    [SkippableFact]
    public async Task HistoryIsParentBoundStableAndPageable()
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (_, source) = await host.CreateSourceAsync(SourceOutcome.Completed);
        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);

        var first = await services.Reprocess.ReprocessAsync(source.TaskId,
            host.Request(Guid.NewGuid(), source.Revision, "manual", "first pass"), default);
        var second = await services.Reprocess.ReprocessAsync(source.TaskId,
            host.Request(Guid.NewGuid(), source.Revision, "quality", "second pass"), default);
        var secondState = await services.Tasks.ReadAsync(host.Owner, second.Task.TaskId, default);
        await services.Tasks.UpdateAsync(host.Owner, first.Task.TaskId,
            state => state.CreatedAt = secondState.CreatedAt.AddSeconds(-10), default);

        var page = await services.Reprocess.HistoryAsync(source.TaskId, host.Owner, 0, 1, default);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal(second.Task.TaskId, page.Items[0].TaskId);
        Assert.Equal("quality: second pass", page.Items[0].Reason);

        var next = await services.Reprocess.HistoryAsync(source.TaskId, host.Owner, 1, 1, default);
        Assert.Single(next.Items);
        Assert.Equal(first.Task.TaskId, next.Items[0].TaskId);
        Assert.Equal("manual: first pass", next.Items[0].Reason);
    }

    [SkippableFact]
    public async Task EndpointMapsReprocessRequestToActualService()
    {
        await using var host = await ReprocessRealDbHost.CreateAsync();
        var (_, source) = await host.CreateSourceAsync(SourceOutcome.Completed);
        using var scope = host.Factory.Services.CreateScope();
        var services = host.NewServices(scope.ServiceProvider);
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
            host.Request(Guid.NewGuid(), source.Revision, "manual", "endpoint"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<NoteAsrReprocessResultDto>>();
        Assert.NotNull(envelope?.Data);
        Assert.Equal(source.TaskId, envelope.Data.ParentTaskId);
        Assert.Equal("manual: endpoint", envelope.Data.Reason);
        await app.StopAsync();
    }

    private static AiAsrDestinationConsent SourceDestination =>
        new(Guid.NewGuid(), Guid.NewGuid(), "http://127.0.0.1/unused", "unused", "1", true);

    public enum SourceOutcome { Completed, Failed, Stopped, Running }

    private sealed record ReprocessServices(NoteAsrReprocessService Reprocess, NoteAsrTaskStore Tasks);

    private sealed class ReprocessRealDbHost : IAsyncDisposable
    {
        private readonly string _root;
        private readonly TempMigrationDatabase _database;

        private ReprocessRealDbHost(WebApplicationFactory<Program> factory, HttpClient client,
            TempMigrationDatabase database, string root, Guid owner, NoteAsrRouteConfigDto routeConfig)
        {
            Factory = factory;
            Client = client;
            _database = database;
            _root = root;
            Owner = owner;
            RouteConfig = routeConfig;
        }

        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public Guid Owner { get; }
        public NoteAsrRouteConfigDto RouteConfig { get; }

        public static async Task<ReprocessRealDbHost> CreateAsync()
        {
            var database = await TempMigrationDatabase.CreateAsync("test_note_asr_reprocess");
            await database.MigrateAsync();
            var root = Path.Combine(Path.GetTempPath(), "pim-note-reprocess-pg",
                Guid.NewGuid().ToString("N"));
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test")
                    .UseSetting("DisableHangfire", "true")
                    .UseSetting("Database:Migrations:FailFast", "false")
                    .UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz")
                    .UseSetting("ConnectionStrings:DefaultConnection", database.ConnectionString)
                    .UseSetting("NativeNotes:StorageRoot", Path.Combine(root, "materials"))
                    .UseSetting("ManagedStorage:Root", Path.Combine(root, "binary"))
                    .UseSetting("DataProtection:KeysPath", Path.Combine(root, "keys"))
                    .ConfigureServices(services =>
                    {
                        var descriptor = services.SingleOrDefault(d =>
                            d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                        if (descriptor is not null) services.Remove(descriptor);
                        services.AddDbContext<PimDbContext>(options =>
                            options.UseNpgsql(database.ConnectionString));
                    });
            });

            var session = await AuthRegistrationTestHost.SeedAndLoginAsync(
                factory, "note-reprocess-pg-" + Guid.NewGuid().ToString("N"), "admin");
            var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
            var current = await Data<NoteAsrRouteConfigDto>(
                await client.GetAsync("/api/v1/native-notes/asr-routing/config"));
            var routeConfig = await Data<NoteAsrRouteConfigDto>(await client.PutAsJsonAsync(
                "/api/v1/native-notes/asr-routing/config", new UpdateNoteAsrRouteConfigRequest(
                    current.Revision, NoteAsrRoute.Local, "local-model", "r1", null, "local-v1")));
            return new(factory, client, database, root, session.UserId, routeConfig);
        }

        public ReprocessServices NewServices(IServiceProvider services)
        {
            var db = services.GetRequiredService<PimDbContext>();
            var tasks = services.GetRequiredService<NoteAsrTaskStore>();
            var materials = services.GetRequiredService<NoteMaterialUploadStore>();
            var presets = services.GetRequiredService<AiAsrPresetRegistry>();
            var secrets = services.GetRequiredService<ISecretProtector>();
            var configuration = services.GetRequiredService<IConfiguration>();
            var fixedUser = new FixedUser(Owner);
            var routing = new NoteAsrRoutingService(db, fixedUser, materials, presets, tasks,
                new NoteAsrEvidenceService(db, tasks, fixedUser), secrets,
                Options.Create(new NoteAsrRoutingOptions()));
            var noteAsr = new NoteAsrService(tasks, materials, db, fixedUser, presets,
                configuration, secrets, routing);
            return new(new NoteAsrReprocessService(db, tasks, materials, noteAsr, fixedUser), tasks);
        }

        public ReprocessNoteAsrRequest Request(Guid clientTaskId, Guid sourceRevision,
            string reasonKind, string reasonText) =>
            new(clientTaskId, Owner, sourceRevision,
                new NoteAsrReprocessReason(reasonKind, reasonText),
                new NoteAsrReprocessRoute(NoteAsrRoute.Local, RouteConfig.Revision,
                    RouteConfig.ConsentVersion, NoteAsrExecutionMode.Realtime, true));

        public async Task<(NoteMaterialUploadStoreTests.Fixture Fixture, NoteAsrTaskDto Source)>
            CreateSourceAsync(SourceOutcome outcome)
        {
            var fixture = NoteMaterialUploadStoreTests.Fixture.Create(16_000 * 61);
            var store = Factory.Services.GetRequiredService<NoteMaterialUploadStore>();
            await store.BeginAsync(Owner, fixture.Request);
            for (var file = 0; file < fixture.Content.Count; file++)
                for (var position = 0; position < fixture.Content[file].Length;
                     position += NoteMaterialUploadOptions.PartSizeBytes)
                {
                    var part = fixture.Content[file].AsSpan(position,
                        Math.Min(NoteMaterialUploadOptions.PartSizeBytes,
                            fixture.Content[file].Length - position)).ToArray();
                    await store.UploadPartAsync(Owner, fixture.Request.VersionId, file,
                        position / NoteMaterialUploadOptions.PartSizeBytes,
                        Convert.ToHexString(SHA256.HashData(part)).ToLowerInvariant(),
                        new MemoryStream(part));
                }
            await store.CompleteAsync(Owner, fixture.Request.VersionId);

            var start = new StartNoteAsrRequest(Guid.NewGuid(), Owner, fixture.Request.VersionId,
                SourceDestination, "zh", new NoteAsrRouteConsent(NoteAsrRoute.Local,
                    RouteConfig.Revision, RouteConfig.ConsentVersion, true));
            var started = await Data<NoteAsrTaskDto>(await Client.PostAsJsonAsync(Route, start));

            using var scope = Factory.Services.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
            var source = await tasks.UpdateAsync(Owner, started.TaskId, state =>
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

        public async Task<NoteAsrState> ReadSourceAsync(Guid taskId)
        {
            using var scope = Factory.Services.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<NoteAsrTaskStore>();
            return await tasks.ReadAsync(Owner, taskId, default);
        }

        public string TaskPath(Guid taskId) =>
            Path.Combine(_root, "materials", "asr-tasks", Owner.ToString("D"),
                taskId.ToString("D") + ".json");

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static async Task<T> Data<T>(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode,
                response.StatusCode + ": " + await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "ReprocessRealDbTestScheme";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "reprocess-real-db")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId { get; } = id;
        public string? Role => "admin";
    }
}
