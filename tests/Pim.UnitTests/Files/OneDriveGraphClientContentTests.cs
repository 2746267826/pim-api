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
        Assert.Contains("$select=", request.Url);
        Assert.Contains("microsoft.graph.downloadUrl", Uri.UnescapeDataString(request.Url));
        Assert.Equal("Bearer at", request.Authorization);
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
