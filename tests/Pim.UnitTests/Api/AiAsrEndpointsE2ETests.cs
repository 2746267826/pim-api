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
        Assert.Single(catalogue); Assert.Equal("mimo-asr",catalogue[0].Id);
        var model=await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",new SaveAiModelRequest("ASR配置"+Guid.NewGuid(),
            provider.Urls.Single()+"/v1/chat/completions","mimo-v2.5-asr",true,[AiModelCapability.Transcription],30,123,"synthetic-asr-http-key",AsrPresetId:catalogue[0].Id)));
        Assert.EndsWith("/v1/",model.BaseUrl); Assert.Equal("mimo-asr",model.AsrPresetId);
        var runtime=await Data<AiModelSettingsDto>(await client.GetAsync("/api/v1/ai/model-settings"));
        await Data<AiModelSettingsDto>(await client.PutAsJsonAsync("/api/v1/ai/model-settings",new SaveAiModelSettingsRequest(runtime.Revision,true,null,null,model.Id)));
        var request=new AiAsrCallRequest(Guid.NewGuid(),owner.UserId,new("语音.wav","wav",Convert.ToBase64String(AiAsrPresetTests.Wave())),
            new(model.Id,model.Revision,model.BaseUrl+catalogue[0].OperationPath,catalogue[0].Id,catalogue[0].Version,true),"zh");
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
}
