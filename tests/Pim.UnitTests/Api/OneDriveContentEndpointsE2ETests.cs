using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// OneDrive 内容出口端到端测试（P2）：稳定直链 302、缩略图、预览地址、文本读写与快照、敏感路径 403。
/// </summary>
public class OneDriveContentEndpointsE2ETests
{
    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient User, Guid ProviderId, Guid ItemId, OneDriveFilesEndpointsE2ETests.E2EGraphClient Graph)>
        CreateUserWithFileAsync(string dbName, string path = "/工作/a.txt", string? mimeType = "text/plain")
    {
        var graph = new OneDriveFilesEndpointsE2ETests.E2EGraphClient();
        var factory = OneDriveFilesEndpointsE2ETests.CreateFactory(dbName, graph);
        var anon = factory.CreateClient();
        var username = ("files-p2-" + Guid.NewGuid().ToString("N"))[..18];
        var token = await OneDriveFilesEndpointsE2ETests.RegisterAndGetTokenAsync(anon, username);
        var user = OneDriveFilesEndpointsE2ETests.Authed(factory, token);

        await user.PostAsJsonAsync("/api/v1/files/providers/onedrive", new { clientId = "cid-p2" });
        var providerId = await OneDriveFilesEndpointsE2ETests.GetSingleProviderIdAsync(factory);
        await user.GetAsync($"/api/v1/files/providers/{providerId}/binding-status");
        await user.PostAsync($"/api/v1/files/providers/{providerId}/sync", null);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var item = await db.Set<FileItemEntity>().SingleAsync(row => row.ProviderId == providerId && row.ItemType == "file");
        if (path != item.Path || mimeType != item.MimeType)
        {
            item.Path = path;
            item.MimeType = mimeType;
            await db.SaveChangesAsync();
        }

        return (factory, user, providerId, item.Id, graph);
    }

    [Fact]
    public async Task ContentEndpoint_Redirects302_ToFreshLink_WithoutLeakingToken()
    {
        var (factory, user, _, itemId, _) = await CreateUserWithFileAsync($"p2-content-{Guid.NewGuid():N}");
        var noRedirect = ((TestServer)factory.Server).CreateClient();
        noRedirect.BaseAddress = user.BaseAddress;
        noRedirect.DefaultRequestHeaders.Authorization = user.DefaultRequestHeaders.Authorization;

        var response = await noRedirect.GetAsync($"/api/v1/files/items/{itemId}/content");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith("https://", location);
        // 302 响应体不应携带任何内容（链接只在 Location 头里）
        Assert.Equal(0, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task TextFlow_GetEdit_ListSnapshots_Restore()
    {
        var (factory, user, _, itemId, graph) = await CreateUserWithFileAsync($"p2-text-{Guid.NewGuid():N}");

        // 1. 读文本（fake 返回 "e2e 文本内容"）
        var getResp = await user.GetAsync($"/api/v1/files/items/{itemId}/text");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        using (var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal("e2e 文本内容", data.GetProperty("content").GetString());
            Assert.False(data.GetProperty("truncated").GetBoolean());
        }

        // 2. 编辑 → 编辑前快照 + PUT 新内容
        var saveResp = await user.PutAsJsonAsync($"/api/v1/files/items/{itemId}/text", new { content = "编辑后的内容" });
        Assert.Equal(HttpStatusCode.OK, saveResp.StatusCode);
        var put = Assert.Single(graph.PutCalls);
        Assert.Equal("编辑后的内容", System.Text.Encoding.UTF8.GetString(put.Bytes));

        // 3. 快照列表含编辑前内容
        var listResp = await user.GetAsync($"/api/v1/files/items/{itemId}/snapshots");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        using (var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync()))
        {
            var snapshots = doc.RootElement.GetProperty("data");
            Assert.Equal(1, snapshots.GetArrayLength());
            Assert.Equal("e2e 文本内容", snapshots[0].GetProperty("content").GetString());
            Assert.Equal("pre-edit", snapshots[0].GetProperty("reason").GetString());
            var snapshotId = snapshots[0].GetProperty("id").GetGuid();

            // 4. 恢复快照 → PUT 历史内容，且恢复前再存一份快照
            var restoreResp = await user.PostAsync($"/api/v1/files/items/{itemId}/snapshots/{snapshotId}/restore", null);
            Assert.Equal(HttpStatusCode.OK, restoreResp.StatusCode);
            Assert.Equal(2, graph.PutCalls.Count);
            Assert.Equal("e2e 文本内容", System.Text.Encoding.UTF8.GetString(graph.PutCalls[1].Bytes));
        }
    }

    [Fact]
    public async Task SensitivePath_ContentAndText_Returns403()
    {
        var (factory, user, _, itemId, graph) = await CreateUserWithFileAsync($"p2-secret-{Guid.NewGuid():N}", path: "/Secrets/密钥.txt");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.GetAsync($"/api/v1/files/items/{itemId}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.GetAsync($"/api/v1/files/items/{itemId}/text")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PutAsJsonAsync($"/api/v1/files/items/{itemId}/text", new { content = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.GetAsync($"/api/v1/files/items/{itemId}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.GetAsync($"/api/v1/files/items/{itemId}/preview-url")).StatusCode);

        // 全程没有触达 Graph
        Assert.Empty(graph.DownloadUrlCalls);
        Assert.Empty(graph.DownloadSmallCalls);
        Assert.Empty(graph.PutCalls);
    }

    [Fact]
    public async Task ThumbnailEndpoint_Redirects302()
    {
        var (factory, user, _, itemId, _) = await CreateUserWithFileAsync($"p2-thumb-{Guid.NewGuid():N}", mimeType: "image/png");
        var noRedirect = ((TestServer)factory.Server).CreateClient();
        noRedirect.BaseAddress = user.BaseAddress;
        noRedirect.DefaultRequestHeaders.Authorization = user.DefaultRequestHeaders.Authorization;

        var response = await noRedirect.GetAsync($"/api/v1/files/items/{itemId}/thumbnail?size=large");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("thumb", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task PreviewUrl_ReturnsEmbedLink()
    {
        var (factory, user, _, itemId, graph) = await CreateUserWithFileAsync($"p2-preview-{Guid.NewGuid():N}", mimeType: "application/pdf");

        var response = await user.GetAsync($"/api/v1/files/items/{itemId}/preview-url");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("preview", doc.RootElement.GetProperty("data").GetProperty("url").GetString());
        Assert.Single(graph.PreviewCalls);
    }
}
