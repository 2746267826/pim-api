using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Local;
using Pim.UnitTests.Ai;
using Xunit;

namespace Pim.UnitTests.Api;

public sealed class AiAsrEndpointFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-asr-http", Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<Program> Factory { get; }
    public AiAsrEndpointFixture() => Factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("asr-http"))
        .WithWebHostBuilder(builder => builder.UseEnvironment("Test").UseSetting("DataProtection:KeysPath", Path.Combine(_root,"keys"))
            .UseSetting("ManagedStorage:Root", Path.Combine(_root,"files")));
    public void Dispose() { Factory.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root,true); }
}

public sealed class AiAsrEndpointsE2ETests(AiAsrEndpointFixture host) : IClassFixture<AiAsrEndpointFixture>
{
    [Fact]
    public async Task CatalogueAndRealHttpFlowUseSdkPreset_WithBuiltinFilesOwnerOnlyAndDigestProtection()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = builder.Build(); var calls = 0;
        provider.MapPost("/v1/chat/completions", async (Microsoft.AspNetCore.Http.HttpRequest incoming) => {
            Interlocked.Increment(ref calls); using var json = await JsonDocument.ParseAsync(incoming.Body); var root=json.RootElement;
            Assert.Equal("mimo-v2.5-asr",root.GetProperty("model").GetString()); Assert.Equal("zh",root.GetProperty("asr_options").GetProperty("language").GetString());
            var messages=root.GetProperty("messages"); Assert.Equal(1,messages.GetArrayLength());
            var content=messages[0].GetProperty("content"); Assert.Equal(1,content.GetArrayLength());
            Assert.Equal("input_audio",content[0].GetProperty("type").GetString());
            Assert.Equal(Convert.ToBase64String(AiAsrPresetTests.Wave()),content[0].GetProperty("input_audio").GetProperty("data").GetString());
            return Microsoft.AspNetCore.Http.Results.Json(new { id="synthetic-asr-http", @object="chat.completion", created=1780000000, model="mimo-v2.5-asr",
                choices=new[]{new { index=0,finish_reason="stop",message=new { role="assistant",content="本机HTTP转写结果"}}},
                usage=new {prompt_tokens=10,completion_tokens=5,total_tokens=15,seconds=3} });
        });
        await provider.StartAsync();
        var owner=await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,"asr-owner-"+Guid.NewGuid().ToString("N"),"admin");
        using var client=AuthRegistrationTestHost.Authed(host.Factory,owner.AccessToken);
        var catalogue=await Data<AiAsrPresetDto[]>(await client.GetAsync("/api/v1/ai/asr-presets"));
        var legacyPreset=Assert.Single(catalogue.Where(x=>x.Id=="mimo-asr"));
        var model=await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",new SaveAiModelRequest("ASR配置"+Guid.NewGuid(),
            provider.Urls.Single()+"/v1/chat/completions","mimo-v2.5-asr",true,[AiModelCapability.Transcription],30,123,"synthetic-asr-http-key",AsrPresetId:legacyPreset.Id)));
        Assert.EndsWith("/v1/",model.BaseUrl); Assert.Equal("mimo-asr",model.AsrPresetId);
        var runtime=await Data<AiModelSettingsDto>(await client.GetAsync("/api/v1/ai/model-settings"));
        await Data<AiModelSettingsDto>(await client.PutAsJsonAsync("/api/v1/ai/model-settings",new SaveAiModelSettingsRequest(runtime.Revision,true,null,null,model.Id)));
        var request=new AiAsrCallRequest(Guid.NewGuid(),owner.UserId,new("语音.wav","wav",Convert.ToBase64String(AiAsrPresetTests.Wave())),
            new(model.Id,model.Revision,model.BaseUrl+legacyPreset.OperationPath,legacyPreset.Id,legacyPreset.Version,true),"zh");
        var result=await Data<AiTextCallResultDto>(await client.PostAsJsonAsync("/api/v1/ai/asr-calls",request));
        Assert.Equal(AiRequestStatus.Succeeded,result.Status); Assert.Equal("本机HTTP转写结果",result.ResponseText); Assert.Equal(1,calls);
        var detail=await Data<AiRequestLogDetailDto>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}"));
        Assert.Equal("ai.asr-call",detail.Purpose); Assert.Equal(15,detail.Usage.TotalTokens); Assert.DoesNotContain("synthetic-asr-http-key",JsonSerializer.Serialize(detail));
        var audio=await Data<AiCallAudioDto>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}/audio")); Assert.True(audio.Stored);
        Assert.Contains("pim-ai-audio:"+audio.Id,detail.RequestPayloadJson);
        var bytes=await client.GetAsync($"/api/v1/ai/requests/{result.LogId}/audio/content"); Assert.Equal(HttpStatusCode.OK,bytes.StatusCode);
        Assert.True(bytes.Headers.CacheControl!.NoStore); Assert.Equal(AiAsrPresetTests.Wave(),await bytes.Content.ReadAsByteArrayAsync());
        Assert.True((await Data<AiTextCallResultDto>(await client.PostAsJsonAsync("/api/v1/ai/asr-calls",request))).Replayed); Assert.Equal(1,calls);
        Assert.True((await Data<AiTextCallResultDto>(await client.GetAsync($"/api/v1/ai/asr-calls/{request.ClientRequestId}"))).Replayed);
        var other=await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,"asr-other-admin-"+Guid.NewGuid().ToString("N"),"admin");
        using var otherClient=AuthRegistrationTestHost.Authed(host.Factory,other.AccessToken);
        foreach(var suffix in new[]{"audio","audio/content"}) Assert.Equal(HttpStatusCode.NotFound,(await otherClient.GetAsync($"/api/v1/ai/requests/{result.LogId}/{suffix}")).StatusCode);
        using(var scope=host.Factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<PimDbContext>(); var stored=await db.AiCallAudio.AsNoTracking().SingleAsync(x=>x.LogId==result.LogId);
            var account=await db.Set<FileProviderEntity>().SingleAsync(x=>x.Id==stored.ProviderId); Assert.Equal("local",account.Provider);
            using var changed=new MemoryStream("changed"u8.ToArray());
            await scope.ServiceProvider.GetRequiredService<LocalFileStore>().ReplaceAsync(owner.UserId,account.Id,stored.ObjectKey!,changed,"audio/wav",4*1024*1024);
        }
        Assert.Equal(HttpStatusCode.Conflict,(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}/audio/content")).StatusCode);
        Assert.Equal(result.ResponseText,(await Data<AiRequestLogDetailDto>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}"))).ResponseText);
    }

    [Fact]
    public async Task Mimo_50MiB_contract_accepts_real_http_body_above_legacy_7MiB_and_saved_audio_reads_back()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 80L * 1024 * 1024);
        await using var provider = builder.Build();
        provider.MapPost("/v1/chat/completions", async (Microsoft.AspNetCore.Http.HttpRequest incoming) =>
        {
            using var json = await JsonDocument.ParseAsync(incoming.Body);
            var root = json.RootElement;
            Assert.Equal("mimo-v2.6-flash", root.GetProperty("model").GetString());
            var content = root.GetProperty("messages")[0].GetProperty("content");
            Assert.StartsWith("data:audio/wav;base64,",
                content[0].GetProperty("input_audio").GetProperty("data").GetString());
            Assert.Contains("language=zh", content[1].GetProperty("text").GetString());
            return Microsoft.AspNetCore.Http.Results.Json(new
            {
                choices = new[] { new { finish_reason = "stop",
                    message = new { role = "assistant", content = "large synthetic transcript" } } },
                usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15,
                    prompt_tokens_details = new { audio_tokens = 5 } }
            });
        });
        await provider.StartAsync();
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "asr-large-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var presets = await Data<AiAsrPresetDto[]>(await client.GetAsync("/api/v1/ai/asr-presets"));
        var mimo = Assert.Single(presets.Where(x => x.Id == "mimo-multimodal"));
        var model = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("MiMo-large-" + Guid.NewGuid(), provider.Urls.Single() + "/v1/",
                "mimo-v2.6-flash", true, [AiModelCapability.Transcription], 60, 123,
                "synthetic-mimo-large-key", AsrPresetId: mimo.Id)));
        var runtime = await Data<AiModelSettingsDto>(await client.GetAsync("/api/v1/ai/model-settings"));
        await Data<AiModelSettingsDto>(await client.PutAsJsonAsync("/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(runtime.Revision, true, null, null, model.Id)));
        var audio = BuildWave(9 * 1024 * 1024);
        var request = new AiAsrCallRequest(Guid.NewGuid(), owner.UserId,
            new AiAsrAudioRequest("large.wav", "wav", Convert.ToBase64String(audio)),
            new AiAsrDestinationConsent(model.Id, model.Revision,
                AiAsrPresetRegistry.OperationEndpoint(model.BaseUrl, mimo), mimo.Id, mimo.Version, true), "zh");

        var result = await Data<AiTextCallResultDto>(
            await client.PostAsJsonAsync("/api/v1/ai/asr-calls", request));

        Assert.True(result.Status == AiRequestStatus.Succeeded,
            $"Large MiMo call failed: {result.ErrorMessage}");
        var bytes = await client.GetAsync($"/api/v1/ai/requests/{result.LogId}/audio/content");
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal(audio, await bytes.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AsrAndOriginalAudioRoutesRequireAdministrator()
    {
        using var anonymous=host.Factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/v1/ai/asr-presets")).StatusCode);
        var ordinary=await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,"asr-user-"+Guid.NewGuid().ToString("N"));
        using var client=AuthRegistrationTestHost.Authed(host.Factory,ordinary.AccessToken);
        foreach(var path in new[]{"/api/v1/ai/asr-presets",$"/api/v1/ai/asr-calls/{Guid.NewGuid()}",$"/api/v1/ai/requests/{Guid.NewGuid()}/audio",$"/api/v1/ai/requests/{Guid.NewGuid()}/audio/content"})
            Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/v1/ai/asr-calls",new{})).StatusCode);
    }
    private static async Task<T> Data<T>(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.OK,response.StatusCode); return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!; }

    private static byte[] BuildWave(int dataBytes)
    {
        dataBytes -= dataBytes % 2;
        var bytes = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 16_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 32_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)dataBytes);
        for (var i = 44; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }
}
