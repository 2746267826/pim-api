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
    /// <summary>
    /// 用**真实 HTTP 栈**验证跳转语义：本地起一个只回 302 的最小服务，客户端指向它。
    /// stub handler 不模拟「自动跟随」，只有真实 <see cref="HttpClientHandler"/> 才能证明
    /// 「关掉 AllowAutoRedirect 后确实读得到 Location」——这正是 #342 复审漏掉的那一层。
    /// </summary>
    [Fact]
    public async Task NoRedirectHttpClient_ReadsLocationFromRealRedirectServer()
    {
        var server = new RedirectOnlyServer();
        await server.StartAsync();
        try
        {
            var following = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            var notFollowing = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

            using var followResponse = await following.GetAsync(server.RedirectUrl);
            Assert.Equal(HttpStatusCode.OK, followResponse.StatusCode);
            Assert.Null(followResponse.Headers.Location); // 跟随之后 Location 已被消费

            using var noFollowResponse = await notFollowing.GetAsync(server.RedirectUrl);
            Assert.Equal(HttpStatusCode.Found, noFollowResponse.StatusCode);
            Assert.Equal(server.TargetUrl, noFollowResponse.Headers.Location?.ToString());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>只回 302（Location 指向自身 + /cdn）的最小本地服务，不访问外网。</summary>
    private sealed class RedirectOnlyServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();

        public string BaseUrl { get; }

        public RedirectOnlyServer()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
        }

        public string RedirectUrl => BaseUrl + "item/content";
        public string TargetUrl => BaseUrl + "cdn/item";

        public Task StartAsync()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch
                    {
                        return;
                    }

                    if (context.Request.Url!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = TargetUrl;
                    }
                    else
                    {
                        context.Response.StatusCode = 200;
                        var payload = Encoding.UTF8.GetBytes("cdn-bytes");
                        context.Response.OutputStream.Write(payload);
                    }
                    context.Response.Close();
                }
            });
            return Task.CompletedTask;
        }

        private static int GetFreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch { /* 已停 */ }
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// REQ-12：任何上传路径都**不得覆盖**同名文件——必须显式要求 Graph 用 rename。
    /// `PUT .../content` 的默认冲突行为是 replace（直接覆盖），不显式指定就会静默覆盖，正是 AC-12.2 禁止的。
    /// </summary>
    [Fact]
    public async Task PutNewFileByPath_RequestsRenameConflictBehavior()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(201, new Dictionary<string, object> { ["id"] = "item-1" }),
        };
        var client = CreateClient(handler);

        await client.PutNewFileByPathAsync("at", "/文档/报告.docx", [1, 2, 3], "application/octet-stream");

        var request = Assert.Single(handler.Requests);
        var decoded = Uri.UnescapeDataString(request.Url);
        Assert.Contains("conflictBehavior=rename", decoded);
        Assert.DoesNotContain("conflictBehavior=replace", decoded);
    }

    /// <summary>
    /// REQ-14 / AC-14.2：创建上传会话时必须带 `@microsoft.graph.conflictBehavior = rename`，
    /// 且**不得**出现 `replace` —— 否则大文件上传会覆盖同名文件。
    /// </summary>
    [Fact]
    public async Task CreateUploadSession_RequestsRenameConflictBehaviorAndReturnsUploadUrl()
    {
        var handler = new StubHttpHandler
        {
            Responder = _ => StubHttpHandler.Json(200, new Dictionary<string, object>
            {
                ["uploadUrl"] = "https://upload.example.com/session-1",
                ["expirationDateTime"] = "2026-09-24T12:00:00Z",
            }),
        };
        var client = CreateClient(handler);

        var session = await client.CreateUploadSessionAsync("at", "/文档/大视频.mp4", "大视频.mp4");

        Assert.Equal("https://upload.example.com/session-1", session.UploadUrl);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        var decoded = Uri.UnescapeDataString(request.Url);
        Assert.Contains("createUploadSession", decoded);
        Assert.Contains("/drive/root:/文档/大视频.mp4:", decoded);

        Assert.NotNull(request.Body);
        Assert.Contains("rename", request.Body);
        Assert.DoesNotContain("replace", request.Body);
    }

    private static OneDriveGraphClient CreateClient(StubHttpHandler handler)
        => new(new StubHttpClientFactory(handler));

    private static (OneDriveGraphClient Client, RedirectAwareHttpClientFactory Factory) CreateRedirectAwareClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = new RedirectAwareHttpClientFactory(responder);
        return (new OneDriveGraphClient(factory), factory);
    }

    /// <summary>
    /// 按**注册名**给出不同配置的客户端，忠实复现生产装配：
    /// 内容出口那个命名客户端关闭自动跳转（<c>AllowAutoRedirect=false</c>），其余保持默认跟随。
    /// 于是「兜底不跟随 / 下载内容仍跟随」在测试里真的走了两套 handler，
    /// 而不是「一个 stub 两种期望」——后者正是漏掉本次复审问题的原因。
    /// </summary>
    private sealed class RedirectAwareHttpClientFactory : IHttpClientFactory
    {
        private readonly RedirectModelingHandler _following;
        private readonly RedirectModelingHandler _notFollowing;

        public RedirectAwareHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _following = new RedirectModelingHandler(responder, followRedirects: true);
            _notFollowing = new RedirectModelingHandler(responder, followRedirects: false);
        }

        /// <summary>所有命名客户端发出的请求，按发生顺序。</summary>
        public IReadOnlyList<string> AllRequestUrls
            => _notFollowing.Requests.Select(r => r.Url).Concat(_following.Requests.Select(r => r.Url)).ToList();

        public bool FollowingSawFollowUp => _following.SawFollowUpRequest;

        public int TotalRequests => _notFollowing.Requests.Count + _following.Requests.Count;

        public HttpClient CreateClient(string name)
            => new(name == OneDriveGraphClient.NoRedirectHttpClientName ? _notFollowing : _following)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
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

    /// <summary>
    /// issue #342 复审 Important 的回归：兜底必须在**关掉自动跳转**的请求路径上执行。
    ///
    /// 这条用忠实模拟跳转语义的 handler：若客户端仍开启 AllowAutoRedirect，
    /// 302 会被跟随到「CDN」，末端响应是 200 且 Location 已被消费 → 兜底恒返回 null
    /// （线上就是这个后果，而只提供 stub 的旧测试永远发现不了）。
    /// 断言：① 拿得到 CDN 地址；② 没有发生跟随请求（说明跳转确实被关闭）。
    /// </summary>
    [Fact]
    public async Task GetDownloadUrl_FallbackDoesNotFollowRedirect()
    {
        var cdn = "https://cdn.example.com/file?tempauth=xyz";
        var (client, factory) = CreateRedirectAwareClient(
            request => request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? RedirectModelingHandler.Redirect(cdn)
                : StubHttpHandler.Json(200, new Dictionary<string, object>
                {
                    ["id"] = "item-1",
                    ["file"] = new Dictionary<string, object> { ["mimeType"] = "image/png" },
                }));

        var url = await client.GetDownloadUrlAsync("at", "item-1");

        Assert.Equal(cdn, url);
        // 只有「条目请求 + /content」两次；若走了会跟随的那套客户端，就会出现第三个 CDN 请求
        Assert.Equal(2, factory.TotalRequests);
        Assert.False(factory.FollowingSawFollowUp, "兜底请求不得走会跟随跳转的客户端");
        Assert.Contains(factory.AllRequestUrls, u => u.EndsWith("/content", StringComparison.Ordinal));
    }

    /// <summary>
    /// 对照：<see cref="OneDriveGraphClient.DownloadSmallAsync"/> 依赖**跟随**跳转取内容，
    /// 关闭跳转不得影响它（回归保护：两条路径必须是不同的 HttpClient 配置）。
    /// </summary>
    [Fact]
    public async Task DownloadSmall_StillFollowsRedirectToFetchContent()
    {
        var (client, factory) = CreateRedirectAwareClient(
            request => request.RequestUri!.AbsolutePath.Contains("/cdn/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("real-bytes") }
                : RedirectModelingHandler.Redirect("https://cdn.example.com/cdn/item-1"));

        var content = await client.DownloadSmallAsync("at", "item-1", maxBytes: 1024);

        Assert.NotNull(content);
        Assert.Equal("real-bytes", System.Text.Encoding.UTF8.GetString(content!.Bytes));
        Assert.True(factory.FollowingSawFollowUp, "下载内容依赖跟随跳转；本修复不得把它一并关掉");
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
