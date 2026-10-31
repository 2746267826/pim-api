using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Ai;
using Pim.Core.Storage;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Ai.Asr.Mimo;

public sealed class AiAsrCallServiceMimoIntegrationTests
{
    private readonly PimDbContext _db = new(new DbContextOptionsBuilder<PimDbContext>()
        .UseInMemoryDatabase("mimo-call-" + Guid.NewGuid()).Options);
    private readonly Owner _owner = new();
    private readonly Files _files = new();
    private readonly ISecretProtector _secrets =
        new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task Named_client_pipeline_captures_actual_wire_request_and_keeps_parser_wrapper()
    {
        var handler = new StubHandler(() => JsonResponse("""
            {"choices":[{"message":{"content":"0-1 | 说话人: 你好"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":8,"completion_tokens":4,"total_tokens":12,
             "prompt_tokens_details":{"audio_tokens":5}}}
            """));
        await SeedAsync();
        using var provider = BuildProvider(handler);
        var registry = provider.GetRequiredService<AiAsrPresetRegistry>();
        var service = Service(registry);

        var result = await service.CallAsync(Request(registry), CancellationToken.None);

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal(1, handler.Calls);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        using var request = JsonDocument.Parse(row.RequestPayloadJson);
        var requestRoot = request.RootElement;
        Assert.Equal(MimoMultimodalPreset.MaxCompletionTokens,
            requestRoot.GetProperty("max_completion_tokens").GetInt32());
        var content = requestRoot.GetProperty("messages")[0].GetProperty("content");
        Assert.Contains("逐字转写", content[1].GetProperty("text").GetString());
        Assert.StartsWith("pim-ai-audio:", content[0].GetProperty("input_audio").GetProperty("data").GetString());

        using var metadata = JsonDocument.Parse(row.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("requestBodyCaptured").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("responseBodyComplete").GetBoolean());

        using var response = JsonDocument.Parse(row.ResponseRawJson);
        Assert.Equal("asr-call-response-v1", response.RootElement.GetProperty("recordFormat").GetString());
        Assert.Equal("preset-parser",
            response.RootElement.GetProperty("sources").GetProperty("parsed").GetString());
        Assert.Equal("parsed.upstream",
            response.RootElement.GetProperty("sources").GetProperty("raw").GetString());
        var parsed = response.RootElement.GetProperty("parsed");
        Assert.Equal("mimo-multimodal-v1", parsed.GetProperty("recordFormat").GetString());
        Assert.Equal("content", parsed.GetProperty("contentSource").GetString());
        Assert.Equal(5, parsed.GetProperty("audioTokens").GetInt64());
        Assert.Equal("你好", parsed.GetProperty("transcript").GetProperty("segments")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Missing_audio_tokens_keeps_provider_reason_and_wire_body_visible()
    {
        var handler = new StubHandler(() => JsonResponse("""
            {"choices":[{"message":{"content":"unverified"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":8,"completion_tokens":1,"total_tokens":9}}
            """));
        await SeedAsync();
        using var provider = BuildProvider(handler);
        var registry = provider.GetRequiredService<AiAsrPresetRegistry>();
        var service = Service(registry);

        var result = await service.CallAsync(Request(registry), CancellationToken.None);

        Assert.Equal(AiRequestStatus.Failed, result.Status);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("provider_50202", row.ErrorCode);
        Assert.Contains("audio_tokens", row.ErrorMessage);
        using var metadata = JsonDocument.Parse(row.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
        using var response = JsonDocument.Parse(row.ResponseRawJson);
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("parsed").ValueKind);
        Assert.Equal("http-wire",
            response.RootElement.GetProperty("sources").GetProperty("raw").GetString());
        Assert.Equal(9, response.RootElement.GetProperty("raw").GetProperty("usage")
            .GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Mimo_preset_allows_finite_request_larger_than_legacy_8MiB_capture()
    {
        var handler = new StubHandler(() => JsonResponse("""
            {"choices":[{"message":{"content":"long audio"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens_details":{"audio_tokens":4}}}
            """));
        await SeedAsync();
        using var provider = BuildProvider(handler);
        var registry = provider.GetRequiredService<AiAsrPresetRegistry>();
        var service = Service(registry);
        var request = Request(registry) with
        {
            Audio = new AiAsrAudioRequest("large.wav", "wav",
                Convert.ToBase64String(BuildWave(9 * 1024 * 1024)))
        };

        var result = await service.CallAsync(request, CancellationToken.None);

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal(1, handler.Calls);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        using var metadata = JsonDocument.Parse(row.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("requestBodyCaptured").GetBoolean());
    }

    private AiAsrCallService Service(AiAsrPresetRegistry registry) =>
        new(_db, _secrets, _owner, new AiTextCallGate(), registry,
            new AiAsrMaterialService(_db, _files, _owner, registry));

    private ServiceProvider BuildProvider(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<AiTextResponseLimitHandler>();
        services.AddHttpClient(AiProfileChatClientFactory.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpMessageHandler<AiTextResponseLimitHandler>();
        services.AddSingleton<IAiAsrPreset>(sp => new MimoMultimodalPreset(_ =>
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AiProfileChatClientFactory.ClientName)));
        services.AddSingleton(sp => new AiAsrPresetRegistry(sp.GetServices<IAiAsrPreset>()));
        return services.BuildServiceProvider();
    }

    private async Task SeedAsync()
    {
        var profile = new AiModelProfileEntity
        {
            Name = "synthetic-mimo", NormalizedName = "synthetic-mimo",
            BaseUrl = "https://api.synthetic.invalid/v1/", Model = MimoMultimodalPreset.FlashModel,
            Enabled = true, Capabilities = AiModelCapabilityFlags.Transcription,
            AsrPresetId = MimoMultimodalPreset.PresetId,
            ApiKeySecret = _secrets.Protect("synthetic-key"), TimeoutSeconds = 30
        };
        _db.Users.Add(new UserEntity
        {
            Id = _owner.UserId!.Value, Username = "mimo-synthetic", Email = "mimo@example.invalid",
            PasswordHash = "synthetic", Role = "admin", IsActive = true
        });
        _db.AiModelProfiles.Add(profile);
        _db.AiModelRuntime.Add(new AiModelRuntimeEntity
        {
            Enabled = true, TranscriptionModelId = profile.Id
        });
        await _db.SaveChangesAsync();
    }

    private AiAsrCallRequest Request(AiAsrPresetRegistry registry)
    {
        var profile = _db.AiModelProfiles.AsNoTracking().Single();
        var preset = registry.Resolve(profile.AsrPresetId).Descriptor;
        return new(Guid.NewGuid(), _owner.UserId!.Value,
            new("fixture.wav", "wav", Convert.ToBase64String(BuildWave(3200))),
            new(profile.Id, profile.Revision,
                AiAsrPresetRegistry.OperationEndpoint(profile.BaseUrl, preset),
                preset.Id, preset.Version, true),
            "zh", profile.Id);
    }

    private static byte[] BuildWave(int dataBytes)
    {
        var size = Math.Max(1, dataBytes);
        var bytes = new byte[44 + size];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), 36 + size);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 16_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 32_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), (short)2);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), (short)16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.TryWriteBytes(bytes.AsSpan(40), size);
        return bytes;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(response());
        }
    }

    private sealed class Owner : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Username => "mimo-synthetic";
        public string? Role => "admin";
    }

    private sealed class Files : IFileAttachmentStore
    {
        private readonly Dictionary<string, byte[]> _objects = [];
        public async Task<FileAttachmentRef> StoreAsync(Guid user, string key, Stream content, string mime,
            long size, CancellationToken ct = default)
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct);
            _objects[key] = memory.ToArray();
            return new(key, "local", user);
        }
        public Task<Stream> OpenReadAsync(Guid user, FileAttachmentRef reference, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(_objects[reference.ObjectKey]));
        public Task<string?> GetDirectLinkAsync(Guid user, FileAttachmentRef reference, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task DeleteAsync(Guid user, FileAttachmentRef reference, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
