using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Pim.UnitTests.Api;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteMaterialEndpointsTests : IDisposable
{
    private const string Root = "/api/v1/native-notes";
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "pim-note-http-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public NoteMaterialEndpointsTests() => _factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("note-upload"))
        .WithWebHostBuilder(builder => builder.UseEnvironment("Test")
            .UseSetting("NativeNotes:StorageRoot", Path.Combine(_storage, "materials"))
            .UseSetting("DataProtection:KeysPath", Path.Combine(_storage, "keys")));

    [Fact]
    public async Task RealAuthenticatedHttpResumesAllMixedMaterialsAndPublishesOneFixedVersion()
    {
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-owner");
        using var client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(audioSamples: 16_000 * 180);
        var response = await client.PostAsJsonAsync(Root + "/versions", fixture.Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var begun = (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
        Assert.Equal("Uploading", begun.Phase);
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            var bytes = fixture.Content[file];
            for (var part = 0; part < begun.Files[file].PartCount; part++)
            {
                var offset = part * begun.Limits.PartSizeBytes;
                var chunk = bytes.AsSpan(offset, Math.Min(begun.Limits.PartSizeBytes, bytes.Length - offset)).ToArray();
                using var body = new ByteArrayContent(chunk);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chunk)).ToLowerInvariant();
                var uploaded = await client.PutAsync($"{Root}/versions/{fixture.Request.VersionId}/files/{file}/parts/{part}?sha256={hash}", body);
                Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            }
        }
        response = await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}");
        var resumed = (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialVersionDto>>())!.Data!;
        Assert.All(resumed.Upload.Files, file => Assert.Equal(file.PartCount, file.UploadedParts.Count));
        response = await client.PostAsync($"{Root}/versions/{fixture.Request.VersionId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var complete = (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
        Assert.True(complete.IsCurrent);
        Assert.Equal("Uploaded", complete.Phase);
        var audioIndex = fixture.Content.Count - 1;
        response = await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/files/{audioIndex}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(complete.Files[audioIndex].Sha256, response.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Equal(fixture.Content[audioIndex], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnonymousAndOtherAccountsCannotAccessOrMutateMaterials()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-alice");
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-bob");
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Root + "/versions", fixture.Request)).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Root + "/versions", fixture.Request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Root}/versions/{fixture.Request.VersionId}")).StatusCode);
        using var bob = AuthRegistrationTestHost.Authed(_factory, other.AccessToken);
        var denied = await bob.GetAsync($"{Root}/versions/{fixture.Request.VersionId}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.DoesNotContain(fixture.Request.NoteId.ToString(), await denied.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"{Root}/versions/{fixture.Request.VersionId}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}")).StatusCode);
    }

    [Fact]
    public async Task ActualMiddlewareReportsConsentIntegrityAndIncompleteUploadErrors()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-errors");
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create();
        var response = await client.PostAsJsonAsync(Root + "/versions", fixture.Request with { StorageConsent = false });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(40001, (await response.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Root + "/versions", fixture.Request)).StatusCode);
        response = await client.PostAsync($"{Root}/versions/{fixture.Request.VersionId}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(40901, (await response.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);
        response = await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/files/0");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var huge = new ByteArrayContent(new byte[NoteMaterialUploadOptions.PartSizeBytes + 1]);
        huge.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        response = await client.PutAsync($"{Root}/versions/{fixture.Request.VersionId}/files/0/parts/0?sha256={new string('0', 64)}", huge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RunningTestApiPublishesTheActualAuthenticatedBinaryUploadContract()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var upload = paths.GetProperty(Root + "/versions/{versionId}/files/{fileIndex}/parts/{partIndex}").GetProperty("put");
        Assert.True(upload.TryGetProperty("security", out var security) && security.GetArrayLength() > 0);
        Assert.True(upload.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/octet-stream", out var binary));
        Assert.Equal("string", binary.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("binary", binary.GetProperty("schema").GetProperty("format").GetString());
        Assert.True(paths.TryGetProperty(Root + "/versions/{versionId}/complete", out _));
        Assert.True(paths.TryGetProperty(Root + "/versions/{versionId}/promote", out _));
        Assert.True(document.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("BeginNoteMaterialUploadRequest", out _));
        _ = _factory.Services.GetRequiredService<ISwaggerProvider>();
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }
}
