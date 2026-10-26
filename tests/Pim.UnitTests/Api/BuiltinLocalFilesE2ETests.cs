using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

public sealed class BuiltinLocalFilesE2ETests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-local-http", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    public BuiltinLocalFilesE2ETests() => _factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("builtin-local"))
        .WithWebHostBuilder(builder => builder.UseEnvironment("Test").UseSetting("ManagedStorage:Root", Path.Combine(_root, "storage"))
            .UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys")));

    [Fact]
    public async Task NoCloudBindingCanBrowseUploadDownloadRangeRenameTrashAndRestore()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "local-owner");
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var providers = await Data<List<FileProviderDto>>(await client.GetAsync("/api/v1/files/providers"));
        var disk = Assert.Single(providers); Assert.Equal("local", disk.Provider); Assert.True(disk.IsDefault); Assert.Null(disk.TokenExpiresAt);
        var item = await SimpleUpload(client, disk.Id, "/note.txt", "class note"u8.ToArray(), "text/plain");
        var link = await Data<FileLinkDto>(await client.GetAsync($"/api/v1/files/items/{item.Id}/download-url"));
        Assert.StartsWith("/api/v1/files/local/content?ticket=", link.Url);
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(link.Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("class note", await response.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, link.Url); request.Headers.Range = new RangeHeaderValue(0, 4);
        response = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode); Assert.Equal("class", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/files/items/{item.Id}/rename", new RenameFileRequest("renamed.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(link.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/v1/files/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/files/items/{item.Id}/restore", null)).StatusCode);
        var restoredLink = await Data<FileLinkDto>(await client.GetAsync($"/api/v1/files/items/{item.Id}/download-url"));
        Assert.Equal("class note", await anonymous.GetStringAsync(restoredLink.Url));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/v1/files/providers/{disk.Id}")).StatusCode);
    }

    [Fact]
    public async Task LargeLocalFileUsesExistingSessionTransportAndRejectsOtherOwnersAndFakeTickets()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "local-large");
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "local-other");
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        using var stranger = AuthRegistrationTestHost.Authed(_factory, other.AccessToken);
        using var anonymous = _factory.CreateClient();
        var disk = Assert.Single(await Data<List<FileProviderDto>>(await client.GetAsync("/api/v1/files/providers")));
        var upload = await Data<UploadSessionDto>(await client.PostAsJsonAsync("/api/v1/files/items/upload-session", new { providerId = disk.Id, path = "/", fileName = "audio.bin" }));
        var data = new byte[6 * 1024 * 1024]; new Random(7).NextBytes(data);
        string? externalId = null;
        for (var start = 0; start < data.Length; start += 3 * 1024 * 1024)
        {
            using var body = new ByteArrayContent(data, start, 3 * 1024 * 1024);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Headers.ContentRange = new ContentRangeHeaderValue(start, start + 3 * 1024 * 1024 - 1, data.Length);
            var response = await anonymous.PutAsync(upload.UploadUrl, body);
            Assert.Equal(start == 0 ? HttpStatusCode.Accepted : HttpStatusCode.OK, response.StatusCode);
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (start > 0) externalId = json.RootElement.GetProperty("id").GetString();
        }
        var item = await Data<FileItemDto>(await client.PostAsJsonAsync("/api/v1/files/items/upload-session/complete", new { providerId = disk.Id, path = "/", fileName = "audio.bin", uploadedItemId = externalId }));
        Assert.Equal(data.LongLength, item.Size);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/files/items/{item.Id}/download-url")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync("/api/v1/files/local/content?ticket=invalid")).StatusCode);
        var link = await Data<FileLinkDto>(await client.GetAsync($"/api/v1/files/items/{item.Id}/download-url"));
        Assert.Equal(data, await anonymous.GetByteArrayAsync(link.Url));
    }

    [Fact]
    public async Task QuickNoteAttachmentWorksBeforeOpeningTheFilesPageAndUsesTheSameBuiltinSource()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "local-attachment");
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8X8AAAAASUVORK5CYII=");
        using var form = new MultipartFormDataContent(); var bytes = new ByteArrayContent(png); bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(bytes, "file", "photo.png");
        var response = await client.PostAsync("/api/v1/quick-notes/attachments", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var url = json.RootElement.GetProperty("data").GetProperty("downloadUrl").GetString()!;
        Assert.Equal(png, await client.GetByteArrayAsync(url));
        var disk = Assert.Single(await Data<List<FileProviderDto>>(await client.GetAsync("/api/v1/files/providers")));
        Assert.Equal("local", disk.Provider);
    }

    [Fact]
    public async Task ExplicitExpiredCloudDefaultCannotSilentlyFallBackToLocalAttachments()
    {
        var owner = await AuthRegistrationTestHost.SeedAndLoginAsync(_factory, "local-expired");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        db.Add(new FileProviderEntity { UserId = owner.UserId, Provider = "onedrive", AccountId = "old", Status = "expired", IsDefault = true });
        await db.SaveChangesAsync();
        using var client = AuthRegistrationTestHost.Authed(_factory, owner.AccessToken);
        var disks = await Data<List<FileProviderDto>>(await client.GetAsync("/api/v1/files/providers"));
        Assert.True(disks.Single(item => item.Provider == "onedrive").IsDefault);
        Assert.False(disks.Single(item => item.Provider == "local").IsDefault);
        var store = scope.ServiceProvider.GetRequiredService<Pim.Core.Storage.IFileAttachmentStore>();
        var error = await Assert.ThrowsAsync<Pim.Core.Exceptions.DomainException>(() => store.StoreAsync(owner.UserId, "a", new MemoryStream([1]), "text/plain", 1));
        Assert.Equal(5322, error.ErrorCode);
    }

    private static async Task<FileItemDto> SimpleUpload(HttpClient client, Guid provider, string path, byte[] data, string mime)
    {
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(provider.ToString()), "providerId"); form.Add(new StringContent(path), "path");
        var content = new ByteArrayContent(data); content.Headers.ContentType = new MediaTypeHeaderValue(mime); form.Add(content, "file", Path.GetFileName(path));
        return await Data<FileItemDto>(await client.PostAsync("/api/v1/files/items/upload", form));
    }
    private static async Task<T> Data<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }
    public void Dispose() { _factory.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
