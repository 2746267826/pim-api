using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class AndroidMixedMaterialInteropTests : IDisposable
{
    private const string Root = "/api/v1/native-notes";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), "pim-android-mixed-material", Guid.NewGuid().ToString("N"));
    private readonly string _httpRoot = Path.Combine(Path.GetTempPath(), "pim-android-mixed-http", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public AndroidMixedMaterialInteropTests()
    {
        _factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("android-mixed-material"))
            .WithWebHostBuilder(builder => builder.UseEnvironment("Test")
                .UseSetting("NativeNotes:StorageRoot", Path.Combine(_httpRoot, "materials"))
                .UseSetting("ManagedStorage:Root", Path.Combine(_httpRoot, "binary"))
                .UseSetting("DataProtection:KeysPath", Path.Combine(_httpRoot, "keys")));
    }

    [Fact]
    public async Task StorePreservesTheAndroidRawBundleAcrossResumeCompleteAndConcurrentPromotion()
    {
        var fixture = AndroidFixture.Load();
        var owner = Guid.NewGuid();
        var store = NewStore();

        var begun = await store.BeginAsync(owner, fixture.Request);
        await UploadPartAsync(store, owner, fixture, begun, 0, 0);
        var resumed = await NewStore().BeginAsync(owner, fixture.Request);
        Assert.Equal(new[] { 0 }, resumed.Files[0].UploadedParts);
        await UploadPartAsync(store, owner, fixture, resumed, 0, 0);
        await UploadAllAsync(store, owner, fixture, resumed);

        var complete = await store.CompleteAsync(owner, fixture.Request.VersionId);
        Assert.True(complete.IsCurrent);
        var saved = await store.GetAsync(owner, fixture.Request.VersionId);
        Assert.Equal(fixture.Request.InputJson, saved.InputJson);
        Assert.Equal(fixture.Request.ManifestJson, saved.ManifestJson);
        await AssertFilesAsync(store, owner, fixture, saved.Upload);

        var first = AndroidFixture.Edit(fixture, "Android branch A", 8);
        var second = AndroidFixture.Edit(fixture, "Android branch B", 9);
        await store.BeginAsync(owner, first.Request);
        await store.BeginAsync(owner, second.Request);
        await UploadAllAsync(store, owner, first);
        await UploadAllAsync(store, owner, second);

        var published = await Task.WhenAll(
            store.CompleteAsync(owner, first.Request.VersionId),
            store.CompleteAsync(owner, second.Request.VersionId));
        var head = Assert.Single(published.Where(item => item.IsCurrent));
        var conflict = Assert.Single(published.Where(item => item.HasConflict));
        Assert.Equal("Conflict", conflict.Phase);
        await Assert.ThrowsAsync<DomainException>(() => store.PromoteAsync(owner, conflict.VersionId, null));

        var promoted = await store.PromoteAsync(owner, conflict.VersionId, head.VersionId);
        Assert.True(promoted.IsCurrent);
        Assert.False(promoted.HasConflict);
        foreach (var branch in new[] { first, second })
        {
            var version = await store.GetAsync(owner, branch.Request.VersionId);
            Assert.Equal(branch.Request.InputJson, version.InputJson);
            Assert.Equal(branch.Request.ManifestJson, version.ManifestJson);
            await AssertFilesAsync(store, owner, branch, version.Upload);
        }
    }

    [Fact]
    public async Task HttpPublishesAndReadsTheRealAndroidBundleThenRequiresExpectedHeadForParallelEdits()
    {
        var fixture = AndroidFixture.Load();
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(
            _factory, "android-mixed-owner-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);

        var response = await client.PostAsJsonAsync(Root + "/versions", fixture.Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var begun = (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
        await UploadAllHttpAsync(client, fixture, begun);
        response = await client.PostAsync($"{Root}/versions/{fixture.Request.VersionId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var listed = (await client.GetFromJsonAsync<ApiResponse<NativeNotePageDto>>(
            Root + "?offset=0&limit=20"))!.Data!;
        var item = Assert.Single(listed.Items);
        Assert.Equal(fixture.Request.NoteId, item.NoteId);
        Assert.Equal(fixture.Request.VersionId, item.CurrentVersionId);
        Assert.False(item.HasConflict);

        var current = (await client.GetFromJsonAsync<ApiResponse<NoteMaterialVersionDto>>(
            $"{Root}/{fixture.Request.NoteId}/current"))!.Data!;
        Assert.Equal(fixture.Request.InputJson, current.InputJson);
        Assert.Equal(fixture.Request.ManifestJson, current.ManifestJson);
        var version = (await client.GetFromJsonAsync<ApiResponse<NoteMaterialVersionDto>>(
            $"{Root}/versions/{fixture.Request.VersionId}"))!.Data!;
        Assert.Equal(fixture.Request.InputJson, version.InputJson);
        await AssertHttpFilesAsync(client, fixture, version.Upload);

        var first = AndroidFixture.Edit(fixture, "HTTP branch A", 8);
        var second = AndroidFixture.Edit(fixture, "HTTP branch B", 9);
        var firstBegun = await BeginHttpAsync(client, first.Request);
        var secondBegun = await BeginHttpAsync(client, second.Request);
        await UploadAllHttpAsync(client, first, firstBegun);
        await UploadAllHttpAsync(client, second, secondBegun);
        var published = await Task.WhenAll(
            CompleteHttpAsync(client, first.Request.VersionId),
            CompleteHttpAsync(client, second.Request.VersionId));
        var head = Assert.Single(published.Where(item => item.IsCurrent));
        var conflict = Assert.Single(published.Where(item => item.HasConflict));

        response = await client.PostAsJsonAsync(
            $"{Root}/versions/{conflict.VersionId}/promote", new PromoteNoteMaterialVersionRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        response = await client.PostAsJsonAsync(
            $"{Root}/versions/{conflict.VersionId}/promote",
            new PromoteNoteMaterialVersionRequest(head.VersionId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var promoted = (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
        Assert.True(promoted.IsCurrent);

        var versions = (await client.GetFromJsonAsync<ApiResponse<IReadOnlyList<NoteMaterialUploadDto>>>(
            $"{Root}/{fixture.Request.NoteId}/versions"))!.Data!;
        Assert.Equal(3, versions.Count);
        Assert.Single(versions.Where(item => item.IsCurrent));
        Assert.All(versions, item => Assert.Contains(item.VersionId, new[]
        {
            fixture.Request.VersionId, first.Request.VersionId, second.Request.VersionId
        }));
    }

    [Fact]
    public async Task StoreAndHttpContinueFromTheRealWebBundleWithoutReuploadingPreservedFiles()
    {
        var fixture = ContinuationFixture.Load();

        var owner = Guid.NewGuid();
        var store = NewStore();
        var original = AndroidFixture.Load();
        var originalUpload = await store.BeginAsync(owner, original.Request);
        await UploadAllAsync(store, owner, original);
        await store.CompleteAsync(owner, original.Request.VersionId);

        var cloned = await store.CloneAsync(owner, original.Request.VersionId,
            new CloneNoteMaterialVersionRequest(original.Request.VersionId, fixture.Request));
        Assert.Equal(original.Request.VersionId, cloned.CurrentVersionId);
        Assert.Equal(fixture.Request.BaseVersionId, cloned.BaseVersionId);
        AssertCloneParts(originalUpload, cloned);
        await UploadMissingPartsAsync(store, owner, fixture, cloned);
        var completed = await store.CompleteAsync(owner, fixture.Request.VersionId);
        Assert.True(completed.IsCurrent);
        await AssertContinuationAsync(store, owner, original, fixture);

        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(
            _factory, "web-continuation-owner-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(_factory, session.AccessToken);
        await PublishHttpAsync(client, original);
        var originalHttp = (await client.GetFromJsonAsync<ApiResponse<NoteMaterialVersionDto>>(
            $"{Root}/versions/{original.Request.VersionId}"))!.Data!.Upload;
        var cloneResponse = await client.PostAsJsonAsync(
            $"{Root}/versions/{original.Request.VersionId}/clone",
            new CloneNoteMaterialVersionRequest(original.Request.VersionId, fixture.Request));
        Assert.Equal(HttpStatusCode.OK, cloneResponse.StatusCode);
        var httpClone = (await cloneResponse.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
        Assert.Equal(original.Request.VersionId, httpClone.CurrentVersionId);
        AssertCloneParts(originalHttp, httpClone);
        await UploadMissingPartsHttpAsync(client, fixture, httpClone);
        await CompleteHttpAsync(client, fixture.Request.VersionId);
        await AssertContinuationHttpAsync(client, original, fixture);
    }

    private NoteMaterialUploadStore NewStore() => new(Options.Create(new NoteMaterialUploadOptions
    {
        StorageRoot = _storeRoot,
        MaxVersionBytes = 32 * 1024 * 1024,
        MaxUserReservedBytes = 64 * 1024 * 1024
    }));

    private static async Task UploadAllAsync(NoteMaterialUploadStore store, Guid owner, AndroidFixture fixture,
        NoteMaterialUploadDto? begun = null)
    {
        begun ??= (await store.GetAsync(owner, fixture.Request.VersionId)).Upload;
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            for (var part = 0; part < begun.Files[file].PartCount; part++)
            {
                if (begun.Files[file].UploadedParts.Contains(part)) continue;
                await UploadPartAsync(store, owner, fixture, begun, file, part);
            }
        }
    }

    private static async Task UploadPartAsync(NoteMaterialUploadStore store, Guid owner, AndroidFixture fixture,
        NoteMaterialUploadDto begun, int file, int part)
    {
        var bytes = Part(fixture.Content[file], part, begun.Limits.PartSizeBytes);
        await store.UploadPartAsync(owner, fixture.Request.VersionId, file, part, Hash(bytes), new MemoryStream(bytes));
    }

    private static async Task UploadPartAsync(NoteMaterialUploadStore store, Guid owner, ContinuationFixture fixture,
        NoteMaterialUploadDto begun, int file, int part)
    {
        var bytes = Part(fixture.Content[file], part, begun.Limits.PartSizeBytes);
        await store.UploadPartAsync(owner, fixture.Request.VersionId, file, part, Hash(bytes), new MemoryStream(bytes));
    }

    private static async Task UploadMissingPartsAsync(NoteMaterialUploadStore store, Guid owner,
        ContinuationFixture fixture, NoteMaterialUploadDto begun)
    {
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            for (var part = 0; part < begun.Files[file].PartCount; part++)
            {
                if (begun.Files[file].UploadedParts.Contains(part)) continue;
                await UploadPartAsync(store, owner, fixture, begun, file, part);
            }
        }
    }

    private static async Task AssertFilesAsync(NoteMaterialUploadStore store, Guid owner, AndroidFixture fixture,
        NoteMaterialUploadDto upload)
    {
        for (var index = 0; index < fixture.Content.Count; index++)
        {
            using var output = new MemoryStream();
            await store.ReadFileAsync(owner, upload.VersionId, index, output);
            Assert.Equal(fixture.Content[index], output.ToArray());
            Assert.Equal(Hash(fixture.Content[index]), upload.Files[index].Sha256);
            Assert.Equal(index, upload.Files[index].Index);
        }
    }

    private static async Task AssertFilesAsync(NoteMaterialUploadStore store, Guid owner, ContinuationFixture fixture,
        NoteMaterialUploadDto upload)
    {
        for (var index = 0; index < fixture.Content.Count; index++)
        {
            using var output = new MemoryStream();
            await store.ReadFileAsync(owner, upload.VersionId, index, output);
            Assert.Equal(fixture.Content[index], output.ToArray());
            Assert.Equal(Hash(fixture.Content[index]), upload.Files[index].Sha256);
        }
    }

    private static async Task<NoteMaterialUploadDto> BeginHttpAsync(HttpClient client,
        BeginNoteMaterialUploadRequest request)
    {
        var response = await client.PostAsJsonAsync(Root + "/versions", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
    }

    private static async Task UploadAllHttpAsync(HttpClient client, AndroidFixture fixture,
        NoteMaterialUploadDto? begun = null)
    {
        begun ??= await BeginHttpAsync(client, fixture.Request);
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            for (var part = 0; part < begun.Files[file].PartCount; part++)
            {
                if (begun.Files[file].UploadedParts.Contains(part)) continue;
                var bytes = Part(fixture.Content[file], part, begun.Limits.PartSizeBytes);
                using var body = new ByteArrayContent(bytes);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var response = await client.PutAsync(
                    $"{Root}/versions/{fixture.Request.VersionId}/files/{file}/parts/{part}?sha256={Hash(bytes)}", body);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    private static async Task<NoteMaterialUploadDto> CompleteHttpAsync(HttpClient client, Guid versionId)
    {
        var response = await client.PostAsync($"{Root}/versions/{versionId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<NoteMaterialUploadDto>>())!.Data!;
    }

    private static async Task PublishHttpAsync(HttpClient client, AndroidFixture fixture)
    {
        var begun = await BeginHttpAsync(client, fixture.Request);
        await UploadAllHttpAsync(client, fixture, begun);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"{Root}/versions/{fixture.Request.VersionId}/complete", null)).StatusCode);
    }

    private static async Task UploadPartHttpAsync(HttpClient client, ContinuationFixture fixture,
        NoteMaterialUploadDto begun, int file, int part)
    {
        var bytes = Part(fixture.Content[file], part, begun.Limits.PartSizeBytes);
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await client.PutAsync(
            $"{Root}/versions/{fixture.Request.VersionId}/files/{file}/parts/{part}?sha256={Hash(bytes)}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task UploadMissingPartsHttpAsync(HttpClient client, ContinuationFixture fixture,
        NoteMaterialUploadDto begun)
    {
        for (var file = 0; file < fixture.Content.Count; file++)
        {
            for (var part = 0; part < begun.Files[file].PartCount; part++)
            {
                if (begun.Files[file].UploadedParts.Contains(part)) continue;
                await UploadPartHttpAsync(client, fixture, begun, file, part);
            }
        }
    }

    private static void AssertCloneParts(NoteMaterialUploadDto original, NoteMaterialUploadDto cloned)
    {
        foreach (var file in cloned.Files)
        {
            var preserved = original.Files.Any(source =>
                source.Path == file.Path && source.Kind == file.Kind &&
                source.SizeBytes == file.SizeBytes && source.Sha256 == file.Sha256);
            if (preserved)
                Assert.Equal(file.PartCount, file.UploadedParts.Count);
            else
                Assert.Empty(file.UploadedParts);
        }
    }

    private static async Task AssertContinuationAsync(NoteMaterialUploadStore store, Guid owner,
        AndroidFixture original, ContinuationFixture fixture)
    {
        var current = await store.GetCurrentAsync(owner, fixture.Request.NoteId);
        Assert.Equal(fixture.Request.InputJson, current.InputJson);
        Assert.Equal(fixture.Request.ManifestJson, current.ManifestJson);
        await AssertFilesAsync(store, owner, fixture, current.Upload);
        var old = await store.GetAsync(owner, original.Request.VersionId);
        await AssertFilesAsync(store, owner, original, old.Upload);
    }

    private static async Task AssertContinuationHttpAsync(HttpClient client, AndroidFixture original,
        ContinuationFixture fixture)
    {
        var current = (await client.GetFromJsonAsync<ApiResponse<NoteMaterialVersionDto>>(
            $"{Root}/{fixture.Request.NoteId}/current"))!.Data!;
        Assert.Equal(fixture.Request.InputJson, current.InputJson);
        Assert.Equal(fixture.Request.ManifestJson, current.ManifestJson);
        await AssertHttpFilesAsync(client, fixture, current.Upload);
        var old = (await client.GetFromJsonAsync<ApiResponse<NoteMaterialVersionDto>>(
            $"{Root}/versions/{original.Request.VersionId}"))!.Data!;
        await AssertHttpFilesAsync(client, original, old.Upload);
    }

    private static async Task AssertHttpFilesAsync(HttpClient client, AndroidFixture fixture,
        NoteMaterialUploadDto upload)
    {
        for (var index = 0; index < fixture.Content.Count; index++)
        {
            using var response = await client.GetAsync($"{Root}/versions/{upload.VersionId}/files/{index}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Hash(fixture.Content[index]),
                response.Headers.GetValues("X-Content-SHA256").Single());
            Assert.Equal(fixture.Content[index], await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(upload.Files[index].Index, index);
            Assert.Equal(upload.Files[index].Sha256, response.Headers.GetValues("X-Content-SHA256").Single());
        }
    }

    private static async Task AssertHttpFilesAsync(HttpClient client, ContinuationFixture fixture,
        NoteMaterialUploadDto upload)
    {
        for (var index = 0; index < fixture.Content.Count; index++)
        {
            using var response = await client.GetAsync($"{Root}/versions/{upload.VersionId}/files/{index}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Hash(fixture.Content[index]),
                response.Headers.GetValues("X-Content-SHA256").Single());
            Assert.Equal(fixture.Content[index], await response.Content.ReadAsByteArrayAsync());
        }
    }

    private static byte[] Part(byte[] bytes, int part, int partSize)
    {
        var start = part * partSize;
        return bytes.AsSpan(start, Math.Min(partSize, bytes.Length - start)).ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        _factory.Dispose();
        DeleteDirectory(_storeRoot);
        DeleteDirectory(_httpRoot);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed record AndroidFixture(
        BeginNoteMaterialUploadRequest Request,
        IReadOnlyList<byte[]> Content,
        IReadOnlyList<string> Paths)
    {
        private const string RelativePath = "Fixtures/notes/mixed-material-v1";

        public static AndroidFixture Load()
        {
            var root = Path.Combine(AppContext.BaseDirectory, RelativePath);
            var inputBytes = File.ReadAllBytes(Path.Combine(root, "input.json"));
            var manifestBytes = File.ReadAllBytes(Path.Combine(root, "manifest.json"));
            var input = Encoding.UTF8.GetString(inputBytes);
            var manifest = Encoding.UTF8.GetString(manifestBytes);
            using var inputDocument = JsonDocument.Parse(input);
            using var manifestDocument = JsonDocument.Parse(manifest);
            var versionId = inputDocument.RootElement.GetProperty("id").GetGuid();
            var noteId = inputDocument.RootElement.GetProperty("note").GetProperty("id").GetGuid();
            var paths = manifestDocument.RootElement.GetProperty("files").EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()!)
                .ToArray();
            var content = paths.Select(path => File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
            Assert.Equal(Hash(inputBytes), manifestDocument.RootElement.GetProperty("inputSha256").GetString());
            Assert.Equal(inputDocument.RootElement.GetProperty("id").GetGuid(), manifestDocument.RootElement.GetProperty("versionId").GetGuid());
            Assert.Equal(noteId, manifestDocument.RootElement.GetProperty("noteId").GetGuid());
            for (var index = 0; index < content.Length; index++)
            {
                var file = manifestDocument.RootElement.GetProperty("files")[index];
                Assert.Equal(Hash(content[index]), file.GetProperty("sha256").GetString());
                Assert.Equal(content[index].LongLength, file.GetProperty("sizeBytes").GetInt64());
            }
            return new(new(versionId, noteId, null, input, manifest, true), content, paths);
        }

        public static AndroidFixture Edit(AndroidFixture source, string title, int revision)
        {
            var versionId = Guid.NewGuid();
            using var inputDocument = JsonDocument.Parse(source.Request.InputJson);
            var oldNote = inputDocument.RootElement.GetProperty("note").GetRawText();
            var newNote = ReplaceOnce(oldNote, "\"title\":\"Canonical mixed materials\"", $"\"title\":\"{title}\"");
            newNote = ReplaceOnce(newNote, "\"revision\":7", $"\"revision\":{revision}");
            var input = ReplaceOnce(source.Request.InputJson, oldNote, newNote);
            input = ReplaceOnce(input, $"\"id\":\"{source.Request.VersionId:D}\"", $"\"id\":\"{versionId:D}\"");

            using var manifestDocument = JsonDocument.Parse(source.Request.ManifestJson);
            var oldDocumentHash = manifestDocument.RootElement.GetProperty("files")[0].GetProperty("sha256").GetString()!;
            var newNoteBytes = Encoding.UTF8.GetBytes(newNote);
            var manifest = ReplaceOnce(source.Request.ManifestJson,
                $"\"versionId\":\"{source.Request.VersionId:D}\"", $"\"versionId\":\"{versionId:D}\"");
            manifest = ReplaceOnce(manifest, "\"revision\":7", $"\"revision\":{revision}");
            manifest = ReplaceOnce(manifest, $"\"inputSha256\":\"{Hash(Encoding.UTF8.GetBytes(source.Request.InputJson))}\"",
                $"\"inputSha256\":\"{Hash(Encoding.UTF8.GetBytes(input))}\"");
            manifest = ReplaceOnce(manifest, $"\"sha256\":\"{oldDocumentHash}\"",
                $"\"sha256\":\"{Hash(newNoteBytes)}\"");
            manifest = ReplaceOnce(manifest, $"\"sizeBytes\":{source.Content[0].LongLength}",
                $"\"sizeBytes\":{newNoteBytes.LongLength}");

            var content = source.Content.Select(bytes => bytes.ToArray()).ToArray();
            content[0] = newNoteBytes;
            return new(new(versionId, source.Request.NoteId, source.Request.VersionId, input, manifest, true), content,
                source.Paths);
        }

        private static string ReplaceOnce(string source, string oldValue, string newValue)
        {
            var index = source.IndexOf(oldValue, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Raw JSON token not found: {oldValue}");
            Assert.Equal(index, source.LastIndexOf(oldValue, StringComparison.Ordinal));
            return string.Concat(source.AsSpan(0, index), newValue, source.AsSpan(index + oldValue.Length));
        }
    }

    private sealed record ContinuationFixture(
        BeginNoteMaterialUploadRequest Request,
        IReadOnlyList<byte[]> Content,
        IReadOnlyList<string> Paths)
    {
        public static ContinuationFixture Load()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "Fixtures/notes/web-continuation-v1");
            var requestJson = File.ReadAllText(Path.Combine(root, "request.json"));
            using var document = JsonDocument.Parse(requestJson);
            var material = document.RootElement.GetProperty("material");
            var input = File.ReadAllText(Path.Combine(root, "input.json"));
            var manifest = File.ReadAllText(Path.Combine(root, "manifest.json"));
            var paths = JsonDocument.Parse(manifest).RootElement.GetProperty("files").EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()!).ToArray();
            var content = paths.Select(path => File.ReadAllBytes(Path.Combine(root,
                path.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
            var request = new BeginNoteMaterialUploadRequest(
                material.GetProperty("versionId").GetGuid(), material.GetProperty("noteId").GetGuid(),
                material.GetProperty("baseVersionId").GetGuid(), input, manifest,
                material.GetProperty("storageConsent").GetBoolean());
            return new(request, content, paths);
        }
    }
}
