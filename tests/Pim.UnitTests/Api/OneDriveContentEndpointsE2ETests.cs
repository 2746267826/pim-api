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

    // ===================== P4a 复审：MCP 契约对应的路由必须真的可达 =====================

    /// <summary>
    /// MCP <c>read_file_text</c> 映射到 GET /items/{id}/extracted-text。此前该路由从未注册，
    /// 工具调用恒 404——本用例锁死「契约里的路由真实存在且能返回文本」。
    /// </summary>
    [Fact]
    public async Task ExtractedText_EndpointServesText_ForReadFileTextTool()
    {
        var (factory, user, _, itemId, graph) = await CreateUserWithFileAsync($"p4a-extext-{Guid.NewGuid():N}");
        graph.SmallContent = new("e2e 抽取文本"u8.ToArray(), "text/plain");

        var response = await user.GetAsync($"/api/v1/files/items/{itemId}/extracted-text?maxBytes=1024");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Contains("e2e 抽取文本", data.GetProperty("content").GetString());
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    /// <summary>
    /// MCP <c>restore_file</c> 映射到 POST /items/{id}/restore。此前同样只有处理器没有路由，
    /// 恒 404。本用例验证软删后的文件能经该端点恢复。
    /// </summary>
    [Fact]
    public async Task RestoreItem_EndpointUndeletesSoftDeletedItem_ForRestoreFileTool()
    {
        var (factory, user, _, itemId, graph) = await CreateUserWithFileAsync($"p4a-restore-{Guid.NewGuid():N}");

        // 先经 DELETE 软删（走 OneDriveWriteService，Graph 成功后才本地软删）
        var deleteResponse = await user.DeleteAsync($"/api/v1/files/items/{itemId}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var item = await db.Set<FileItemEntity>().SingleAsync(row => row.Id == itemId);
            Assert.True(item.IsDeleted, "删除后本地应标记为软删");
        }

        // 远端仍存在（fake 返回 200）→ 应能恢复
        var restoreResponse = await user.PostAsync($"/api/v1/files/items/{itemId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var item = await db.Set<FileItemEntity>().SingleAsync(row => row.Id == itemId);
            Assert.False(item.IsDeleted, "恢复后本地软删标记应清除");
        }
    }

    /// <summary>
    /// 敏感路径在 read_file_text 出口同样拦截（设计 §13）：不能因为多了一个新出口就绕过。
    /// </summary>
    [Fact]
    public async Task ExtractedText_SensitivePath_IsRejected()
    {
        var (factory, user, _, itemId, _) = await CreateUserWithFileAsync(
            $"p4a-extext-sens-{Guid.NewGuid():N}", path: "/Secrets/密码.txt");

        var response = await user.GetAsync($"/api/v1/files/items/{itemId}/extracted-text");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// API 级隐私回归：把各内容出口串起来验证敏感路径**一致**被拒。
    /// 单点用例分散在各服务，这里确认端点层没有漏掉任何一条出口
    /// （历史上 open-link 就曾漏过；搜索也曾在另一条路径上漏过滤）。
    /// </summary>
    [Fact]
    public async Task SensitivePath_IsRejectedOnEveryContentEgress()
    {
        var (factory, user, _, itemId, _) = await CreateUserWithFileAsync(
            $"p4-egress-{Guid.NewGuid():N}", path: "/Passwords/凭据.txt");

        var egresses = new (string Method, string Url)[]
        {
            ("GET", $"/api/v1/files/items/{itemId}/content"),
            ("GET", $"/api/v1/files/items/{itemId}/thumbnail"),
            ("GET", $"/api/v1/files/items/{itemId}/preview-url"),
            ("GET", $"/api/v1/files/items/{itemId}/text"),
            ("GET", $"/api/v1/files/items/{itemId}/snapshots"),
            ("GET", $"/api/v1/files/items/{itemId}/extracted-text"),
            ("GET", $"/api/v1/files/items/{itemId}/open-link"),
        };

        foreach (var (method, url) in egresses)
        {
            var response = method == "GET"
                ? await user.GetAsync(url)
                : await user.PostAsync(url, null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // 搜索结果里也不能出现敏感项
        var search = await user.GetAsync("/api/v1/files/search?q=" + Uri.EscapeDataString("凭据"));
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var doc = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").GetProperty("items");
        Assert.Equal(0, items.GetArrayLength());
    }

    /// <summary>
    /// 上传端点只认当前用户的 OneDrive 绑定：他人的 providerId 传进来应 404（不泄露存在性），
    /// 而不是把文件写进别人的网盘。
    /// </summary>
    [Fact]
    public async Task Upload_WithForeignProviderId_IsRejected()
    {
        var (factory, alice, providerId, _, _) = await CreateUserWithFileAsync($"p4-upload-iso-{Guid.NewGuid():N}");
        var anon = factory.CreateClient();
        var bobToken = await OneDriveFilesEndpointsE2ETests.RegisterAndGetTokenAsync(
            anon, ("p4-bob-" + Guid.NewGuid().ToString("N"))[..18]);
        var bob = OneDriveFilesEndpointsE2ETests.Authed(factory, bobToken);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(providerId.ToString()), "providerId");
        form.Add(new StringContent("/偷渡.txt"), "path");
        form.Add(new ByteArrayContent("payload"u8.ToArray()), "file", "偷渡.txt");

        var response = await bob.PostAsync("/api/v1/files/items/upload", form);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>上传成功后回读的 DTO 必须是刚落库的那一条（名称/路径/大小一致）。</summary>
    [Fact]
    public async Task Upload_ReturnsConvergedMetadata()
    {
        var (factory, user, providerId, _, _) = await CreateUserWithFileAsync($"p4-upload-dto-{Guid.NewGuid():N}");

        // 目标文件夹必须已在本地元数据里（未同步的目录会被明确拒绝），fixture 里有 /合同
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(providerId.ToString()), "providerId");
        form.Add(new StringContent("/合同/新上传.txt"), "path");
        form.Add(new ByteArrayContent("hello upload"u8.ToArray()), "file", "新上传.txt");

        var response = await user.PostAsync("/api/v1/files/items/upload", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("新上传.txt", data.GetProperty("name").GetString());
        Assert.Equal("/合同/新上传.txt", data.GetProperty("path").GetString());
        // 未显式声明 Content-Type 的 multipart 部件按八位字节流处理（与上传端点的默认一致）
        Assert.Equal("application/octet-stream", data.GetProperty("mimeType").GetString());
        Assert.Equal(12, data.GetProperty("size").GetInt64());
    }
}
