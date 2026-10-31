using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Ai.Asr;

public sealed class AiAsrUrlSourceTests
{
    private const long Mib = 1024L * 1024L;

    [Fact]
    public async Task Url_mode_sends_the_original_url_to_a_real_local_provider_and_persists_preflight_only()
    {
        var calls = 0;
        string? providerUrl = null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = builder.Build();
        provider.MapPost("/v1/chat/completions", async (HttpRequest incoming) =>
        {
            Interlocked.Increment(ref calls);
            using var json = await JsonDocument.ParseAsync(incoming.Body);
            providerUrl = json.RootElement.GetProperty("messages")[0].GetProperty("content")[0]
                .GetProperty("input_audio").GetProperty("data").GetString();
            return Results.Json(new
            {
                choices = new[]
                {
                    new { message = new { content = "synthetic transcript" }, finish_reason = "stop" }
                },
                usage = new
                {
                    prompt_tokens = 10, completion_tokens = 4, total_tokens = 14,
                    prompt_tokens_details = new { audio_tokens = 5 }
                }
            });
        });
        await provider.StartAsync();

        var sourceUrl = "https://audio.example.test/private/take.wav?sig=XZ&opaque=Q7";
        var fixture = await FixtureAsync(new Uri(provider.Urls.Single()).AbsoluteUri.TrimEnd('/') + "/v1/");
        using var presetHandler = new HttpClientHandler { AllowAutoRedirect = false };
        var (preset, registry) = Registry(() => new HttpClient(presetHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        var download = Download(sourceUrl, BuildWave(8_000));
        var fetcher = new StaticFetcher(download);
        var service = Service(fixture, registry, fetcher);

        var result = await service.CallUrlAsync(Request(fixture, preset.Descriptor), CancellationToken.None);

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal(1, calls);
        Assert.Equal(sourceUrl, providerUrl);
        Assert.Equal(1, fetcher.Calls);
        fixture.Db.ChangeTracker.Clear();
        var row = await fixture.Db.AiRequestLogs.AsNoTracking().SingleAsync(x => x.Id == result.LogId);
        Assert.Equal("ai.asr-url-call", row.Purpose);
        Assert.True(row.ExecutionExpiresAt!.Value - row.StartedAt >=
                    SecureRemoteAudioFetcher.DefaultOperationBudget + TimeSpan.FromSeconds(30));
        using var metadata = JsonDocument.Parse(row.MetadataJson);
        Assert.Equal("https://audio.example.test", metadata.RootElement.GetProperty("sourceOrigin").GetString());
        Assert.Equal(download.Provenance.Sha256,
            metadata.RootElement.GetProperty("urlPreflightSha256").GetString());
        Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
        Assert.False(metadata.RootElement.GetProperty("providerFetchVerified").GetBoolean());
        Assert.Equal("url-preflight-not-provider-verified",
            metadata.RootElement.GetProperty("urlIntegrityWarning").GetString());
        Assert.DoesNotContain(sourceUrl, row.RequestPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/take.wav", row.RequestMessagesJson, StringComparison.Ordinal);
        using var responseAudit = JsonDocument.Parse(row.ResponseRawJson);
        Assert.False(responseAudit.RootElement.GetProperty("providerWireBodyPersisted").GetBoolean());

        var material = new AiAsrMaterialService(fixture.Db, fixture.Files, fixture.Owner, registry);
        var (stored, contentType) = await material.ReadAsync(result.LogId, CancellationToken.None);
        Assert.Equal(BuildWave(8_000), stored);
        Assert.Equal("audio/wav", contentType);
    }

    [Fact]
    public async Task Provider_echo_and_json_escaped_url_forms_never_reach_durable_audit_or_tostring()
    {
        var sourceUrl = "https://audio.example.test/private/take.wav?sig=XZ&opaque=Q7";
        const string escaped = "https:\\/\\/audio.example.test\\/private\\/take.wav?sig=XZ\\u0026opaque=Q7";
        var echo = $"raw={sourceUrl}; escaped={escaped}; query=sig=XZ&opaque=Q7";
        var handler = new CountingHandler((_, _) => Task.FromResult(JsonResponse(new
        {
            choices = new[]
            {
                new { message = new { content = echo }, finish_reason = "stop" }
            },
            usage = new { prompt_tokens_details = new { audio_tokens = 3 } },
            upstreamEcho = new { url = sourceUrl, path = "/private/take.wav", query = "sig=XZ&opaque=Q7" }
        })));
        var fixture = await FixtureAsync("https://api.synthetic.invalid/v1/");
        var (preset, registry) = Registry(() => new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        var fetcher = new StaticFetcher(Download(sourceUrl, BuildWave(8_000)));
        var request = Request(fixture, preset.Descriptor);
        var service = Service(fixture, registry, fetcher);

        var result = await service.CallUrlAsync(request, CancellationToken.None);

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal(1, handler.Calls);
        fixture.Db.ChangeTracker.Clear();
        var row = await fixture.Db.AiRequestLogs.AsNoTracking().SingleAsync();
        var durable = string.Join("\n", row.RequestMessagesJson, row.RequestPayloadJson,
            row.ResponseRawJson, row.ResponseText, row.ErrorMessage, row.MetadataJson);
        foreach (var sensitive in new[]
                 {
                     sourceUrl, "/private/take.wav", "take.wav", "sig=XZ", "opaque=Q7",
                     "XZ", "Q7", escaped, "https%3A%2F%2Faudio.example.test"
                 })
            Assert.DoesNotContain(sensitive, durable, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_REMOTE_AUDIO_URL]", row.ResponseText);
        Assert.DoesNotContain("upstreamEcho", row.ResponseRawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=XZ", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sig=XZ", request.Audio.ToString(), StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(row.MetadataJson);
        Assert.Equal("https://audio.example.test",
            metadata.RootElement.GetProperty("sourceOrigin").GetString());
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_same_id_replay_does_not_fetch_again()
    {
        var fixture = await FixtureAsync("https://api.synthetic.invalid/v1/");
        var (preset, registry) = Registry(() => new HttpClient(JsonHandler(new
        {
            choices = new[] { new { message = new { content = "unused" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 1 } }
        })) { Timeout = Timeout.InfiniteTimeSpan });
        var fetcher = new BlockingFetcher();
        var request = Request(fixture, preset.Descriptor);
        var service = Service(fixture, registry, fetcher);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CallUrlAsync(request, cancelled.Token));
        await fetcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Db.ChangeTracker.Clear();
        var row = await fixture.Db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("cancelled", row.Status);
        Assert.Equal("url_preflight_cancelled", row.ErrorCode);

        var replay = await service.CallUrlAsync(request, CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(AiRequestStatus.Cancelled, replay.Status);
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task Provider_timeout_is_unknown_and_replay_does_not_send_a_second_provider_request()
    {
        var handler = new CountingHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return JsonResponse(new { });
        });
        var fixture = await FixtureAsync("https://api.synthetic.invalid/v1/", timeoutSeconds: 1);
        var (preset, registry) = Registry(() => new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        var sourceUrl = "https://audio.example.test/private/timeout.wav?sig=T1";
        var service = Service(fixture, registry,
            new StaticFetcher(Download(sourceUrl, BuildWave(8_000))));
        var request = Request(fixture, preset.Descriptor);

        var result = await service.CallUrlAsync(request, CancellationToken.None);

        Assert.Equal(AiRequestStatus.TimedOut, result.Status);
        Assert.Contains("UNKNOWN", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
        fixture.Db.ChangeTracker.Clear();
        var row = await fixture.Db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("provider_timeout_unknown", row.ErrorCode);

        var replay = await service.CallUrlAsync(request, CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(AiRequestStatus.TimedOut, replay.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Exact_100MiB_remote_audio_is_owner_readable_but_one_byte_over_is_rejected()
    {
        var fixture = await FixtureAsync("https://api.synthetic.invalid/v1/", seedRuntime: false);
        var log = new AiRequestLogEntity
        {
            UserId = fixture.Owner.UserId,
            ClientRequestId = Guid.NewGuid(),
            InputHash = new string('a', 64),
            Module = "system",
            Purpose = "ai.asr-url-call",
            SourceObjectType = "ai-model-public-url",
            SourceObjectId = fixture.Profile.Id.ToString(),
            Provider = MimoMultimodalPreset.PresetId,
            Model = MimoMultimodalPreset.FlashModel,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        fixture.Db.AiRequestLogs.Add(log);
        var bytes = BuildWave(AiAsrMaterialService.MaxRemoteAudioBytes);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var download = new RemoteAudioDownload(bytes, new RemoteAudioProvenance(
            "https://audio.example.test", "wav", digest, bytes.Length, "audio/wav", DateTimeOffset.UtcNow));
        var source = new Uri("https://audio.example.test/private/100mib.wav?sig=B1");
        var audio = AiAsrMaterialService.AcceptRemote("100mib.wav", "wav", source, download,
            MimoAudioFormats.Supported);
        var stored = AiAsrMaterialService.Describe(log.Id, audio);
        fixture.Db.AiCallAudio.Add(stored);
        await fixture.Db.SaveChangesAsync();
        var material = new AiAsrMaterialService(fixture.Db, fixture.Files, fixture.Owner);
        await material.StoreForOwnerAsync(fixture.Owner.UserId!.Value, stored, audio, CancellationToken.None);

        var (read, _) = await material.ReadAsync(log.Id, CancellationToken.None);
        Assert.Equal(AiAsrMaterialService.MaxRemoteAudioBytes, read.Length);
        Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(read)).ToLowerInvariant());

        var other = new Owner();
        var otherMaterial = new AiAsrMaterialService(fixture.Db, fixture.Files, other);
        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            otherMaterial.ReadAsync(log.Id, CancellationToken.None));
        Assert.Equal(40401, denied.ErrorCode);

        var tooLarge = new byte[AiAsrMaterialService.MaxRemoteAudioBytes + 1];
        var rejected = Assert.Throws<DomainException>(() => AiAsrMaterialService.AcceptRemote(
            "too-large.wav", "wav", source,
            new RemoteAudioDownload(tooLarge, new RemoteAudioProvenance(
                "https://audio.example.test", "wav", new string('0', 64), tooLarge.Length,
                "audio/wav", DateTimeOffset.UtcNow)),
            MimoAudioFormats.Supported));
        Assert.Equal(40001, rejected.ErrorCode);
    }

    [Fact]
    public void Url_mode_does_not_raise_the_existing_default_or_dedicated_audio_budgets()
    {
        Assert.Equal(4L * Mib, AiAsrMaterialService.MaxAudioBytes);
        Assert.Equal(72L * Mib, AiAsrMaterialService.MaxMimoRequestJsonBytes);
        Assert.Equal(50L * Mib, MimoAudioFormats.MaxDecodedBytes);
        Assert.Equal(50L * Mib, new MimoMultimodalPreset(_ => throw new InvalidOperationException())
            .Descriptor.MaxAudioBytes);
        Assert.Equal(100L * Mib, AiAsrMaterialService.MaxRemoteAudioBytes);
        Assert.Equal(100L * Mib, RemoteAudioFetchLimits.HardMaxBytes);
    }

    [SkippableFact]
    public async Task Postgres_same_client_id_has_one_durable_placeholder_and_one_fetch()
    {
        var pg = await OwnedPgAsync();
        await using var handle = pg;
        var owner = new Owner();
        var files = new MemoryFiles();
        ISecretProtector secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(pg.Options, owner, secrets);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCalls = 0;
        var handler = new CountingHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref providerCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return JsonResponse(new
            {
                choices = new[] { new { message = new { content = "pg concurrent" }, finish_reason = "stop" } },
                usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
            });
        });
        var preset = new MimoMultimodalPreset(_ => new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        var registry = new AiAsrPresetRegistry([preset]);
        var sourceUrl = "https://audio.example.test/private/pg-concurrent.wav?sig=PC";
        var fetcher = new StaticFetcher(Download(sourceUrl, BuildWave(8_000)));
        var fixture = new Fixture(null!, owner, files, secrets, profile);
        var request = Request(fixture, preset.Descriptor);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () =>
        {
            await start.Task;
            await using var db = new PimDbContext(pg.Options);
            return await Service(new Fixture(db, owner, files, secrets, profile), registry, fetcher)
                .CallUrlAsync(request, CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            await start.Task;
            await using var db = new PimDbContext(pg.Options);
            return await Service(new Fixture(db, owner, files, secrets, profile), registry, fetcher)
                .CallUrlAsync(request, CancellationToken.None);
        });
        start.SetResult();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, fetcher.Calls);
        Assert.Equal(1, Volatile.Read(ref providerCalls));
        Assert.Single(results.Select(x => x.LogId).Distinct());
        await using (var check = new PimDbContext(pg.Options))
        {
            Assert.Equal(1, await check.AiRequestLogs.CountAsync(x =>
                x.UserId == owner.UserId && x.ClientRequestId == request.ClientRequestId));
            var duplicate = new AiRequestLogEntity
            {
                UserId = owner.UserId, ClientRequestId = request.ClientRequestId,
                InputHash = new string('e', 64), Module = "system", Purpose = "ai.asr-url-call",
                SourceObjectType = "synthetic", SourceObjectId = profile.Id.ToString(),
                Provider = MimoMultimodalPreset.PresetId, Model = MimoMultimodalPreset.FlashModel,
                Status = "failed", StartedAt = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid().ToString("N")
            };
            check.AiRequestLogs.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync());
            var pgError = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, pgError.SqlState);
        }
    }

    [SkippableFact]
    public async Task Postgres_recovery_to_unknown_cannot_be_overwritten_by_a_late_provider_response()
    {
        var pg = await OwnedPgAsync();
        await using var handle = pg;
        var owner = new Owner();
        var files = new MemoryFiles();
        ISecretProtector secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(pg.Options, owner, secrets);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCalls = 0;
        var handler = new CountingHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref providerCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return JsonResponse(new
            {
                choices = new[] { new { message = new { content = "late response" }, finish_reason = "stop" } },
                usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
            });
        });
        var preset = new MimoMultimodalPreset(_ => new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        var registry = new AiAsrPresetRegistry([preset]);
        var sourceUrl = "https://audio.example.test/private/pg-recovery.wav?sig=PR";
        var fetcher = new StaticFetcher(Download(sourceUrl, BuildWave(8_000)));
        var fixture = new Fixture(null!, owner, files, secrets, profile);
        var request = Request(fixture, preset.Descriptor);
        await using var db = new PimDbContext(pg.Options);
        var service = Service(new Fixture(db, owner, files, secrets, profile), registry, fetcher);
        var running = service.CallUrlAsync(request, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var recovery = new PimDbContext(pg.Options))
        {
            await recovery.AiRequestLogs.Where(x => x.UserId == owner.UserId &&
                    x.ClientRequestId == request.ClientRequestId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ExecutionExpiresAt,
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
            await AiCallRecovery.RecoverExpiredAsync(recovery, CancellationToken.None);
        }
        release.SetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(AiRequestStatus.Incomplete, result.Status);
        Assert.Equal(1, fetcher.Calls);
        Assert.Equal(1, Volatile.Read(ref providerCalls));
        db.ChangeTracker.Clear();
        var final = await db.AiRequestLogs.AsNoTracking().SingleAsync(x =>
            x.UserId == owner.UserId && x.ClientRequestId == request.ClientRequestId);
        Assert.Equal("incomplete", final.Status);
        Assert.Equal("outcome_unknown", final.ErrorCode);
        Assert.Null(final.ResponseText);

        var replay = await service.CallUrlAsync(request, CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(AiRequestStatus.Incomplete, replay.Status);
        Assert.Equal(1, fetcher.Calls);
        Assert.Equal(1, Volatile.Read(ref providerCalls));
    }

    private static async Task<PgHandle> OwnedPgAsync()
    {
        const string optInVariable = "PIM_ASR516_URL_PG";
        Skip.IfNot(Environment.GetEnvironmentVariable(optInVariable) == "1",
            $"{optInVariable}=1 not set; synthetic PostgreSQL URL tests skipped");
        var rootValue = Environment.GetEnvironmentVariable("PIM_ASR_ADMISSION_PG_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(rootValue),
            "PIM_ASR516_URL_PG=1 requires PIM_ASR_ADMISSION_PG_ROOT");
        var root = Path.GetFullPath(rootValue!);
        var data = Path.Combine(root, "data");
        var database = "pim_url_" + Guid.NewGuid().ToString("N")[..16];
        var user = "pim_url_user_" + Guid.NewGuid().ToString("N")[..8];
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
        var adminConnection = "Host=127.0.0.1;Port=55444;Database=postgres;Username=pim;Timeout=10";
        using (var admin = new NpgsqlConnection(adminConnection))
        {
            admin.Open();
            using (var dataDirectory = new NpgsqlCommand("select current_setting('data_directory')", admin))
            {
                var actual = (string)dataDirectory.ExecuteScalar()!;
                Assert.Equal(Path.GetFullPath(data), Path.GetFullPath(actual), ignoreCase: true);
            }
            using (var createRole = new NpgsqlCommand(
                $"CREATE ROLE {user} LOGIN PASSWORD '{password}'", admin))
                createRole.ExecuteNonQuery();
            try
            {
                using var createDb = new NpgsqlCommand($"CREATE DATABASE {database} OWNER {user}", admin);
                createDb.ExecuteNonQuery();
            }
            catch
            {
                using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS {user}", admin);
                dropRole.ExecuteNonQuery();
                throw;
            }
        }
        var connection = $"Host=127.0.0.1;Port=55444;Database={database};Username={user};" +
                         $"Password={password};Include Error Detail=true";
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.EnableRetryOnFailure(3)).Options;
        await using (var strategyDb = new PimDbContext(options))
            Assert.True(strategyDb.Database.CreateExecutionStrategy().RetriesOnFailure);
        await using (var db = new PimDbContext(options))
            await db.Database.MigrateAsync();
        return new PgHandle(adminConnection, database, user, connection, options);
    }

    private static async Task<AiModelProfileEntity> SeedPostgresAsync(DbContextOptions<PimDbContext> options,
        Owner owner, ISecretProtector secrets)
    {
        await using var db = new PimDbContext(options);
        db.Users.Add(new UserEntity
        {
            Id = owner.UserId!.Value, Username = "url-pg-" + owner.UserId.Value.ToString("N")[..10],
            Email = "url-pg-synthetic@example.invalid", PasswordHash = "synthetic-not-a-login",
            Role = "admin", IsActive = true
        });
        var profile = new AiModelProfileEntity
        {
            Name = "url-pg-synthetic", NormalizedName = "url-pg-" + Guid.NewGuid().ToString("N"),
            BaseUrl = "https://api.synthetic.invalid/v1/", Model = MimoMultimodalPreset.FlashModel,
            Enabled = true, Capabilities = AiModelCapabilityFlags.Transcription,
            AsrPresetId = MimoMultimodalPreset.PresetId,
            ApiKeySecret = secrets.Protect("synthetic-url-pg-key"), TimeoutSeconds = 30
        };
        db.AiModelProfiles.Add(profile);
        db.AiModelRuntime.Add(new AiModelRuntimeEntity
        {
            Id = AiModelRuntimeEntity.SingletonId, Enabled = true,
            Revision = Guid.NewGuid(), TranscriptionModelId = profile.Id
        });
        await db.SaveChangesAsync();
        return profile;
    }

    private static async Task<Fixture> FixtureAsync(string baseUrl, int timeoutSeconds = 30,
        bool seedRuntime = true)
    {
        var db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("asr-url-" + Guid.NewGuid().ToString("N")).Options);
        var owner = new Owner();
        var files = new MemoryFiles();
        ISecretProtector secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        await db.Users.AddAsync(new UserEntity
        {
            Id = owner.UserId!.Value, Username = "url-" + owner.UserId.Value.ToString("N")[..10],
            Email = "url-synthetic@example.invalid", PasswordHash = "synthetic-not-a-login",
            Role = "admin", IsActive = true
        });
        var profile = new AiModelProfileEntity
        {
            Name = "url-synthetic", NormalizedName = "url-synthetic-" + Guid.NewGuid().ToString("N"),
            BaseUrl = baseUrl, Model = MimoMultimodalPreset.FlashModel, Enabled = true,
            Capabilities = AiModelCapabilityFlags.Transcription,
            AsrPresetId = MimoMultimodalPreset.PresetId,
            ApiKeySecret = secrets.Protect("synthetic-url-provider-key"), TimeoutSeconds = timeoutSeconds
        };
        db.AiModelProfiles.Add(profile);
        if (seedRuntime)
            db.AiModelRuntime.Add(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId, Enabled = true,
                Revision = Guid.NewGuid(), TranscriptionModelId = profile.Id
            });
        await db.SaveChangesAsync();
        return new Fixture(db, owner, files, secrets, profile);
    }

    private static (MimoMultimodalPreset Preset, AiAsrPresetRegistry Registry) Registry(
        Func<HttpClient> client)
    {
        var preset = new MimoMultimodalPreset(_ => client());
        return (preset, new AiAsrPresetRegistry([preset]));
    }

    private static AiAsrCallService Service(Fixture fixture, AiAsrPresetRegistry registry,
        IRemoteAudioFetcher fetcher) =>
        new(fixture.Db, fixture.Secrets, fixture.Owner, new AiTextCallGate(), registry,
            new AiAsrMaterialService(fixture.Db, fixture.Files, fixture.Owner, registry), fetcher);

    private static AiAsrUrlCallRequest Request(Fixture fixture, AiAsrPresetDto preset) =>
        new(Guid.NewGuid(), fixture.Owner.UserId!.Value,
            new AiAsrUrlAudioRequest("take.wav", "wav",
                "https://audio.example.test/private/take.wav?sig=XZ&opaque=Q7",
                null, true, true),
            new AiAsrDestinationConsent(fixture.Profile.Id, fixture.Profile.Revision,
                AiAsrPresetRegistry.OperationEndpoint(fixture.Profile.BaseUrl, preset),
                preset.Id, preset.Version, true),
            "zh", fixture.Profile.Id);

    private static RemoteAudioDownload Download(string url, byte[] bytes)
    {
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new RemoteAudioDownload(bytes, new RemoteAudioProvenance(
            new Uri(url).GetLeftPart(UriPartial.Authority), "wav", digest, bytes.Length,
            "audio/wav", DateTimeOffset.UtcNow));
    }

    private static byte[] BuildWave(int totalBytes)
    {
        var size = Math.Max(44, totalBytes);
        var bytes = new byte[size];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 16_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 32_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), (short)2);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), (short)16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.TryWriteBytes(bytes.AsSpan(40), size - 44);
        return bytes;
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private static HttpMessageHandler JsonHandler(object body) =>
        new CountingHandler((_, _) => Task.FromResult(JsonResponse(body)));

    private sealed record Fixture(PimDbContext Db, Owner Owner, MemoryFiles Files,
        ISecretProtector Secrets, AiModelProfileEntity Profile);

    private sealed class PgHandle(string adminConnection, string database, string user,
        string connection, DbContextOptions<PimDbContext> options) : IAsyncDisposable
    {
        public string Connection { get; } = connection;
        public DbContextOptions<PimDbContext> Options { get; } = options;

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin))
                await drop.ExecuteNonQueryAsync();
            using (var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS {user}", admin))
                await dropRole.ExecuteNonQueryAsync();
        }
    }

    private sealed class Owner : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Username => "url-synthetic";
        public string? Role => "admin";
    }

    private sealed class StaticFetcher(RemoteAudioDownload download) : IRemoteAudioFetcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<RemoteAudioDownload> FetchAsync(Uri url, RemoteAudioExpectation expectation,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(download);
        }
    }

    private sealed class BlockingFetcher : IRemoteAudioFetcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RemoteAudioDownload> FetchAsync(Uri url, RemoteAudioExpectation expectation,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                Released.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class CountingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return responder(request, cancellationToken);
        }
    }

    private sealed class MemoryFiles : IFileAttachmentStore, IBoundedFileAttachmentStore
    {
        private readonly ConcurrentDictionary<string, (Guid Owner, byte[] Bytes)> _objects = [];

        public Task<FileAttachmentRef> StoreAsync(Guid userId, string objectKey, Stream content,
            string contentType, long sizeBytes, CancellationToken ct = default) =>
            StoreAsync(userId, objectKey, content, contentType, sizeBytes, 4 * Mib, ct);

        public async Task<FileAttachmentRef> StoreAsync(Guid userId, string objectKey, Stream content,
            string contentType, long sizeBytes, long maxBytes, CancellationToken ct = default)
        {
            Assert.InRange(sizeBytes, 1, maxBytes);
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct);
            Assert.Equal(sizeBytes, memory.Length);
            _objects[objectKey] = (userId, memory.ToArray());
            return new FileAttachmentRef(objectKey, "synthetic", userId);
        }

        public Task<Stream> OpenReadAsync(Guid userId, FileAttachmentRef reference,
            CancellationToken ct = default)
        {
            var item = _objects[reference.ObjectKey];
            Assert.Equal(userId, item.Owner);
            return Task.FromResult<Stream>(new MemoryStream(item.Bytes, writable: false));
        }

        public Task<Stream> OpenReadAsync(Guid userId, FileAttachmentRef reference, long maxBytes,
            CancellationToken ct = default)
        {
            var item = _objects[reference.ObjectKey];
            Assert.Equal(userId, item.Owner);
            Assert.InRange(item.Bytes.Length, 1, maxBytes);
            return Task.FromResult<Stream>(new MemoryStream(item.Bytes, writable: false));
        }

        public Task<string?> GetDirectLinkAsync(Guid userId, FileAttachmentRef reference,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task DeleteAsync(Guid userId, FileAttachmentRef reference,
            CancellationToken ct = default)
        {
            _objects.TryRemove(reference.ObjectKey, out _);
            return Task.CompletedTask;
        }
    }
}
