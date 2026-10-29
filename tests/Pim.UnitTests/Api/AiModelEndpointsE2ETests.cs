using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;
using Xunit;

namespace Pim.UnitTests.Api;

public sealed class AiModelEndpointFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-ai-model-http", Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<Program> Factory { get; }
    public AiModelEndpointFixture() => Factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("ai-model-http"))
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test").UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"))
                .UseSetting("ManagedStorage:Root", Path.Combine(_root, "storage"));
            builder.ConfigureServices(services => services.AddHttpClient(AiProfileChatClientFactory.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new JsonProvider()));
        });
    public void Dispose() { Factory.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class JsonProvider : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = input.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = "chatcmpl-http-fixture", @object = "chat.completion", created = 1780000000, model = "http-model",
                choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "回执：" + prompt } } },
                usage = new { prompt_tokens = 2, completion_tokens = 3, total_tokens = 5 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}

public sealed class AiModelEndpointsE2ETests(AiModelEndpointFixture host) : IClassFixture<AiModelEndpointFixture>
{
    [Fact]
    public async Task ActualLoopbackProviderGetsExactPrefixAndSdkDoesNotFollowRedirectsOrRetry429()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = builder.Build(); var trap = 0; var busy = 0;
        object Reply() => new { id = "chatcmpl-loopback", @object = "chat.completion", created = 1780000000, model = "loopback-model",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "本机HTTP完整回执" } } } };
        provider.MapPost("/custom-prefix/chat/completions", (HttpRequest request) =>
        { Assert.Equal("Bearer loopback-fixture-key", request.Headers.Authorization.ToString()); return Results.Json(Reply()); });
        provider.MapPost("/redirect/chat/completions", () => Results.Redirect("/trap", preserveMethod: true));
        provider.MapMethods("/trap", ["GET", "POST"], () => { Interlocked.Increment(ref trap); return Results.Json(Reply()); });
        provider.MapPost("/busy/chat/completions", () => { Interlocked.Increment(ref busy); return Results.Json(new { error = new { message = "fixture busy" } }, statusCode: 429); });
        await provider.StartAsync(); var address = provider.Urls.Single();
        using var factory = host.Factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddHttpClient(AiProfileChatClientFactory.ClientName).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })));
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "ai-wire-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(factory, owner.AccessToken);
        var settings = await Data<AiModelSettingsDto>(await client.GetAsync("/api/v1/ai/model-settings"));
        await Data<AiModelSettingsDto>(await client.PutAsJsonAsync("/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, true, null, null, null)));
        async Task<AiTextCallResultDto> Call(string prefix)
        {
            var model = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
                new SaveAiModelRequest("本机HTTP-" + prefix + Guid.NewGuid(), address + prefix, "loopback-model", true,
                    [AiModelCapability.Text], 5, 123, "loopback-fixture-key")));
            return await Data<AiTextCallResultDto>(await client.PostAsJsonAsync("/api/v1/ai/text-calls",
                new AiTextCallRequest(Guid.NewGuid(), owner.UserId, "合成输入", ModelId: model.Id)));
        }
        var success = await Call("/custom-prefix");
        Assert.Equal(AiRequestStatus.Succeeded, success.Status); Assert.Equal("本机HTTP完整回执", success.ResponseText);
        Assert.NotEqual(AiRequestStatus.Succeeded, (await Call("/redirect")).Status); Assert.Equal(0, trap);
        Assert.Equal(AiRequestStatus.Failed, (await Call("/busy")).Status); Assert.Equal(1, busy);
    }

    [Fact]
    public async Task ModelsSettingsCallsAndHistoryAllRequireAdministrator()
    {
        using var anon = host.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/ai/models")).StatusCode);
        var user = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory, "ai-user-" + Guid.NewGuid().ToString("N"));
        using var ordinary = AuthRegistrationTestHost.Authed(host.Factory, user.AccessToken);
        foreach (var path in new[] { "/api/v1/ai/models", "/api/v1/ai/model-settings", "/api/v1/ai/requests", "/api/v1/ai/usage/summary" })
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.PostAsJsonAsync("/api/v1/ai/text-calls",
            new AiTextCallRequest(Guid.NewGuid(), user.UserId, "private"))).StatusCode);
    }

    [Fact]
    public async Task FullAdminHttpFlowUsesRealSdkAndRetainsHistoryAfterDeletingTheModel()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory, "ai-admin-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var model = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("HTTP模型" + Guid.NewGuid(), "http://provider.fixture/v1", "http-model", true,
                [AiModelCapability.Text, AiModelCapability.Vision], ApiKey: "fake-api-credential")));
        Assert.True(model.HasApiKey);
        Assert.Contains(model.Id, (await Data<List<AiModelDto>>(await client.GetAsync("/api/v1/ai/models"))).Select(x => x.Id));
        var settings = await Data<AiModelSettingsDto>(await client.GetAsync("/api/v1/ai/model-settings"));
        settings = await Data<AiModelSettingsDto>(await client.PutAsJsonAsync("/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, true, model.Id, model.Id, null)));
        var request = new AiTextCallRequest(Guid.NewGuid(), owner.UserId, "接口正文\npassword=ordinary-text", "完整系统提示");
        var result = await Data<AiTextCallResultDto>(await client.PostAsJsonAsync("/api/v1/ai/text-calls", request));
        Assert.Equal(AiRequestStatus.Succeeded, result.Status); Assert.Equal("回执：" + request.Prompt, result.ResponseText);
        var replay = await Data<AiTextCallResultDto>(await client.PostAsJsonAsync("/api/v1/ai/text-calls", request));
        Assert.True(replay.Replayed); Assert.Equal(result.LogId, replay.LogId);
        var detail = await Data<AiRequestLogDetailDto>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}"));
        Assert.Equal(owner.UserId, detail.UserId); Assert.Equal(model.Id, detail.ModelProfileId);
        Assert.Equal(result.ResponseText, detail.ResponseText); Assert.DoesNotContain("fake-api-credential", JsonSerializer.Serialize(detail));
        using var messages = JsonDocument.Parse(detail.RequestMessagesJson);
        Assert.Equal(request.Prompt, messages.RootElement[1].GetProperty("content").GetString());
        var usage = await Data<AiUsageSummaryDto>(await client.GetAsync("/api/v1/ai/usage/summary"));
        Assert.True(usage.SuccessCount >= 1); Assert.Equal(0, usage.KnownCostRequestCount);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v1/ai/models/{model.Id}?revision={model.Revision}")).StatusCode);
        await Data<AiModelSettingsDto>(await client.PutAsJsonAsync("/api/v1/ai/model-settings",
            new SaveAiModelSettingsRequest(settings.Revision, false, null, null, null)));
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/v1/ai/models/{model.Id}?revision={model.Revision}")).StatusCode);
        Assert.Equal(result.ResponseText, (await Data<AiRequestLogDetailDto>(await client.GetAsync($"/api/v1/ai/requests/{result.LogId}"))).ResponseText);
        Assert.True((await Data<AiTextCallResultDto>(await client.GetAsync($"/api/v1/ai/text-calls/{request.ClientRequestId}"))).Replayed);
    }

    [Fact]
    public async Task SessionMismatchAndStaleConfigurationFailWithoutSilentlyOverwriting()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory, "ai-revision-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var model = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("版本模型" + Guid.NewGuid(), "http://provider.fixture/v1", "http-model", true,
                [AiModelCapability.Text], ApiKey: "fake-revision-key")));
        var changed = new SaveAiModelRequest(model.Name, model.BaseUrl, "new-model", true, model.Capabilities,
            ExpectedRevision: model.Revision);
        var renamed = await Data<AiModelDto>(await client.PutAsJsonAsync($"/api/v1/ai/models/{model.Id}", changed));
        Assert.Equal(model.BaseUrl, renamed.BaseUrl); Assert.Equal("new-model", renamed.Model); Assert.True(renamed.HasApiKey);

        var changedEndpoint = new SaveAiModelRequest(renamed.Name, "http://other-provider.fixture/v1", renamed.Model,
            true, renamed.Capabilities, ExpectedRevision: renamed.Revision);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(
            $"/api/v1/ai/models/{model.Id}", changedEndpoint)).StatusCode);
        var unchanged = Assert.Single(await Data<List<AiModelDto>>(await client.GetAsync("/api/v1/ai/models")),
            x => x.Id == model.Id);
        Assert.Equal(renamed.BaseUrl, unchanged.BaseUrl); Assert.Equal(renamed.Revision, unchanged.Revision);

        var moved = await Data<AiModelDto>(await client.PutAsJsonAsync($"/api/v1/ai/models/{model.Id}",
            changedEndpoint with { ApiKey = "new-endpoint-key" }));
        Assert.Equal("http://other-provider.fixture/v1/", moved.BaseUrl); Assert.True(moved.HasApiKey);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/v1/ai/models/{model.Id}", changed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/ai/text-calls",
            new AiTextCallRequest(Guid.NewGuid(), Guid.NewGuid(), "会话已变"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/ai/text-calls/{Guid.NewGuid()}")).StatusCode);
    }

    private static async Task<T> Data<T>(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!; }
}
