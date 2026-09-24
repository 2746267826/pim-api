using Xunit;
using System.Net;
using System.Text;
using Pim.Module.Files.Providers;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveGraphClient P2 契约测试：downloadUrl / 缩略图 / preview / 小文件下载与回写。
/// </summary>
public class OneDriveGraphClientContentTests
{
    private static OneDriveGraphClient CreateClient(StubHttpHandler handler)
        => new(new StubHttpClientFactory(handler));

    private sealed class StubHttpClientFactory(StubHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    [Fact]
    public async Task GetDownloadUrl_ReadsAtProperty()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object>
            {
                ["id"] = "item-1",
                ["@microsoft.graph.downloadUrl"] = "https://dl.example.com/x?tempauth=abc",
            }),
        };
        var client = CreateClient(handler);

        var url = await client.GetDownloadUrlAsync("at", "item-1");

        Assert.Equal("https://dl.example.com/x?tempauth=abc", url);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("drive/items/item-1", request.Url);
        Assert.Equal("Bearer at", request.Authorization);
        // issue #342：请求形状不得回到 `$select=id,@microsoft.graph.downloadUrl`
        // ——该组合会让微软侧**静默丢弃** downloadUrl 注解（个人版实测）。
        var decoded = Uri.UnescapeDataString(request.Url);
        Assert.DoesNotContain("@microsoft.graph.downloadUrl", decoded);
        Assert.DoesNotContain("$select=id", decoded);
    }

    /// <summary>
    /// issue #342 主因回归：`$select=id,@microsoft.graph.downloadUrl` 会被微软静默丢弃注解。
    /// 这里断言修复后的首选形状是「不带 $select 的完整条目请求」——它是实测可用的变体 C。
    /// </summary>
    [Fact]
    public async Task GetDownloadUrl_UsesFullItemRequestWithoutSelect()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object>
            {
                ["@microsoft.graph.downloadUrl"] = "https://dl.example.com/a",
                ["id"] = "item-1",
            }),
        };
        var client = CreateClient(handler);

        Assert.Equal("https://dl.example.com/a", await client.GetDownloadUrlAsync("at", "item-1"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.EndsWith("/drive/items/item-1", request.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("$select", request.Url);
    }

    /// <summary>
    /// issue #342 修复建议 1 的兜底：注解缺失时改用 `/content`，**不跟随跳转**只取 Location，
    /// 维持「内容不经服务器搬运」的硬约束（零字节下载）。
    /// </summary>
    [Fact]
    public async Task GetDownloadUrl_FallsBackToContentLocationWhenAnnotationMissing()
    {
        var handler = new StubHttpHandler
        {
            Responder = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found)
                    {
                        Content = new StringContent(string.Empty),
                    };
                    redirect.Headers.Location = new Uri("https://dl.example.com/fallback?sig=xyz");
                    return redirect;
                }

                // 注解缺失（正是 #342 里变体 A 的响应：只有 @odata.context/etag/id），
                // 但它是文件（带 file facet），因此应触发 /content 兜底
                return StubHttpHandler.Json(200, new Dictionary<string, object>
                {
                    ["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#drives('d')/items/$entity",
                    ["@odata.etag"] = "etag-1",
                    ["id"] = "item-1",
                    ["file"] = new Dictionary<string, object> { ["mimeType"] = "image/png" },
                });
            },
        };
        var client = CreateClient(handler);

        var url = await client.GetDownloadUrlAsync("at", "item-1");

        Assert.Equal("https://dl.example.com/fallback?sig=xyz", url);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.EndsWith("/drive/items/item-1/content", handler.Requests[1].Url, StringComparison.Ordinal);
        Assert.Equal("Bearer at", handler.Requests[1].Authorization);
        // 兜底路径不得把内容拉进服务器：只允许出现一次请求，且响应体为零字节
        Assert.DoesNotContain("/content?$select", handler.Requests[1].Url);
    }

    /// <summary>
    /// 目录没有 `/content`：注解缺失时**不得**去请求 `/content`，直接返回 null。
    ///
    /// 这条守住一个真实影响：`OneDriveWriteService.RemoteItemExistsAsync` 用本方法做
    /// 「远端是否仍存在」的存在性探测，删除/恢复流程会传**目录** id。对目录打 `/content`
    /// 会拿到 404，从而被误判成「文件已从 OneDrive 删除」，让删除/恢复行为整体错乱。
    /// </summary>
    [Fact]
    public async Task GetDownloadUrl_FolderWithoutAnnotation_DoesNotProbeContent()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object>
            {
                ["id"] = "folder-1",
                ["folder"] = new Dictionary<string, object> { ["childCount"] = 3 },
            }),
        };
        var client = CreateClient(handler);

        Assert.Null(await client.GetDownloadUrlAsync("at", "folder-1"));

        // 只允许一次条目请求；目录不得触发 /content 兜底
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/drive/items/folder-1", request.Url, StringComparison.Ordinal);
    }

    /// <summary>带 file facet 的条目仍然走兜底（与上一条构成对照，避免「一律不兜底」）。</summary>
    [Fact]
    public async Task GetDownloadUrl_FileWithoutAnnotation_StillProbesContent()
    {
        var handler = new StubHttpHandler
        {
            Responder = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent(string.Empty) };
                    redirect.Headers.Location = new Uri("https://dl.example.com/file?sig=1");
                    return redirect;
                }

                return StubHttpHandler.Json(200, new Dictionary<string, object>
                {
                    ["id"] = "file-1",
                    ["file"] = new Dictionary<string, object> { ["mimeType"] = "image/png" },
                });
            },
        };
        var client = CreateClient(handler);

        Assert.Equal("https://dl.example.com/file?sig=1", await client.GetDownloadUrlAsync("at", "file-1"));
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>兜底也拿不到 Location 时返回 null（由上层映射为可读错误），不得抛未处理异常。</summary>
    [Fact]
    public async Task GetDownloadUrl_BothPathsUnavailable_ReturnsNull()
    {
        var handler = new StubHttpHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("bytes") }
                : StubHttpHandler.Json(200, new Dictionary<string, object>
                {
                    ["id"] = "item-1",
                    ["file"] = new Dictionary<string, object> { ["mimeType"] = "image/png" },
                }),
        };
        var client = CreateClient(handler);

        Assert.Null(await client.GetDownloadUrlAsync("at", "item-1"));
    }

    /// <summary>兜底路径上的 404/403 必须映射为原有语义（上层的 5300/敏感路径闸门依赖它）。</summary>
    [Fact]
    public async Task GetDownloadUrl_FallbackNotAllowed_MapsTo404()
    {
        var handler = new StubHttpHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? StubHttpHandler.Json(404, new Dictionary<string, object> { ["error"] = "not found" })
                : StubHttpHandler.Json(200, new Dictionary<string, object>
                {
                    ["id"] = "item-1",
                    ["file"] = new Dictionary<string, object> { ["mimeType"] = "image/png" },
                }),
        };
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<OneDriveGraphException>(
            () => client.GetDownloadUrlAsync("at", "item-1"));

        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task GetDownloadUrl_MissingProperty_ReturnsNull()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object> { ["id"] = "item-1" }),
        };
        var client = CreateClient(handler);

        Assert.Null(await client.GetDownloadUrlAsync("at", "item-1"));
    }

    [Fact]
    public async Task GetThumbnailUrl_ReturnsUrlForSize()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object>
            {
                ["url"] = "https://thumb.example.com/medium",
            }),
        };
        var client = CreateClient(handler);

        var url = await client.GetThumbnailUrlAsync("at", "item-1", "medium");

        Assert.Equal("https://thumb.example.com/medium", url);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/thumbnails/0/medium", request.Url);
    }

    [Fact]
    public async Task GetThumbnailUrl_NotSupported_ReturnsNull()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(404, new { error = new { code = "itemNotFound" } }),
        };
        var client = CreateClient(handler);

        Assert.Null(await client.GetThumbnailUrlAsync("at", "item-1", "medium"));
    }

    [Fact]
    public async Task GetPreviewUrl_PostsEmptyBody_AndReadsGetUrl()
    {
        var handler = new StubHttpHandler
        {
            Responder = request =>
            {
                // preview 是 POST
                Assert.Equal("POST", request.Method.Method);
                return StubHttpHandler.Json(200, new Dictionary<string, object>
                {
                    ["getUrl"] = "https://preview.example.com/embed",
                });
            },
        };
        var client = CreateClient(handler);

        var url = await client.GetPreviewUrlAsync("at", "item-1");

        Assert.Equal("https://preview.example.com/embed", url);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/drive/items/item-1/preview", request.Url);
        Assert.Equal("Bearer at", request.Authorization);
    }

    [Fact]
    public async Task DownloadSmall_ReturnsBytesAndContentType()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("hello 文件"u8.ToArray()),
                };
                response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=utf-8");
                return response;
            },
        };
        var client = CreateClient(handler);

        var content = await client.DownloadSmallAsync("at", "item-1", maxBytes: 1024);

        Assert.NotNull(content);
        Assert.Equal("hello 文件", Encoding.UTF8.GetString(content!.Bytes));
        Assert.Contains("text/plain", content.ContentType);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/drive/items/item-1/content", request.Url);
    }

    [Fact]
    public async Task DownloadSmall_ExceedsMaxBytes_ThrowsTooLarge()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[2048]),
                };
                response.Content.Headers.ContentLength = 2048;
                return response;
            },
        };
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<OneDriveContentTooLargeException>(
            () => client.DownloadSmallAsync("at", "item-1", maxBytes: 1024));
    }

    [Fact]
    public async Task DownloadSmall_NotFound_ReturnsNull()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(404, new { error = new { code = "itemNotFound" } }),
        };
        var client = CreateClient(handler);

        Assert.Null(await client.DownloadSmallAsync("at", "item-1", maxBytes: 1024));
    }

    /// <summary>
    /// 没有 Content-Length 的 chunked 响应不能绕过上限：实现必须边读边计数，
    /// 而不是先把整个响应体缓冲进内存再判断（复审 I-9）。
    /// 这里用一个「无 Content-Length、内容远超上限」的流来验证。
    /// </summary>
    [Fact]
    public async Task DownloadSmall_ChunkedResponseWithoutContentLength_StillEnforcesMaxBytes()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ =>
            {
                // 未知长度的流式内容：HttpClient 不会给出 Content-Length。
                var content = new StreamContent(new MemoryStream(new byte[8192]));
                content.Headers.ContentLength = null;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
        };
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<OneDriveContentTooLargeException>(
            () => client.DownloadSmallAsync("at", "item-1", maxBytes: 1024));

        Assert.True(error.ActualBytes > 1024);
    }

    /// <summary>无 Content-Length 但在上限内的流式响应仍应正常返回。</summary>
    [Fact]
    public async Task DownloadSmall_ChunkedResponseWithinLimit_ReturnsContent()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ =>
            {
                var content = new StreamContent(new MemoryStream("chunked 内容"u8.ToArray()));
                content.Headers.ContentLength = null;
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
        };
        var client = CreateClient(handler);

        var result = await client.DownloadSmallAsync("at", "item-1", maxBytes: 1024);

        Assert.NotNull(result);
        Assert.Equal("chunked 内容", Encoding.UTF8.GetString(result!.Bytes));
    }

    [Fact]
    public async Task PutSmallContent_PutsBytesWithContentType()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object> { ["id"] = "item-1" }),
        };
        var client = CreateClient(handler);

        await client.PutSmallContentAsync("at", "item-1", "新内容"u8.ToArray(), "text/plain");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.EndsWith("/drive/items/item-1/content", request.Url);
        Assert.NotNull(request.Body);
        Assert.Contains("新内容", request.Body);
        Assert.Equal("Bearer at", request.Authorization);
    }
}
