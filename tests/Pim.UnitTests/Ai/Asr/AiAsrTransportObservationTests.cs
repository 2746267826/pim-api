using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.UnitTests.Harness;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Ai.Asr;

public sealed class AiAsrTransportObservationTests
{
    [Fact]
    public async Task Browser_http_evidence_is_persisted_without_rewriting_provider_success()
    {
        await using var db = ServiceTestBase.CreateDb();
        var owner = ServiceTestBase.DefaultUserId;
        var (job, log, clientRequestId) = Seed(db, owner, "succeeded");
        await db.SaveChangesAsync();
        var service = new AiAsrTransportObservationService(db, ServiceTestBase.CurrentUser(owner));

        var observation = await service.ReportAsync(new(clientRequestId, owner,
            AiAsrTransportKind.AsyncPoll, AiAsrTransportObservationSource.ClientHttpResponse,
            504, "nginx"), CancellationToken.None);

        Assert.Equal(504, observation.GatewayStatus);
        Assert.Equal("nginx", observation.GatewayName);
        Assert.Equal(AiAsrTransportObservationSource.ClientHttpResponse, observation.Source);
        job = await db.AiAsrJobs.AsNoTracking().SingleAsync();
        log = await db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("succeeded", job.Status);
        Assert.True(job.ProviderAttempted);
        Assert.True(job.OutcomeKnown);
        Assert.Equal(504, job.GatewayStatus);
        Assert.Equal("nginx", job.GatewayName);
        Assert.Equal("client_http_response", job.GatewayEvidenceSource);
        Assert.Equal("succeeded", log.Status);
        var metadata = Assert.IsType<JsonObject>(JsonNode.Parse(log.MetadataJson));
        Assert.Equal(504, metadata["gatewayStatus"]!.GetValue<int>());
        Assert.Equal("nginx", metadata["gatewayName"]!.GetValue<string>());
        Assert.Equal("client_http_response",
            metadata["gatewayEvidenceSource"]!.GetValue<string>());

        await Assert.ThrowsAsync<DomainException>(() => service.ReportAsync(new(clientRequestId,
            owner, AiAsrTransportKind.AsyncPoll,
            AiAsrTransportObservationSource.ClientNetworkFailure, 504, "nginx"),
            CancellationToken.None));
        Assert.Equal("succeeded", (await db.AiAsrJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Single(db.AiAsrTransportObservations);
    }

    [Fact]
    public async Task Observation_is_owner_scoped()
    {
        await using var db = ServiceTestBase.CreateDb();
        var owner = ServiceTestBase.DefaultUserId;
        var other = Guid.NewGuid();
        var (_, _, clientRequestId) = Seed(db, owner, "running");
        await db.SaveChangesAsync();
        var service = new AiAsrTransportObservationService(db, ServiceTestBase.CurrentUser(other));

        var failure = await Assert.ThrowsAsync<DomainException>(() => service.ReportAsync(new(
            clientRequestId, other, AiAsrTransportKind.AsyncPoll,
            AiAsrTransportObservationSource.ClientHttpResponse, 504, "nginx"),
            CancellationToken.None));

        Assert.Equal(40401, failure.ErrorCode);
        Assert.Empty(db.AiAsrTransportObservations);
        Assert.Null((await db.AiAsrJobs.AsNoTracking().SingleAsync()).GatewayStatus);
    }

    [Fact]
    public async Task Legacy_sync_url_result_reads_gateway_evidence_without_changing_provider_outcome()
    {
        await using var db = ServiceTestBase.CreateDb();
        var owner = ServiceTestBase.DefaultUserId;
        var clientRequestId = Guid.NewGuid();
        var log = new AiRequestLogEntity
        {
            UserId = owner,
            ClientRequestId = clientRequestId,
            Module = "system",
            Purpose = "ai.asr-url-call",
            SourceObjectType = "ai-model",
            SourceObjectId = Guid.NewGuid().ToString(),
            Provider = "mimo-multimodal",
            Model = "synthetic-asr",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "incomplete",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            InputHash = "synthetic-url-input",
            MetadataJson = """
                {"abortSource":"caller_disconnect","outcomeKnown":false,
                 "gatewayStatus":null,"upstreamStatus":null,"maxOutputTokens":1024}
                """
        };
        db.AiRequestLogs.Add(log);
        await db.SaveChangesAsync();
        var currentUser = ServiceTestBase.CurrentUser(owner);
        var service = new AiAsrCallService(db, null!, currentUser, new AiTextCallGate(),
            new AiAsrPresetRegistry([]), null!);

        await new AiAsrTransportObservationService(db, currentUser).ReportAsync(new(
            clientRequestId, owner, AiAsrTransportKind.SyncUrlCall,
            AiAsrTransportObservationSource.ClientHttpResponse, 504, "nginx"),
            CancellationToken.None);
        var result = await service.GetUrlAsync(clientRequestId, CancellationToken.None);

        Assert.Equal(AiRequestStatus.Incomplete, result.Status);
        Assert.NotNull(result.Diagnostics);
        Assert.Equal("caller_disconnect", result.Diagnostics.AbortSource);
        Assert.False(result.Diagnostics.OutcomeKnown);
        Assert.Equal(504, result.Diagnostics.GatewayStatus);
        Assert.Equal("nginx", result.Diagnostics.GatewayName);
        Assert.Equal("client_http_response", result.Diagnostics.GatewayEvidenceSource);
        Assert.Equal("sync_url_call", result.Diagnostics.TransportObservationKind);
        Assert.Equal("incomplete",
            (await db.AiRequestLogs.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Legacy_sync_file_result_reads_gateway_504_and_network_unknown_separately()
    {
        await using var db = ServiceTestBase.CreateDb();
        var owner = ServiceTestBase.DefaultUserId;
        var clientRequestId = Guid.NewGuid();
        var log = new AiRequestLogEntity
        {
            UserId = owner,
            ClientRequestId = clientRequestId,
            Module = "system",
            Purpose = "ai.asr-call",
            SourceObjectType = "ai-model",
            SourceObjectId = Guid.NewGuid().ToString(),
            Provider = "mimo-multimodal",
            Model = "synthetic-asr",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "incomplete",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            InputHash = "synthetic-file-input",
            MetadataJson = """
                {"abortSource":"caller_disconnect","outcomeKnown":false,
                 "gatewayStatus":null,"upstreamStatus":null,"maxOutputTokens":1024}
                """
        };
        db.AiRequestLogs.Add(log);
        await db.SaveChangesAsync();
        var currentUser = ServiceTestBase.CurrentUser(owner);
        var service = new AiAsrCallService(db, null!, currentUser, new AiTextCallGate(),
            new AiAsrPresetRegistry([]), null!);

        await new AiAsrTransportObservationService(db, currentUser).ReportAsync(new(
            clientRequestId, owner, AiAsrTransportKind.SyncFileCall,
            AiAsrTransportObservationSource.ClientHttpResponse, 504, "nginx"),
            CancellationToken.None);
        var http = await service.GetAsync(clientRequestId, CancellationToken.None);

        Assert.Equal(AiRequestStatus.Incomplete, http.Status);
        Assert.Equal("caller_disconnect", http.Diagnostics?.AbortSource);
        Assert.Equal(504, http.Diagnostics?.GatewayStatus);
        Assert.Equal("nginx", http.Diagnostics?.GatewayName);

        await new AiAsrTransportObservationService(db, currentUser).ReportAsync(new(
            clientRequestId, owner, AiAsrTransportKind.SyncFileCall,
            AiAsrTransportObservationSource.ClientNetworkFailure), CancellationToken.None);
        var network = await service.GetAsync(clientRequestId, CancellationToken.None);

        Assert.Equal(AiRequestStatus.Incomplete, network.Status);
        Assert.Equal(504, network.Diagnostics?.GatewayStatus);
        Assert.Equal("client_http_response", network.Diagnostics?.GatewayEvidenceSource);
        Assert.Equal("incomplete", (await db.AiRequestLogs.AsNoTracking().SingleAsync()).Status);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_gateway_evidence_is_upserted_owner_scoped_and_survives_success()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_transport_observation");
        await database.MigrateAsync();
        Assert.True(await database.HasMigrationLikeAsync("AddAiAsrTransportObservations"));
        Assert.True(await database.TableExistsAsync("ai_asr_transport_observations"));
        Assert.True(await database.IndexExistsAsync(
            "IX_ai_asr_transport_observations_owner_kind_source"));

        var owner = Guid.NewGuid();
        var clientRequestId = Guid.NewGuid();
        await using (var seed = Context(database.ConnectionString))
        {
            Seed(seed, owner, "running", clientRequestId);
            await seed.SaveChangesAsync();
        }

        using var scopes = BuildScopes(database.ConnectionString);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var service = new AiAsrTransportObservationService(db,
                ServiceTestBase.CurrentUser(owner));
            await service.ReportAsync(new(clientRequestId, owner,
                AiAsrTransportKind.AsyncPoll,
                AiAsrTransportObservationSource.ClientHttpResponse, 504, "nginx"),
                CancellationToken.None);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(reports).WaitAsync(TimeSpan.FromSeconds(20));

        await using (var success = Context(database.ConnectionString))
        {
            await success.AiAsrJobs.ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "succeeded")
                .SetProperty(x => x.OutcomeKnown, true)
                .SetProperty(x => x.ProviderAttempted, true)
                .SetProperty(x => x.ResponseBodyComplete, true));
            await success.AiRequestLogs.ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "succeeded")
                .SetProperty(x => x.ResponseText, "synthetic complete transcript"));
        }

        await using (var late = Context(database.ConnectionString))
        {
            await new AiAsrTransportObservationService(late,
                    ServiceTestBase.CurrentUser(owner))
                .ReportAsync(new(clientRequestId, owner, AiAsrTransportKind.AsyncPoll,
                    AiAsrTransportObservationSource.ClientHttpResponse, 504, "nginx"),
                    CancellationToken.None);
        }

        await using (var verify = Context(database.ConnectionString))
        {
            var job = await verify.AiAsrJobs.AsNoTracking().SingleAsync();
            var log = await verify.AiRequestLogs.AsNoTracking().SingleAsync();
            var observation = await verify.AiAsrTransportObservations.AsNoTracking().SingleAsync();
            Assert.Equal("succeeded", job.Status);
            Assert.True(job.OutcomeKnown);
            Assert.True(job.ProviderAttempted);
            Assert.Equal("succeeded", log.Status);
            Assert.Equal(504, observation.GatewayStatus);
            Assert.Equal("nginx", observation.GatewayName);
            Assert.Equal("async_poll", observation.TransportKind);
            var metadata = Assert.IsType<JsonObject>(JsonNode.Parse(log.MetadataJson));
            Assert.Equal(504, metadata["gatewayStatus"]!.GetValue<int>());
            Assert.Equal("client_http_response",
                metadata["gatewayEvidenceSource"]!.GetValue<string>());
        }

        var other = Guid.NewGuid();
        await using (var hidden = Context(database.ConnectionString))
        {
            var failure = await Assert.ThrowsAsync<DomainException>(() =>
                new AiAsrTransportObservationService(hidden, ServiceTestBase.CurrentUser(other))
                    .ReportAsync(new(clientRequestId, other, AiAsrTransportKind.AsyncPoll,
                        AiAsrTransportObservationSource.ClientHttpResponse, 504, "nginx"),
                        CancellationToken.None));
            Assert.Equal(40401, failure.ErrorCode);
        }
    }

    private static (AiAsrJobEntity Job, AiRequestLogEntity Log, Guid ClientRequestId) Seed(
        PimDbContext db, Guid owner, string status, Guid? id = null)
    {
        var clientRequestId = id ?? Guid.NewGuid();
        var log = new AiRequestLogEntity
        {
            UserId = owner,
            ClientRequestId = clientRequestId,
            Module = "system",
            Purpose = "ai.asr-job",
            SourceObjectType = "ai-model",
            SourceObjectId = Guid.NewGuid().ToString(),
            Provider = "mimo-multimodal",
            Model = "synthetic-asr",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            InputHash = "synthetic-input",
            MetadataJson = """{"abortSource":"none","outcomeKnown":false,"maxOutputTokens":1024}"""
        };
        var job = new AiAsrJobEntity
        {
            OwnerId = owner,
            ClientRequestId = clientRequestId,
            LogId = log.Id,
            InputHash = log.InputHash,
            Status = status,
            ModelProfileId = Guid.NewGuid(),
            ModelRevision = Guid.NewGuid(),
            PresetId = "mimo-multimodal",
            PresetVersion = "1",
            Model = log.Model,
            Endpoint = "https://synthetic.invalid/v1/chat/completions",
            MaxOutputTokens = 1024,
            TimeoutSeconds = 600,
            AudioName = "synthetic.wav",
            AudioFormat = "wav",
            AudioSizeBytes = 44,
            AudioSha256 = new string('a', 64),
            ProviderAttempted = status == "succeeded",
            OutcomeKnown = status == "succeeded",
            ResponseBodyComplete = status == "succeeded"
        };
        db.AiRequestLogs.Add(log);
        db.AiAsrJobs.Add(job);
        return (job, log, clientRequestId);
    }

    private static PimDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(connectionString,
            npgsql => npgsql.EnableRetryOnFailure(3)).Options);

    private static ServiceProvider BuildScopes(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PimDbContext>(options => options.UseNpgsql(connectionString,
            npgsql => npgsql.EnableRetryOnFailure(3)));
        return services.BuildServiceProvider();
    }
}
