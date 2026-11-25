using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 管理端 AI/ASR 配置的真实 HTTP + 隔离 PostgreSQL 回归。重点证明三件事：
/// 混合能力在服务端被拒绝、留空 Key 不会重写已保存密文、MiMo 提示词可以真正清空并随预设切换清理。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class AiModelSettingsRealDbE2ETests
{
    [SkippableFact]
    public async Task Http_configuration_rejects_mixed_capabilities_and_preserves_or_clears_secrets_by_intent()
    {
        var temp = await TempMigrationDatabase.CreateAsync("test_ai_model_settings_http");
        await using var database = temp;
        await temp.MigrateAsync();
        var adminId = await SeedAdminAsync(temp);
        var keysPath = Path.Combine(Path.GetTempPath(), $"pim-ai-model-dp-{Guid.NewGuid():N}");
        try
        {
            using var factory = CreateFactory(temp.ConnectionString, keysPath);
            using var client = Authed(factory, adminId);
            var suffix = Guid.NewGuid().ToString("N")[..8];

            var mixed = await client.PostAsJsonAsync("/api/v1/ai/models", new SaveAiModelRequest(
                "混合能力-" + suffix, "https://mixed.invalid/v1", "mixed-model", true,
                [AiModelCapability.Text, AiModelCapability.Transcription], 30, 123,
                "synthetic-mixed-key"));
            Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
            var mixedError = await mixed.Content.ReadFromJsonAsync<ApiResponse<AiModelDto>>();
            Assert.Equal(40001, mixedError!.Code);
            Assert.Contains("独立ASR", mixedError.Message);
            Assert.Equal(0, await temp.Db.AiModelProfiles.CountAsync());

            var text = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
                new SaveAiModelRequest("留空密钥-" + suffix, "https://blank-key.invalid/v1",
                    "text-model", true, [AiModelCapability.Text], 30, 123,
                    "synthetic-original-key")));
            temp.Db.ChangeTracker.Clear();
            var originalSecret = (await temp.Db.AiModelProfiles.AsNoTracking()
                .SingleAsync(x => x.Id == text.Id)).ApiKeySecret;
            Assert.NotEmpty(originalSecret);
            Assert.DoesNotContain("synthetic-original-key", originalSecret);

            var blankKey = await Data<AiModelDto>(await client.PutAsJsonAsync(
                $"/api/v1/ai/models/{text.Id}", new SaveAiModelRequest(text.Name, text.BaseUrl,
                    text.Model, true, text.Capabilities, 30, 512, ApiKey: "",
                    ExpectedRevision: text.Revision)));
            Assert.True(blankKey.HasApiKey);
            temp.Db.ChangeTracker.Clear();
            var preserved = await temp.Db.AiModelProfiles.AsNoTracking()
                .SingleAsync(x => x.Id == text.Id);
            Assert.Equal(originalSecret, preserved.ApiKeySecret);

            var presets = await Data<AiAsrPresetDto[]>(
                await client.GetAsync("/api/v1/ai/asr-presets"));
            var multimodal = Assert.Single(presets, x => x.Id == "mimo-multimodal");
            var legacy = Assert.Single(presets, x => x.Id == "mimo-asr");

            var asr = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
                new SaveAiModelRequest("MiMo提示词-" + suffix, multimodal.DefaultBaseUrl,
                    multimodal.DefaultModel, true, [AiModelCapability.Transcription], 30, 123,
                    "synthetic-mimo-key", AsrPresetId: multimodal.Id,
                    AsrPrompt: "  自定义转写提示词  ")));
            Assert.Equal("自定义转写提示词", asr.AsrPrompt);
            temp.Db.ChangeTracker.Clear();
            var mimoSecret = (await temp.Db.AiModelProfiles.AsNoTracking()
                .SingleAsync(x => x.Id == asr.Id)).ApiKeySecret;

            var switched = await Data<AiModelDto>(await client.PutAsJsonAsync(
                $"/api/v1/ai/models/{asr.Id}", new SaveAiModelRequest(asr.Name, asr.BaseUrl,
                    legacy.DefaultModel, true, asr.Capabilities, 30, 123,
                    ApiKey: "synthetic-legacy-key", ExpectedRevision: asr.Revision,
                    AsrPresetId: legacy.Id)));
            Assert.Equal(legacy.Id, switched.AsrPresetId);
            Assert.Null(switched.AsrPrompt);
            temp.Db.ChangeTracker.Clear();
            var legacyRow = await temp.Db.AiModelProfiles.AsNoTracking()
                .SingleAsync(x => x.Id == asr.Id);
            Assert.Null(legacyRow.AsrPrompt);
            Assert.NotEqual(mimoSecret, legacyRow.ApiKeySecret);

            var backToMimo = await Data<AiModelDto>(await client.PutAsJsonAsync(
                $"/api/v1/ai/models/{asr.Id}", new SaveAiModelRequest(switched.Name,
                    switched.BaseUrl, multimodal.DefaultModel, true, switched.Capabilities, 30, 123,
                    ApiKey: "second-mimo-credential", ExpectedRevision: switched.Revision,
                    AsrPresetId: multimodal.Id, AsrPrompt: "再次自定义提示词")));
            Assert.Equal("再次自定义提示词", backToMimo.AsrPrompt);

            var cleared = await Data<AiModelDto>(await client.PutAsJsonAsync(
                $"/api/v1/ai/models/{asr.Id}", new SaveAiModelRequest(backToMimo.Name,
                    backToMimo.BaseUrl, backToMimo.Model, true, backToMimo.Capabilities, 30, 123,
                    ApiKey: "", ExpectedRevision: backToMimo.Revision, AsrPrompt: "")));
            Assert.Null(cleared.AsrPrompt);
            temp.Db.ChangeTracker.Clear();
            var clearedRow = await temp.Db.AiModelProfiles.AsNoTracking()
                .SingleAsync(x => x.Id == asr.Id);
            Assert.Null(clearedRow.AsrPrompt);
        }
        finally
        {
            if (Directory.Exists(keysPath)) Directory.Delete(keysPath, true);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString,
        string keysPath) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("DisableHangfire", "true");
        builder.UseSetting("Database:Migrations:FailFast", "false");
        builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("DataProtection:KeysPath", keysPath);
    });

    private static async Task<Guid> SeedAdminAsync(TempMigrationDatabase temp)
    {
        var user = new UserEntity
        {
            Username = "ai-model-admin-" + Guid.NewGuid().ToString("N"),
            Email = "ai-model-admin@example.test",
            Role = "admin",
            IsActive = true
        };
        temp.Db.Users.Add(user);
        await temp.Db.SaveChangesAsync();
        return user.Id;
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, Guid userId)
    {
        var jwt = factory.Services.GetRequiredService<JwtService>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", jwt.GenerateAccessToken(userId, $"ai-model-{userId:N}", "admin"));
        return client;
    }

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }
}
