using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pim.Core.Notes;
using Pim.Module.NoteTaskSuggestions;
using Pim.Module.NoteTaskSuggestions.DTOs;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteTaskSuggestionEndpointsTests
{
    [Fact]
    public async Task TestHostDiscoversAuthorizedRoutesAndExportsTypedContract()
    {
        using var factory = AuthRegistrationTestHost.Create(
                AuthRegistrationTestHost.NewDbName("note-task-suggestion-http"))
            .WithWebHostBuilder(builder => builder.UseEnvironment("Test"));

        using var anonymous = factory.CreateClient();
        var unauthorized = await anonymous.PostAsJsonAsync(
            NoteTaskSuggestionEndpointPaths.Root + "/extractions", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "task-suggestion-http");
        using var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var request = new StartNoteTaskSuggestionExtractionRequest(
            Guid.NewGuid(),
            session.UserId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('0', 64),
            new NoteDerivedGenerationConsent(
                Guid.NewGuid(), Guid.NewGuid(), "https://text.invalid/v1", true),
            AcknowledgePossibleCharge: false);

        var rejected = await client.PostAsJsonAsync(
            NoteTaskSuggestionEndpointPaths.Root + "/extractions", request);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var error = await AuthRegistrationTestHost.ReadErrorAsync(rejected);
        Assert.Equal(40001, error.Code);
        Assert.Contains("可能产生费用", error.Message);

        var unavailable = await client.PostAsJsonAsync(
            NoteTaskSuggestionEndpointPaths.Root + "/extractions", request with
            {
                RequestId = Guid.NewGuid(),
                Consent = new NoteDerivedGenerationConsent(
                    Guid.NewGuid(), Guid.NewGuid(), "https://text.invalid/v1", true),
                AcknowledgePossibleCharge = true
            });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var unavailableError = await AuthRegistrationTestHost.ReadErrorAsync(unavailable);
        Assert.Equal(50301, unavailableError.Code);
        Assert.Contains("后台处理未配置", unavailableError.Message);

        var swagger = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        using var document = JsonDocument.Parse(await swagger.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty(
            NoteTaskSuggestionEndpointPaths.Root + "/extractions/{id}", out var extractionById));
        Assert.True(extractionById.TryGetProperty("get", out _));
        Assert.True(paths.GetProperty(NoteTaskSuggestionEndpointPaths.Root + "/extractions")
            .TryGetProperty("post", out var create));
        Assert.True(create.TryGetProperty("requestBody", out _));
        Assert.True(create.GetProperty("responses").TryGetProperty("503", out _));
        Assert.True(paths.TryGetProperty(NoteTaskSuggestionEndpointPaths.Root + "/{id}/confirm", out _));
        Assert.True(paths.TryGetProperty(NoteTaskSuggestionEndpointPaths.Root + "/{id}/dismiss", out _));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty(nameof(StartNoteTaskSuggestionExtractionRequest), out _));
        Assert.True(schemas.TryGetProperty(nameof(NoteTaskSuggestionDto), out _));
        Assert.True(schemas.TryGetProperty(nameof(NoteTaskSuggestionExtractionDto), out _));
    }
}
