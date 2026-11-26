using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
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
            .UseSetting("ManagedStorage:Root", Path.Combine(_storage, "binary"))
            .UseSetting("DataProtection:KeysPath", Path.Combine(_storage, "keys")));

    [Fact]
    public async Task RealAuthenticatedHttpResumesAllMixedMaterialsAndPublishesOneFixedVersion()
    {
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-owner");
        using var client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-download-other");
        using var otherClient = AuthRegistrationTestHost.Authed(_factory, other.AccessToken);
        using var anonymous = _factory.CreateClient();
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
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/files/0")).StatusCode);
        response = await client.PostAsync($"{Root}/versions/{fixture.Request.VersionId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var complete = (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
        Assert.True(complete.IsCurrent);
        Assert.Equal("Uploaded", complete.Phase);
        var audioIndex = fixture.Content.Count - 1;
        response = await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/files/{audioIndex}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(complete.Files[audioIndex].Sha256, response.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store, private", response.Headers.GetValues("Cache-Control").Single());
        Assert.Contains(Path.GetFileName(complete.Files[audioIndex].Path),
            response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.Equal(fixture.Content[audioIndex], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/files/{fixture.Content.Count}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/files/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/files/0")).StatusCode);
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
    public async Task AuthenticatedOwnerCanDiscoverAndReadCurrentNoteButOtherOwnerCannot()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-discovery-owner");
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "notes-discovery-other");
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        using var otherClient = AuthRegistrationTestHost.Authed(_factory, other.AccessToken);
        using var anonymous = _factory.CreateClient();
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create();
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root)).StatusCode);
        var pendingPage = await client.GetFromJsonAsync<ApiResponse<NativeNotePageDto>>(Root);
        Assert.Empty(pendingPage!.Data!.Items);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"{Root}/{fixture.Request.NoteId}/current")).StatusCode);

        await store.BeginAsync(owner.UserId, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
            for (var part = 0; part < (fixture.Content[file].Length + NoteMaterialUploadOptions.PartSizeBytes - 1) /
                 NoteMaterialUploadOptions.PartSizeBytes; part++)
            {
                var start = part * NoteMaterialUploadOptions.PartSizeBytes;
                var bytes = fixture.Content[file].AsSpan(start,
                    Math.Min(NoteMaterialUploadOptions.PartSizeBytes, fixture.Content[file].Length - start)).ToArray();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                await store.UploadPartAsync(owner.UserId, fixture.Request.VersionId, file, part, hash, new MemoryStream(bytes));
            }
        await store.CompleteAsync(owner.UserId, fixture.Request.VersionId);

        var page = await client.GetFromJsonAsync<ApiResponse<NativeNotePageDto>>(Root + "?offset=0&limit=20");
        var item = Assert.Single(page!.Data!.Items);
        Assert.Equal(fixture.Request.NoteId, item.NoteId);
        Assert.Equal(fixture.Request.VersionId, item.CurrentVersionId);
        var current = await client.GetFromJsonAsync<ApiResponse<NoteMaterialVersionDto>>(
            $"{Root}/{fixture.Request.NoteId}/current");
        Assert.Equal(fixture.Request.InputJson, current!.Data!.InputJson);
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherClient.GetAsync($"{Root}/{fixture.Request.NoteId}/current")).StatusCode);
    }

    [Fact]
    public async Task OrdinaryOwnerMaterialReadsKeepUploadProtocolAndExposeNoDerivedDiagnostics()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "notes-material-privacy-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create();
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await store.BeginAsync(owner.UserId, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            var bytes = fixture.Content[file];
            for (var offset = 0; offset < bytes.Length; offset += NoteMaterialUploadOptions.PartSizeBytes)
            {
                var chunk = bytes.AsSpan(offset,
                    Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - offset)).ToArray();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chunk)).ToLowerInvariant();
                await store.UploadPartAsync(owner.UserId, fixture.Request.VersionId, file,
                    offset / NoteMaterialUploadOptions.PartSizeBytes, hash, new MemoryStream(chunk));
            }
        }
        await store.CompleteAsync(owner.UserId, fixture.Request.VersionId);

        var resolved = new[]
        {
            ($"{Root}/{fixture.Request.NoteId}/current", true),
            ($"{Root}/versions/{fixture.Request.VersionId}", true),
            ($"{Root}/{fixture.Request.NoteId}/versions", false)
        };
        var derivedDiagnostics = new[]
        {
            "planVersion", "promptVersion", "aiCallLogId", "modelProfileId", "modelRevision",
            "modelEndpoint", "providerHandle", "coverageJson", "errorCode", "errorMessage",
            "sourcePath", "auditVersion", "requestId", "logId", "evidenceId", "attempts"
        };

        foreach (var (path, includesMaterialJson) in resolved)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.Contains("uploadedParts", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sha256", json, StringComparison.OrdinalIgnoreCase);
            if (includesMaterialJson)
            {
                Assert.Contains("inputJson", json, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("manifestJson", json, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var marker in derivedDiagnostics)
                Assert.DoesNotContain(marker, json, StringComparison.OrdinalIgnoreCase);
        }

        using var bundle = await client.GetAsync($"{Root}/versions/{fixture.Request.VersionId}/bundle");
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        Assert.Equal("application/zip", bundle.Content.Headers.ContentType!.MediaType);
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
    public async Task OwnerCanExportCompletedMaterialInNativeAndroidBundleLayout()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "notes-bundle-owner-" + Guid.NewGuid().ToString("N"));
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "notes-bundle-other-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        using var otherClient = AuthRegistrationTestHost.Authed(_factory, other.AccessToken);
        using var anonymous = _factory.CreateClient();
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create();
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await store.BeginAsync(owner.UserId, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            var bytes = fixture.Content[file];
            for (var offset = 0; offset < bytes.Length; offset += NoteMaterialUploadOptions.PartSizeBytes)
            {
                var chunk = bytes.AsSpan(offset,
                    Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - offset)).ToArray();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(chunk)).ToLowerInvariant();
                await store.UploadPartAsync(owner.UserId, fixture.Request.VersionId, file,
                    offset / NoteMaterialUploadOptions.PartSizeBytes, hash, new MemoryStream(chunk));
            }
        }
        await store.CompleteAsync(owner.UserId, fixture.Request.VersionId);

        var path = $"{Root}/versions/{fixture.Request.VersionId}/bundle";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(path)).StatusCode);

        var incomplete = NoteMaterialUploadStoreTests.Fixture.Create();
        await store.BeginAsync(owner.UserId, incomplete.Request);
        using (var incompleteResponse = await client.GetAsync(
                   $"{Root}/versions/{incomplete.Request.VersionId}/bundle"))
        {
            Assert.Equal(HttpStatusCode.Conflict, incompleteResponse.StatusCode);
            Assert.Equal(40901, (await incompleteResponse.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);
        }

        using var response = await client.GetAsync(path);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Bundle endpoint returned {(int)response.StatusCode}: {responseBody}");
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Equal(fixture.Content.Count + 2, archive.Entries.Count);
        Assert.Equal("material-version.json", archive.Entries[0].FullName);
        Assert.Equal("materials.json", archive.Entries[1].FullName);
        Assert.Equal(fixture.Request.InputJson, await ReadEntryAsync(archive.Entries[0]));
        Assert.Equal(fixture.Request.ManifestJson, await ReadEntryAsync(archive.Entries[1]));
        using var manifest = JsonDocument.Parse(fixture.Request.ManifestJson);
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        for (var index = 0; index < fixture.Content.Count; index++)
        {
            var expectedPath = files[index].GetProperty("path").GetString();
            var entry = Assert.Single(archive.Entries.Skip(2).Where(item => item.FullName == expectedPath));
            Assert.Equal(fixture.Content[index], await ReadEntryBytesAsync(entry));
        }
    }

    [Fact]
    public async Task OpusDownloadKeepsTheOggContainerMediaTypeAndOriginalBytes()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "notes-opus-download");
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(audioSamples: 1600,
            audio: OggOpusTestData.TinyOpus(), audioFormat: "opus");
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await store.BeginAsync(owner.UserId, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            var bytes = fixture.Content[file];
            for (var offset = 0; offset < bytes.Length;
                 offset += NoteMaterialUploadOptions.PartSizeBytes)
            {
                var chunk = bytes.AsSpan(offset,
                    Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - offset)).ToArray();
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(chunk)).ToLowerInvariant();
                await store.UploadPartAsync(owner.UserId, fixture.Request.VersionId, file,
                    offset / NoteMaterialUploadOptions.PartSizeBytes, hash, new MemoryStream(chunk));
            }
        }
        await store.CompleteAsync(owner.UserId, fixture.Request.VersionId);

        using var response = await client.GetAsync(
            $"{Root}/versions/{fixture.Request.VersionId}/files/5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/ogg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(fixture.Content[5], await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(".opus", response.Content.Headers.ContentDisposition!.ToString(),
            StringComparison.Ordinal);
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
        var download = paths.GetProperty(Root + "/versions/{versionId}/files/{fileIndex}").GetProperty("get");
        Assert.True(download.TryGetProperty("security", out var downloadSecurity) && downloadSecurity.GetArrayLength() > 0);
        var downloadResponse = download.GetProperty("responses").GetProperty("200");
        var downloadContent = downloadResponse.GetProperty("content");
        foreach (var contentType in new[]
                 {
                     "audio/wav", "audio/ogg", "image/png", "image/jpeg", "image/webp",
                     "application/json",
                     "text/markdown; charset=utf-8", "application/octet-stream"
                 })
        {
            Assert.True(downloadContent.TryGetProperty(contentType, out var fileContent),
                downloadContent.GetRawText());
            Assert.Equal("string", fileContent.GetProperty("schema").GetProperty("type").GetString());
            Assert.Equal("binary", fileContent.GetProperty("schema").GetProperty("format").GetString());
        }
        var headers = downloadResponse.GetProperty("headers");
        Assert.Equal("string", headers.GetProperty("X-Content-SHA256").GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("^[0-9a-f]{64}$",
            headers.GetProperty("X-Content-SHA256").GetProperty("schema").GetProperty("pattern").GetString());
        Assert.True(headers.TryGetProperty("X-Content-Type-Options", out _));
        Assert.True(paths.TryGetProperty(Root + "/versions/{versionId}/complete", out _));
        Assert.True(paths.TryGetProperty(Root + "/versions/{versionId}/promote", out _));
        Assert.True(paths.TryGetProperty(Root, out var discovery));
        Assert.True(discovery.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty(Root + "/{noteId}/current", out _));
        Assert.True(paths.TryGetProperty(Root + "/versions/{sourceVersionId}/clone", out _));
        Assert.True(paths.TryGetProperty(Root + "/versions/{versionId}/bundle", out var bundle));
        Assert.True(bundle.TryGetProperty("get", out var bundleGet));
        var bundleResponse = bundleGet.GetProperty("responses").GetProperty("200");
        Assert.True(bundleResponse.TryGetProperty("content", out var bundleContent), bundleResponse.GetRawText());
        Assert.True(bundleContent.TryGetProperty("application/zip", out var zipContent), bundleResponse.GetRawText());
        Assert.Equal("string", zipContent.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("binary", zipContent.GetProperty("schema").GetProperty("format").GetString());
        Assert.False(paths.TryGetProperty(Root + "/{noteId}", out _));
        Assert.True(document.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("BeginNoteMaterialUploadRequest", out _));
        _ = _factory.Services.GetRequiredService<ISwaggerProvider>();
    }

    [Fact]
    public async Task RecycledNoteIsReadOnlyAcrossRestSearchAndProcessingSources()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory,
            "notes-recycle-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var store = _factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(audioSamples: 16_000);
        await UploadAllAsync(store, owner.UserId, fixture);
        var noteId = fixture.Request.NoteId;
        var versionId = fixture.Request.VersionId;

        var beforeSearch = await SearchNotesAsync(client);
        Assert.Equal(1, beforeSearch.TotalCount);
        Assert.Equal(noteId.ToString("D"), Assert.Single(beforeSearch.Items).Id);
        Assert.Equal(1, await SourceCountAsync(client,
            "/api/v1/native-notes/asr-tasks/sources"));
        Assert.Equal(1, await SourceCountAsync(client,
            "/api/v1/native-notes/derived-tasks/sources"));

        var snapshot = await store.GetMcpSnapshotAsync(owner.UserId, noteId, 1);
        await store.RecycleAsync(owner.UserId, noteId, snapshot.Revision);

        var page = await client.GetFromJsonAsync<ApiResponse<NativeNotePageDto>>(Root);
        Assert.Equal(0, page!.Data!.Total);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync($"{Root}/{noteId}/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync($"{Root}/versions/{versionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync($"{Root}/versions/{versionId}/bundle")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync($"{Root}/{noteId}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync($"{Root}/versions/{versionId}/files/0")).StatusCode);

        var firstFile = fixture.Content[0];
        var firstPart = firstFile.AsSpan(0,
            Math.Min(NoteMaterialUploadOptions.PartSizeBytes, firstFile.Length)).ToArray();
        using (var body = new ByteArrayContent(firstPart))
        {
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(firstPart)).ToLowerInvariant();
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PutAsync(
                    $"{Root}/versions/{versionId}/files/0/parts/0?sha256={hash}", body)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"{Root}/versions/{versionId}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"{Root}/versions/{versionId}/promote",
                new PromoteNoteMaterialVersionRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"{Root}/versions",
                NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId).Request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"{Root}/versions/{versionId}/clone",
                new CloneNoteMaterialVersionRequest(null,
                    NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId).Request))).StatusCode);

        var whileRecycled = await SearchNotesAsync(client);
        Assert.Equal(0, whileRecycled.TotalCount);
        Assert.Empty(whileRecycled.Items);
        Assert.Equal(0, await SourceCountAsync(client,
            "/api/v1/native-notes/asr-tasks/sources"));
        Assert.Equal(0, await SourceCountAsync(client,
            "/api/v1/native-notes/derived-tasks/sources"));

        await store.RestoreAsync(owner.UserId, noteId, null);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"{Root}/{noteId}/current")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"{Root}/versions/{versionId}/files/0")).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<ApiResponse<NativeNotePageDto>>(Root))!
            .Data!.Total);
        Assert.Equal(1, (await SearchNotesAsync(client)).TotalCount);
        Assert.Equal(1, await SourceCountAsync(client,
            "/api/v1/native-notes/asr-tasks/sources"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task UploadAllAsync(NoteMaterialUploadStore store, Guid owner,
        NoteMaterialUploadStoreTests.Fixture fixture)
    {
        await store.BeginAsync(owner, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            var bytes = fixture.Content[file];
            for (var offset = 0; offset < bytes.Length;
                 offset += NoteMaterialUploadOptions.PartSizeBytes)
            {
                var chunk = bytes.AsSpan(offset,
                    Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - offset))
                    .ToArray();
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(chunk)).ToLowerInvariant();
                await store.UploadPartAsync(owner, fixture.Request.VersionId, file,
                    offset / NoteMaterialUploadOptions.PartSizeBytes, hash,
                    new MemoryStream(chunk, writable: false));
            }
        }
        await store.CompleteAsync(owner, fixture.Request.VersionId);
    }

    private static async Task<GlobalSearchResult> SearchNotesAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<ApiResponse<GlobalSearchResult>>(
            "/api/v1/search/?q=%E7%A0%94%E7%A9%B6%E6%96%B9%E6%B3%95&scope=note");
        Assert.Equal(0, response!.Code);
        return response.Data!;
    }

    private static async Task<int> SourceCountAsync(HttpClient client, string path)
    {
        var response = await client.GetFromJsonAsync<ApiResponse<JsonElement>>(path);
        Assert.Equal(0, response!.Code);
        return response.Data.GetProperty("total").GetInt32();
    }
}
