using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Ai;

public sealed class AiConnectionTestPostgresTests
{
    private const string Key = "postgres-connection-test-key";

    [SkippableFact]
    public async Task PurposeColumnsAndConnectionTestPersistThroughRealMigrationAndPostgres()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_j08_connection");
        await database.MigrateAsync();
        var db = database.Db;
        var secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var owner = new Owner();
        db.Users.Add(new UserEntity
        {
            Id = owner.UserId!.Value,
            Username = "j08-postgres-" + Guid.NewGuid().ToString("N"),
            Email = "j08-postgres@example.invalid",
            Role = "admin"
        });
        var modelId = Guid.NewGuid();
        db.AiModelProfiles.Add(new AiModelProfileEntity
        {
            Id = modelId,
            Revision = Guid.NewGuid(),
            Name = "PostgreSQL 连接测试模型",
            NormalizedName = "POSTGRESQL 连接测试模型",
            BaseUrl = "https://fixture.invalid/v1/",
            Model = "fixture-model",
            Enabled = true,
            Capabilities = AiModelCapabilityFlags.Text,
            ApiKeySecret = secrets.Protect(Key),
            TimeoutSeconds = 5,
            MaxOutputTokens = 64
        });
        db.AiModelRuntime.Add(new AiModelRuntimeEntity
        {
            Id = AiModelRuntimeEntity.SingletonId,
            Revision = Guid.NewGuid(),
            Enabled = true,
            TextModelId = modelId,
            NoteTodoModelId = modelId
        });
        await db.SaveChangesAsync();

        var wire = new Wire();
        var gate = new AiTextCallGate();
        var service = new AiConnectionTestService(db, secrets, new AiProfileChatClientFactory(wire),
            owner, new AiAsrPresetRegistry([]), gate);
        var revision = await db.AiModelProfiles.AsNoTracking().Where(x => x.Id == modelId)
            .Select(x => x.Revision).SingleAsync();

        var result = await service.TestAsync(modelId,
            new AiConnectionTestRequest(Guid.NewGuid(), revision, AiModelCapability.Text), default);

        Assert.Equal(AiConnectionTestStatus.Succeeded, result.Status);
        Assert.True(result.ProviderAttempted);
        Assert.Equal(1, wire.Calls);
        var log = await db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("ai.connection-test", log.Purpose);
        Assert.Equal("succeeded", log.Status);
        Assert.Equal(modelId, log.ModelProfileId);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(log));

        var runtime = await db.AiModelRuntime.AsNoTracking().SingleAsync();
        Assert.Equal(modelId, runtime.NoteTodoModelId);
        Assert.Null(runtime.NoteOrganizationModelId);
    }

    private sealed class Owner : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Role => "admin";
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) =>
            new(new AiTextResponseLimitHandler { InnerHandler = this }, false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Calls++;
            var body = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-postgres-fixture",
                @object = "chat.completion",
                created = 1780000000,
                model = "fixture-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        finish_reason = "stop",
                        message = new { role = "assistant", content = "OK" }
                    }
                },
                usage = new { prompt_tokens = 2, completion_tokens = 1, total_tokens = 3 }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
