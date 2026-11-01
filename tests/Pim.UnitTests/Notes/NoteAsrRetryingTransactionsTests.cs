using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Pim.Core.Ai;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Notes;

/// <summary>
/// Synthetic PostgreSQL only. Each test creates its own database so the legacy Batch suite's
/// cross-owner cleanup can never erase another fixture.
/// </summary>
[Collection("NoteAsrRetryingTransactionsPg")]
[Trait("DataSource", "RealDb")]
public sealed class NoteAsrRetryingTransactionsTests
{
    private static readonly Uri BatchOrigin = new("https://batch-api-cn.xiaomimimo.com/v1/");

    [SkippableFact]
    public async Task Batch_submit_freezes_once_under_retry_strategy_and_replays_without_network()
    {
        await using var database = await SyntheticDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        PimDbContext? currentDb = null;
        var observedTransactions = new List<bool>();
        var factory = new SyntheticBatchFactory(
            (_, _) => Task.FromResult(UploadOk()),
            (_, _) => Task.FromResult(BatchOk("batch-retry-once")),
            () => observedTransactions.Add(currentDb!.Database.CurrentTransaction is not null));

        NoteAsrBatchJobDto first;
        await using (var db = database.NewContext(seed.Owner))
        {
            currentDb = db;
            var service = Service(db, seed.Owner, factory);
            first = await StartAsync(service, seed);
        }

        Assert.Equal("Validating", first.Status);
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);
        Assert.NotEmpty(observedTransactions);
        Assert.All(observedTransactions, inTransaction => Assert.False(inTransaction));

        NoteAsrBatchJobDto replay;
        await using (var db = database.NewContext(seed.Owner))
        {
            currentDb = db;
            var service = Service(db, seed.Owner, factory);
            replay = await StartAsync(service, seed);
        }

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, factory.UploadCalls);
        Assert.Equal(1, factory.CreateCalls);
        await using var verify = database.NewContext(seed.Owner);
        Assert.Equal(1, await verify.NoteAsrBatchJobs.CountAsync(x => x.OwnerId == seed.Owner));
        Assert.Equal("Creating", (await verify.NoteAsrAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == seed.AttemptId)).State);
        CleanupStorage(seed.Owner);
    }

    [SkippableFact]
    public async Task Evidence_write_uses_retry_strategy_and_is_idempotent()
    {
        await using var database = await SyntheticDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var tasks = Tasks(seed.Owner);

        NoteAsrEvidenceWriteResult first;
        await using (var db = database.NewContext(seed.Owner))
        {
            var service = new NoteAsrEvidenceService(db, tasks, new FixedUser(seed.Owner));
            first = await service.WriteAsync(seed.Owner, seed.AttemptId, 0, "batch", "batch-retry-once",
                "synthetic transcript", "{\"segment\":0}", true, CancellationToken.None);
        }

        NoteAsrEvidenceWriteResult replay;
        await using (var db = database.NewContext(seed.Owner))
        {
            var service = new NoteAsrEvidenceService(db, tasks, new FixedUser(seed.Owner));
            replay = await service.WriteAsync(seed.Owner, seed.AttemptId, 0, "batch", "batch-retry-once",
                "synthetic transcript", "{\"segment\":0}", true, CancellationToken.None);
        }

        Assert.Equal(first.EvidenceId, replay.EvidenceId);
        Assert.False(first.Duplicate);
        Assert.True(replay.Duplicate);
        await using var verify = database.NewContext(seed.Owner);
        Assert.Equal(1, await verify.NoteAsrSegmentEvidence.CountAsync(x =>
            x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId));
        var attempt = await verify.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.AttemptId);
        Assert.Equal(1, attempt.CompletedSegments);
        var state = await tasks.ReadAsync(seed.Owner, seed.TaskId, CancellationToken.None);
        Assert.Equal("Succeeded", state.Steps[0].Current.Status);
        CleanupStorage(seed.Owner);
    }

    [SkippableFact]
    public async Task Evidence_write_reuses_an_existing_routing_execution_unit()
    {
        await using var database = await SyntheticDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var tasks = Tasks(seed.Owner);

        await using (var db = database.NewContext(seed.Owner))
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync();
                var service = new NoteAsrEvidenceService(db, tasks, new FixedUser(seed.Owner));
                await service.WriteAsync(seed.Owner, seed.AttemptId, 0, "batch", "outer-owner",
                    "outer transaction transcript", null, true, CancellationToken.None);
                await transaction.CommitAsync();
            });
        }

        await using var verify = database.NewContext(seed.Owner);
        Assert.Equal(1, await verify.NoteAsrSegmentEvidence.CountAsync(x =>
            x.OwnerId == seed.Owner && x.AttemptId == seed.AttemptId));
        var attempt = await verify.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.AttemptId);
        Assert.Equal(1, attempt.CompletedSegments);
        CleanupStorage(seed.Owner);
    }

    [Fact]
    public async Task Evidence_write_supports_nonrelational_provider_without_sql_transaction_apis()
    {
        var owner = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("asr-evidence-nonrelational-" + owner.ToString("N"))
            .Options;
        try
        {
            await using var db = new PimDbContext(options, new FixedUser(owner));
            var seed = await SeedInMemoryAsync(db, owner);
            var tasks = Tasks(owner);
            var service = new NoteAsrEvidenceService(db, tasks, new FixedUser(owner));

            var first = await service.WriteAsync(seed.Owner, seed.AttemptId, 0, "batch",
                "in-memory-handle", "in-memory transcript", null, true, CancellationToken.None);
            var replay = await service.WriteAsync(seed.Owner, seed.AttemptId, 0, "batch",
                "in-memory-handle", "in-memory transcript", null, true, CancellationToken.None);

            Assert.False(first.Duplicate);
            Assert.True(replay.Duplicate);
            Assert.Equal(first.EvidenceId, replay.EvidenceId);
            Assert.Equal(1, await db.NoteAsrSegmentEvidence.CountAsync(x =>
                x.OwnerId == owner && x.AttemptId == seed.AttemptId));
            var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.AttemptId);
            Assert.Equal(1, attempt.CompletedSegments);
            var state = await tasks.ReadAsync(owner, seed.TaskId, CancellationToken.None);
            Assert.Equal("Succeeded", state.Steps[0].Current.Status);
            Assert.Equal(first.EvidenceId, state.Steps[0].Current.EvidenceId);
        }
        finally
        {
            CleanupStorage(owner);
        }
    }

    private static NoteAsrBatchService Service(PimDbContext db, Guid owner, SyntheticBatchFactory factory) =>
        new(db, new FixedUser(owner), Tasks(owner), Materials(owner),
            new NoteAsrEvidenceService(db, Tasks(owner), new FixedUser(owner)),
            new EfMimoBatchHandleSink(db), factory, new PassthroughSecrets(),
            Options.Create(new NoteAsrBatchOptions
            {
                AllowUnverifiedAudioBatch = true,
                MaxJsonlBytes = checked((int)MimoBatchDefaults.MaxJsonlBytes)
            }),
            presets: new AiAsrPresetRegistry([new MimoMultimodalPreset(_ => new HttpClient())]));

    private static async Task<NoteAsrBatchJobDto> StartAsync(NoteAsrBatchService service, Seed seed)
    {
        var expectedIds = Enumerable.Range(0, seed.Steps.Count).Select(NoteAsrBatchJsonlReader.CustomId).ToArray();
        await using var input = await NoteAsrBatchJsonlReader.GenerateFromPlanAsync(seed.Owner, seed.VersionId,
            seed.Steps, NoteAsrPlan.SamplesPerCall, "zh", seed.Model, seed.Prompt, Materials(seed.Owner),
            checked((int)MimoBatchDefaults.MaxJsonlBytes), expectedIds, Enumerable.Range(0, seed.Steps.Count).ToArray(),
            CancellationToken.None);
        return await service.StartAsync(new StartNoteAsrBatchRequest(seed.TaskId, seed.AttemptId,
            seed.ProfileId, "consent-1", true), input.Content, input.Length, "input.jsonl",
            CancellationToken.None);
    }

    private static async Task<Seed> SeedAsync(SyntheticDatabase database)
    {
        var owner = Guid.NewGuid();
        var material = await SeedMaterialAsync(owner);
        var taskId = Guid.NewGuid();
        var promptTemplate = AiAsrPromptTemplate.Normalize();
        var prompt = promptTemplate.Snapshot("zh");
        var profileId = Guid.NewGuid();
        var profileRevision = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = database.NewContext(owner))
        {
            var suffix = owner.ToString("N")[..8];
            db.Users.Add(new UserEntity
            {
                Id = owner, Username = "retry-tx-" + suffix, Email = suffix + "@example.invalid",
                PasswordHash = "synthetic", IsActive = true
            });
            db.AiModelProfiles.Add(new AiModelProfileEntity
            {
                Id = profileId, Revision = profileRevision, Name = "RetryTx " + suffix,
                NormalizedName = "retry-tx-" + suffix, BaseUrl = "https://api.example.invalid/v1/",
                Model = "mimo-v2.6-pro", Enabled = true,
                Capabilities = AiModelCapabilityFlags.Transcription, AsrPresetId = MimoMultimodalPreset.PresetId,
                AsrBatchBaseUrl = BatchOrigin.AbsoluteUri, ApiKeySecret = "synthetic-key",
                CreatedAt = now, UpdatedAt = now
            });
            db.AiModelRuntime.Add(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId, Revision = Guid.NewGuid(), Enabled = true,
                TranscriptionModelId = profileId
            });
            db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
            {
                Id = attemptId, OwnerId = owner, TaskId = taskId, NoteId = material.Version.Upload.NoteId,
                VersionId = material.Version.Upload.VersionId,
                InputSha256 = NoteMaterialManifestValidator.Hash(material.Version.InputJson),
                Route = NoteAsrRoute.CloudMultimodal, State = "Queued", ConfigRevision = Guid.NewGuid(),
                ConsentVersion = "consent-1", ModelId = profileId, ModelRevision = profileRevision,
                PresetId = MimoMultimodalPreset.PresetId, PresetVersion = "1", Model = "mimo-v2.6-pro",
                Endpoint = "https://api.example.invalid/v1/chat/completions", ExecutionMode = "batch",
                PromptTemplate = promptTemplate.Text, PromptTemplateSha256 = promptTemplate.Sha256,
                PromptTemplateRevision = promptTemplate.Revision, PromptRendered = prompt.Text,
                PromptSha256 = prompt.Sha256, PromptRevision = prompt.Revision,
                FencingToken = 7, ExpectedSegments = 2, Language = "zh"
            });
            await db.SaveChangesAsync();
        }

        var consent = new AiAsrDestinationConsent(profileId, profileRevision,
            "https://api.example.invalid/v1/chat/completions", MimoMultimodalPreset.PresetId, "1", true);
        var route = new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal, Guid.NewGuid(),
            "consent-1", true, NoteAsrExecutionMode.Batch, true);
        var request = new StartNoteAsrRequest(taskId, owner, material.Version.Upload.VersionId, consent, "zh", route);
        var tasks = Tasks(owner);
        var state = await tasks.CreateAsync(owner, request, material.Version,
            NoteAsrPlan.Build(material.Version, NoteAsrPlan.SamplesPerCall), route, "batch",
            NoteAsrPlan.SamplesPerCall, CancellationToken.None);
        await tasks.UpdateAsync(owner, taskId, current =>
        {
            current.AttemptId = attemptId;
            current.ExecutionMode = "batch";
        }, CancellationToken.None);

        await using (var db = database.NewContext(owner))
        {
            var attempt = await db.NoteAsrAttempts.SingleAsync(x => x.Id == attemptId);
            attempt.ConfigRevision = state.RouteConfigRevision;
            await db.SaveChangesAsync();
        }
        return new Seed(owner, profileId, taskId, attemptId, material.Version.Upload.VersionId,
            "mimo-v2.6-pro", prompt.Text, state.Steps);
    }

    private static async Task<Seed> SeedInMemoryAsync(PimDbContext db, Guid owner)
    {
        var material = await SeedMaterialAsync(owner);
        var taskId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profileRevision = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var promptTemplate = AiAsrPromptTemplate.Normalize();
        var prompt = promptTemplate.Snapshot("zh");
        db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
        {
            Id = attemptId, OwnerId = owner, TaskId = taskId, NoteId = material.Version.Upload.NoteId,
            VersionId = material.Version.Upload.VersionId,
            InputSha256 = NoteMaterialManifestValidator.Hash(material.Version.InputJson),
            Route = NoteAsrRoute.CloudMultimodal, State = "Queued", ConfigRevision = Guid.NewGuid(),
            ConsentVersion = "consent-1", ModelId = profileId, ModelRevision = profileRevision,
            PresetId = MimoMultimodalPreset.PresetId, PresetVersion = "1", Model = "mimo-v2.6-pro",
            Endpoint = "https://api.example.invalid/v1/chat/completions", ExecutionMode = "batch",
            PromptTemplate = promptTemplate.Text, PromptTemplateSha256 = promptTemplate.Sha256,
            PromptTemplateRevision = promptTemplate.Revision, PromptRendered = prompt.Text,
            PromptSha256 = prompt.Sha256, PromptRevision = prompt.Revision,
            FencingToken = 7, ExpectedSegments = 2, Language = "zh"
        });
        await db.SaveChangesAsync();

        var consent = new AiAsrDestinationConsent(profileId, profileRevision,
            "https://api.example.invalid/v1/chat/completions", MimoMultimodalPreset.PresetId, "1", true);
        var route = new NoteAsrRouteConsent(NoteAsrRoute.CloudMultimodal, Guid.NewGuid(),
            "consent-1", true, NoteAsrExecutionMode.Batch, true);
        var request = new StartNoteAsrRequest(taskId, owner, material.Version.Upload.VersionId,
            consent, "zh", route);
        var tasks = Tasks(owner);
        var state = await tasks.CreateAsync(owner, request, material.Version,
            NoteAsrPlan.Build(material.Version, NoteAsrPlan.SamplesPerCall), route, "batch",
            NoteAsrPlan.SamplesPerCall, CancellationToken.None);
        await tasks.UpdateAsync(owner, taskId, current =>
        {
            current.AttemptId = attemptId;
            current.ExecutionMode = "batch";
        }, CancellationToken.None);
        return new Seed(owner, profileId, taskId, attemptId, material.Version.Upload.VersionId,
            "mimo-v2.6-pro", prompt.Text, state.Steps);
    }

    private static async Task<(NoteMaterialVersionDto Version, List<byte[]> Content)> SeedMaterialAsync(Guid owner)
    {
        var versionId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        const string text = "synthetic retry transaction fixture";
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id = noteId.ToString("D"), title = "Retry transactions",
            revision = 1, createdAtMillis = 1,
            blocks = new[] { new { id = Guid.NewGuid().ToString("D"), kind = "markdown", text } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var recordingJson = JsonSerializer.Serialize(new
        {
            id = recordingId.ToString("D"), schemaVersion = 1, createdAtMillis = 1,
            noteId = noteId.ToString("D"), phase = "Completed", currentSegment = 1, problem = (string?)null,
            chunks = new[]
            {
                new { segment = 1, index = 1, samples = 160 },
                new { segment = 1, index = 2, samples = 160 }
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var noteDoc = JsonDocument.Parse(noteJson);
        using var recordingDoc = JsonDocument.Parse(recordingJson);
        var inputJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id = versionId.ToString("D"), createdAtMillis = 1,
            note = noteDoc.RootElement.Clone(), recordings = new[] { recordingDoc.RootElement.Clone() }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var chunks = new[] { SyntheticWav(160, 2_000), SyntheticWav(160, 8_000) };
        var content = new List<byte[]>
        {
            Encoding.UTF8.GetBytes(noteJson), Encoding.UTF8.GetBytes(text),
            Encoding.UTF8.GetBytes(recordingJson), chunks[0], chunks[1]
        };
        var paths = new[]
        {
            "note.json", "document.md", $"recordings/{recordingId:D}/manifest.json",
            $"recordings/{recordingId:D}/s0001-c000001.wav",
            $"recordings/{recordingId:D}/s0001-c000002.wav"
        };
        var kinds = new[] { "Document", "Markdown", "RecordingManifest", "AudioChunk", "AudioChunk" };
        var files = paths.Select((path, index) => new
        {
            path, kind = kinds[index], sha256 = Hash(content[index]), sizeBytes = content[index].Length,
            blockId = (string?)null, recordingId = index >= 2 ? recordingId.ToString("D") : null,
            segment = index >= 3 ? (int?)1 : null, chunkIndex = index >= 3 ? (int?)(index - 2) : null,
            samples = index >= 3 ? (long?)160 : null
        }).ToArray();
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, versionId = versionId.ToString("D"), noteId = noteId.ToString("D"),
            revision = 1, inputSha256 = NoteMaterialManifestValidator.Hash(inputJson), files
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        CleanupStorage(owner);
        var store = Materials(owner);
        await store.BeginAsync(owner, new BeginNoteMaterialUploadRequest(versionId, noteId, Guid.NewGuid(),
            inputJson, manifestJson, true));
        for (var file = 0; file < content.Count; file++)
        {
            await store.UploadPartAsync(owner, versionId, file, 0, Hash(content[file]),
                new MemoryStream(content[file], writable: false));
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

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string StorageRoot(Guid owner) =>
        Path.Combine(Path.GetTempPath(), "pim-asr516-retry-transactions", owner.ToString("D"));

    private static NoteMaterialUploadStore Materials(Guid owner) =>
        new(Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot(owner) }));

    private static NoteAsrTaskStore Tasks(Guid owner) =>
        new(Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot(owner) }));

    private static void CleanupStorage(Guid owner)
    {
        var path = StorageRoot(owner);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static HttpResponseMessage UploadOk() => JsonResponse(HttpStatusCode.OK, new
    {
        id = "file-in-retry", purpose = "batch", filename = "input.jsonl", expire_at = 1780000000L
    });

    private static HttpResponseMessage BatchOk(string id) => JsonResponse(HttpStatusCode.OK, new
    {
        id, status = "validating", expires_at = 1780009000L, output_file_id = (string?)null,
        error_file_id = (string?)null, request_counts = new { total = 0, completed = 0, failed = 0 }
    });

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private sealed record Seed(Guid Owner, Guid ProfileId, Guid TaskId, Guid AttemptId, Guid VersionId,
        string Model, string Prompt, List<NoteAsrStep> Steps);

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

    private sealed class SyntheticBatchFactory(
        Func<string, CancellationToken, Task<HttpResponseMessage>> upload,
        Func<string, CancellationToken, Task<HttpResponseMessage>> create,
        Action? providerBoundary = null) : IMimoBatchClientFactory
    {
        private int _uploads;
        private int _creates;
        public int UploadCalls => Volatile.Read(ref _uploads);
        public int CreateCalls => Volatile.Read(ref _creates);

        public IMimoBatchClientLease Create(Uri baseAddress, string apiKey)
        {
            Assert.Equal(BatchOrigin, baseAddress);
            Assert.Equal("synthetic-key", apiKey);
            var http = new HttpClient(new Handler(this, upload, create, providerBoundary), disposeHandler: true)
            {
                BaseAddress = baseAddress,
                Timeout = Timeout.InfiniteTimeSpan
            };
            return new Lease(http, new MimoBatchClient(http,
                new MimoBatchClientOptions(baseAddress, TimeSpan.FromSeconds(5), AudioBatchEnabled: true)));
        }

        private sealed class Lease(HttpClient http, MimoBatchClient client) : IMimoBatchClientLease
        {
            public HttpClient Http { get; } = http;
            public MimoBatchClient Client { get; } = client;
            public void Dispose() => Http.Dispose();
        }

        private sealed class Handler(
            SyntheticBatchFactory owner,
            Func<string, CancellationToken, Task<HttpResponseMessage>> upload,
            Func<string, CancellationToken, Task<HttpResponseMessage>> create,
            Action? providerBoundary) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                providerBoundary?.Invoke();
                var path = request.RequestUri?.AbsolutePath ?? "";
                if (request.Method == HttpMethod.Post && path.EndsWith("/v1/files", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref owner._uploads);
                    return upload(path, cancellationToken);
                }
                if (request.Method == HttpMethod.Post && path.EndsWith("/v1/batches", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref owner._creates);
                    return create(path, cancellationToken);
                }
                throw new InvalidOperationException("Unexpected synthetic Batch request " + path);
            }
        }
    }

    private sealed class SyntheticDatabase : IAsyncDisposable
    {
        private readonly string _database;
        private readonly NpgsqlConnection _admin;
        private readonly string _connectionString;

        private SyntheticDatabase(string database, NpgsqlConnection admin, string connectionString)
        {
            _database = database;
            _admin = admin;
            _connectionString = connectionString;
        }

        public static async Task<SyntheticDatabase> CreateAsync()
        {
            var adminConnectionString = ResolveAdminConnectionString();
            var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            var database = "pim_asr516_retry_tx_" + Guid.NewGuid().ToString("N");
            var databaseCreated = false;
            try
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
                await create.ExecuteNonQueryAsync();
                databaseCreated = true;
                var scoped = new NpgsqlConnectionStringBuilder(adminConnectionString)
                {
                    Database = database
                }.ConnectionString;
                var instance = new SyntheticDatabase(database, admin, scoped);
                await using var db = instance.NewContext();
                await db.Database.MigrateAsync();
                return instance;
            }
            catch
            {
                try
                {
                    if (databaseCreated)
                    {
                        NpgsqlConnection.ClearAllPools();
                        await using var drop = new NpgsqlCommand(
                            $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
                        await drop.ExecuteNonQueryAsync();
                    }
                }
                catch
                {
                    // Preserve the original migration/setup failure; the temporary CI cluster is disposable.
                }
                finally
                {
                    await admin.DisposeAsync();
                }
                throw;
            }
        }

        public PimDbContext NewContext(Guid? user = null)
        {
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseNpgsql(_connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
                .Options;
            return new PimDbContext(options, user is null ? null : new FixedUser(user.Value));
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", _admin))
                await drop.ExecuteNonQueryAsync();
            await _admin.DisposeAsync();
        }

        private static string ResolveAdminConnectionString()
        {
            var local = Environment.GetEnvironmentVariable("PIM_ASR516_PG");
            if (!string.IsNullOrWhiteSpace(local))
            {
                VerifyReachable(local);
                return local;
            }

            RealDbTestConnection.SkipIfUnavailable();
            return RealDbTestConnection.Require();
        }

        private static void VerifyReachable(string connectionString)
        {
            try
            {
                using var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                using var command = new NpgsqlCommand("SELECT 1", connection);
                command.ExecuteScalar();
            }
            catch (Exception error) when (RealDbTestConnection.IsServerUnreachable(error))
            {
                throw new Xunit.SkipException($"RealDb unreachable: {error.Message}");
            }
        }
    }
}

[CollectionDefinition("NoteAsrRetryingTransactionsPg", DisableParallelization = true)]
public sealed class NoteAsrRetryingTransactionsPgCollection;
