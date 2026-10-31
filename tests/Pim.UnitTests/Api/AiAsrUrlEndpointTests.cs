using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.UnitTests.Ai;
using Xunit;

namespace Pim.UnitTests.Api;

public sealed class AiAsrUrlEndpointTests(AiAsrEndpointFixture host)
    : IClassFixture<AiAsrEndpointFixture>
{
    [Fact]
    public async Task Url_call_route_requires_admin_and_rejects_legacy_asr_before_any_fetch()
    {
        using var anonymous = host.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(
            "/api/v1/ai/asr-url-calls", new { })).StatusCode);

        var ordinary = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "url-route-user-" + Guid.NewGuid().ToString("N"));
        using var ordinaryClient = AuthRegistrationTestHost.Authed(host.Factory, ordinary.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinaryClient.PostAsJsonAsync(
            "/api/v1/ai/asr-url-calls", new { })).StatusCode);

        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(host.Factory,
            "url-route-admin-" + Guid.NewGuid().ToString("N"), "admin");
        using var client = AuthRegistrationTestHost.Authed(host.Factory, owner.AccessToken);
        var presets = await Data<AiAsrPresetDto[]>(
            await client.GetAsync("/api/v1/ai/asr-presets"));
        var legacy = Assert.Single(presets.Where(x => x.Id == "mimo-asr"));
        var model = await Data<AiModelDto>(await client.PostAsJsonAsync("/api/v1/ai/models",
            new SaveAiModelRequest("url-legacy-" + Guid.NewGuid().ToString("N"),
                "https://api.synthetic.invalid/v1/", "mimo-v2.5-asr", true,
                [AiModelCapability.Transcription], 30, 256, "synthetic-url-route-key",
                AsrPresetId: legacy.Id)));
        var request = new AiAsrUrlCallRequest(Guid.NewGuid(), owner.UserId,
            new AiAsrUrlAudioRequest("take.wav", "wav",
                "https://audio.example.test/private/take.wav?sig=EE", null, true, true),
            new AiAsrDestinationConsent(model.Id, model.Revision,
                model.BaseUrl + legacy.OperationPath, legacy.Id, legacy.Version, true),
            "zh", model.Id);

        var response = await client.PostAsJsonAsync("/api/v1/ai/asr-url-calls", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(40001, error.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("MiMo", error.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("sig=EE", error.RootElement.GetRawText(), StringComparison.Ordinal);
        var missing = await client.GetAsync($"/api/v1/ai/asr-url-calls/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }
}
