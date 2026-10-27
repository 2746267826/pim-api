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

public sealed class AiVisionEndpointFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-ai-vision-http", Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<Program> Factory { get; }
    public AiVisionEndpointFixture() => Factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("ai-vision-http"))
        .WithWebHostBuilder(builder => builder.UseEnvironment("Test")
            .UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"))
            .UseSetting("ManagedStorage:Root", Path.Combine(_root, "files")));
    public void Dispose() { Factory.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

public sealed class AiVisionEndpointsE2ETests(AiVisionEndpointFixture host) : IClassFixture<AiVisionEndpointFixture>
{
    [Fact]
    public async Task ActualSdkAndLoopbackHttp_SendBothSyntheticImages_StoredInBuiltinFiles_OwnerOnlyAndDigestProtected()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = builder.Build(); var calls = 0;
        provider.MapPost("/v1/chat/completions", async (Microsoft.AspNetCore.Http.HttpRequest incoming) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("Bearer synthetic-http-vision-key", incoming.Headers.Authorization.ToString());
            using var body = await JsonDocument.ParseAsync(incoming.Body);
            var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal("正文\npassword=普通材料", content[0].GetProperty("text").GetString());
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(AiVisionCallTests.FirstPng), content[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(AiVisionCallTests.SecondPng), content[2].GetProperty("image_url").GetProperty("url").GetString());
            return Microsoft.AspNetCore.Http.Results.Json(new { id = "synthetic-http-vision", @object = "chat.completion", created = 1780000000, model = "synthetic-vision",
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "本机HTTP视觉完整回执" } } },
                usage = new { prompt_tokens = 12, completion_tokens = 8, total_tokens = 20 } });
        });
        await provider.StartAsync();
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory, "vision-owner-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var model = await Model(client, provider.Urls.Single() + "/v1");
        var request = Request(owner.UserId, model);
        var result = await Data<AiTextCallResultDto>(await client.PostAsJsonAsync("/api/v1/ai/vision-calls", request));
        Assert.Equal(AiRequestStatus.Succeeded, result.Status); Assert.Equal(1, calls);
        var detail = await Data<AiRequestLogDetailDto>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}"));
        Assert.Equal("ai.vision-call", detail.Purpose); Assert.Equal("本机HTTP视觉完整回执", detail.ResponseText); Assert.Equal(20, detail.Usage.TotalTokens);
        Assert.DoesNotContain("data:image", detail.RequestPayloadJson); Assert.DoesNotContain("synthetic-http-vision-key", JsonSerializer.Serialize(detail));
        var images = await Data<AiCallImageDto[]>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}/images"));
        Assert.Equal(2, images.Length); Assert.All(images, image => Assert.True(image.Stored));
        var imageResponse = await client.GetAsync($"/api/v1/ai/requests/{result.LogId}/images/{images[0].Id}");
        Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode); Assert.True(imageResponse.Headers.CacheControl!.NoStore);
        Assert.Equal(AiVisionCallTests.FirstPng, await imageResponse.Content.ReadAsByteArrayAsync());
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory, "vision-other-admin-" + Guid.NewGuid().ToString("N"), "admin");
        using var otherClient = AuthRegistrationTestHost.Authed(host.Factory, other.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/ai/requests/{result.LogId}/images")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/ai/requests/{result.LogId}/images/{images[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/ai/requests/{Guid.NewGuid()}/images/{images[0].Id}")).StatusCode);
        Assert.True((await Data<AiTextCallResultDto>(await client.PostAsJsonAsync("/api/v1/ai/vision-calls", request))).Replayed);
        Assert.True((await Data<AiTextCallResultDto>(await client.GetAsync($"/api/v1/ai/vision-calls/{request.ClientRequestId}"))).Replayed);
        Assert.Equal(1, calls);
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var stored = await db.AiCallImages.AsNoTracking().SingleAsync(x => x.Id == images[0].Id);
            var account = await db.Set<FileProviderEntity>().SingleAsync(x => x.Id == stored.ProviderId);
            Assert.Equal("local", account.Provider); Assert.Equal(owner.UserId, account.UserId);
            var files = scope.ServiceProvider.GetRequiredService<LocalFileStore>();
            using var changed = new MemoryStream(AiVisionCallTests.SecondPng);
            await files.ReplaceAsync(owner.UserId, account.Id, stored.ObjectKey!, changed, "image/png", 4 * 1024 * 1024);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/v1/ai/requests/{result.LogId}/images/{images[0].Id}")).StatusCode);
        Assert.Equal("本机HTTP视觉完整回执", (await Data<AiRequestLogDetailDto>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}"))).ResponseText);
    }

    [Fact]
    public async Task AnonymousAndOrdinaryUsersCannotUseAnyVisionOrImageEndpoint()
    {
        using var anonymous = host.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/ai/vision-calls", new { })).StatusCode);
        var user = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory, "vision-user-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(host.Factory, user.AccessToken);
        foreach (var path in new[] { $"/api/v1/ai/vision-calls/{Guid.NewGuid()}", $"/api/v1/ai/requests/{Guid.NewGuid()}/images", $"/api/v1/ai/requests/{Guid.NewGuid()}/images/{Guid.NewGuid()}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/ai/vision-calls", new { })).StatusCode);
    }

    [Fact]
    public async Task MissingConsentOrChangedTargetOrIncompleteImageSetFailsBeforeTransport()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory, "vision-guard-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        // No server listens here: all following requests must be rejected before model transport.
        var model = await Model(client, "http://127.0.0.1:1/v1"); var request = Request(owner.UserId, model);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/ai/vision-calls", request with { Consent = request.Consent with { Confirmed = false } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/ai/vision-calls", request with { Consent = request.Consent with { Endpoint = "http://unapproved.invalid/" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/ai/vision-calls", request with { ImageCount = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/ai/vision-calls", request with { ExpectedUserId = Guid.NewGuid() })).StatusCode);
        using var scope = host.Factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await db.AiRequestLogs.AnyAsync(x => x.UserId == owner.UserId));
        Assert.False(await db.Set<FileProviderEntity>().AnyAsync(x => x.UserId == owner.UserId));
    }

    private static async Task<AiModelDto> Model(HttpClient client, string endpoint)
    {
        var model = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models", new SaveAiModelRequest("视觉HTTP-" + Guid.NewGuid(), endpoint,
            "synthetic-vision", true, [AiModelCapability.Vision], 10, 123, "synthetic-http-vision-key")));
        var settings = await Data<AiModelSettingsDto>(await client.GetAsync("/api/v1/ai/model-settings"));
        await Data<AiModelSettingsDto>(await client.PutAsJsonAsync("/api/v1/ai/model-settings", new SaveAiModelSettingsRequest(settings.Revision, true, null, model.Id, null)));
        return model;
    }
    private static AiVisionCallRequest Request(Guid owner, AiModelDto model) => new(Guid.NewGuid(), owner, "正文\npassword=普通材料", 2,
        [new("照片.png", "image/png", Convert.ToBase64String(AiVisionCallTests.FirstPng)), new("手写.png", "image/png", Convert.ToBase64String(AiVisionCallTests.SecondPng))],
        new(model.Id, model.Revision, model.BaseUrl, true), "使用全部材料");
    private static async Task<T> Data<T>(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!; }
}
