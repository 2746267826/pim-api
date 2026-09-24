using System.Net;
using System.Text;
using System.Text.Json;
using Pim.Module.Files.Providers;

namespace Pim.UnitTests.Files;

/// <summary>
/// 可编程的 IOneDriveGraphClient 测试替身：按脚本依次抛异常/返回页，并记录调用。
/// </summary>
internal sealed class FakeOneDriveGraphClient : IOneDriveGraphClient
{
    public OneDriveDeviceCodeStart DeviceCode { get; set; } = new(
        "device-code", "USER-CODE", "https://www.microsoft.com/link", 900);

    public OneDriveTokenResult Token { get; set; } = new(
        "access-token", "refresh-token", 3600, "Files.ReadWrite.All offline_access");

    public OneDriveDriveInfo Drive { get; set; } = new("drive-1", "personal", 378_000_000_000, 1_100_000_000_000);

    public OneDriveAccountInfo Me { get; set; } = new("acc-1", "Test User");

    /// <summary>GetDeltaPageAsync 的脚本：元素为页或要抛出的异常。</summary>
    public Queue<object> DeltaScript { get; } = new();

    public List<(string? AccessToken, string Url)> DeltaRequests { get; } = [];

    public int PollCalls { get; private set; }
    public int RefreshCalls { get; private set; }

    public Exception? PollException { get; set; }
    public Exception? RefreshException { get; set; }
    public Action? OnPollAsync { get; set; }

    public Task<OneDriveDeviceCodeStart> RequestDeviceCodeAsync(string clientId, CancellationToken ct = default)
        => Task.FromResult(DeviceCode);

    public Task<OneDriveTokenResult> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken ct = default)
    {
        PollCalls++;
        OnPollAsync?.Invoke();
        if (PollException is not null) throw PollException;
        return Task.FromResult(Token);
    }

    public Task<OneDriveTokenResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
    {
        RefreshCalls++;
        if (RefreshException is not null) throw RefreshException;
        return Task.FromResult(Token with { AccessToken = $"access-token-{RefreshCalls}" });
    }

    public Task<OneDriveDriveInfo> GetDriveAsync(string accessToken, CancellationToken ct = default)
        => Task.FromResult(Drive);

    public Task<OneDriveAccountInfo> GetMeAsync(string accessToken, CancellationToken ct = default)
        => Task.FromResult(Me);

    public Task<OneDriveDeltaPage> GetDeltaPageAsync(string accessToken, string url, CancellationToken ct = default)
    {
        DeltaRequests.Add((accessToken, url));
        if (DeltaScript.Count == 0)
            return Task.FromResult(new OneDriveDeltaPage([], null, "https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=done"));
        var step = DeltaScript.Dequeue();
        if (step is Exception error) throw error;
        return Task.FromResult((OneDriveDeltaPage)step);
    }

    // ---- P2：直链 / 缩略图 / 预览 / 小文件读写 ----

    public string? DownloadUrl { get; set; } = "https://my.microsoftpersonalcontent.com/dl?tempauth=xyz";
    public string? ThumbnailUrl { get; set; } = "https://my.microsoftpersonalcontent.com/thumb?tempauth=t";
    public string? PreviewUrl { get; set; } = "https://www.onedrive.com/preview?resid=x";
    public OneDriveSmallContent? SmallContent { get; set; } = new("hello"u8.ToArray(), "text/plain");
    public Exception? DownloadSmallException { get; set; }
    public Exception? ThumbnailException { get; set; }

    public List<(string AccessToken, string ItemId)> DownloadUrlCalls { get; } = [];
    public List<(string AccessToken, string ItemId, string Size)> ThumbnailCalls { get; } = [];
    public List<(string AccessToken, string ItemId)> PreviewCalls { get; } = [];
    public List<(string AccessToken, string ItemId, long MaxBytes)> DownloadSmallCalls { get; } = [];
    public List<(string AccessToken, string ItemId, byte[] Bytes, string ContentType)> PutCalls { get; } = [];

    public Task<string?> GetDownloadUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        DownloadUrlCalls.Add((accessToken, itemId));
        if (DownloadUrlException is not null) throw DownloadUrlException;
        // 按 item 维度模拟「远端已删除」：恢复流程会逐个校验子孙是否仍在 OneDrive
        if (MissingItemIds.Contains(itemId))
        {
            throw new OneDriveGraphException(404, null, $"Graph 404：{itemId} not found");
        }

        // 按 item 维度注入其它 Graph 故障（如 429/500），用于验证错误传播而非「当作不存在」
        if (ItemExceptions.TryGetValue(itemId, out var exception))
        {
            throw exception;
        }

        return Task.FromResult(DownloadUrl);
    }

    /// <summary>这些 item 在远端已不存在（GetDownloadUrlAsync 抛 404）。</summary>
    public HashSet<string> MissingItemIds { get; } = new(StringComparer.Ordinal);

    /// <summary>按 item id 注入指定 Graph 异常（非 404 场景）。</summary>
    public Dictionary<string, Exception> ItemExceptions { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetThumbnailUrlAsync(string accessToken, string itemId, string size, CancellationToken ct = default)
    {
        ThumbnailCalls.Add((accessToken, itemId, size));
        if (ThumbnailException is not null) throw ThumbnailException;
        return Task.FromResult(ThumbnailUrl);
    }

    public Task<string?> GetPreviewUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        PreviewCalls.Add((accessToken, itemId));
        return Task.FromResult(PreviewUrl);
    }

    public Task<OneDriveSmallContent?> DownloadSmallAsync(string accessToken, string itemId, long maxBytes, CancellationToken ct = default)
    {
        DownloadSmallCalls.Add((accessToken, itemId, maxBytes));
        if (DownloadSmallException is not null) throw DownloadSmallException;
        return Task.FromResult(SmallContent);
    }

    public Task PutSmallContentAsync(string accessToken, string itemId, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        PutCalls.Add((accessToken, itemId, bytes, contentType));
        return Task.CompletedTask;
    }

    public List<(string AccessToken, string ItemId, string? NewName, string? NewParentId)> PatchCalls { get; } = [];
    public List<(string AccessToken, string ItemId)> DeleteCalls { get; } = [];
    public string? WebUrl { get; set; } = "https://onedrive.live.com/redir?resid=x";
    public string NewItemId { get; set; } = "new-item-id";
    public Exception? DownloadUrlException { get; set; }
    public List<(string AccessToken, string ItemPath, byte[] Bytes, string ContentType)> PutNewFileCalls { get; } = [];

    public Task<string> PatchItemAsync(string accessToken, string itemId, string? newName, string? newParentId, CancellationToken ct = default)
    {
        PatchCalls.Add((accessToken, itemId, newName, newParentId));
        return Task.FromResult(itemId);
    }

    public Task DeleteItemAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        DeleteCalls.Add((accessToken, itemId));
        return Task.CompletedTask;
    }

    public Task<string> PutNewFileByPathAsync(string accessToken, string itemPath, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        PutNewFileCalls.Add((accessToken, itemPath, bytes, contentType));
        return Task.FromResult(NewItemId);
    }

    public Task<string?> GetItemWebUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
        => Task.FromResult(WebUrl);
}

internal static class OneDriveDeltaPageFactory
{
    public static OneDriveDeltaItem Folder(string id, string name, string? parentId = null, string? parentPath = null, string? ctag = "folder-ctag")
        => new(id, parentId, parentPath ?? "/drive/root:", name, IsFolder: true, IsRemoved: false,
            Size: null, MimeType: null, Ctag: ctag, ModifiedAt: DateTimeOffset.Parse("2026-09-01T08:00:00Z"));

    public static OneDriveDeltaItem File(
        string id,
        string name,
        long size = 1024,
        string? mimeType = "text/plain",
        string? parentId = null,
        string? parentPath = null,
        string? ctag = "file-ctag")
        => new(id, parentId, parentPath ?? "/drive/root:", name, IsFolder: false, IsRemoved: false,
            Size: size, MimeType: mimeType, Ctag: ctag, ModifiedAt: DateTimeOffset.Parse("2026-09-02T09:30:00Z"));

    public static OneDriveDeltaItem Removed(string id)
        => new(id, null, null, "gone", IsFolder: false, IsRemoved: true,
            Size: null, MimeType: null, Ctag: null, ModifiedAt: DateTimeOffset.Parse("2026-09-03T10:00:00Z"));

    public static OneDriveDeltaPage Page(params OneDriveDeltaItem[] items)
        => new(items, null, $"https://graph.microsoft.com/v1.0/me/drive/root/delta?$deltatoken=done-{Guid.NewGuid():N}");

    public static OneDriveDeltaPage NextPage(params OneDriveDeltaItem[] items)
        => new(items, $"https://graph.microsoft.com/v1.0/me/drive/root/delta?$skiptoken=next-{Guid.NewGuid():N}", null);
}

/// <summary>针对 OneDriveGraphClient 原始 HTTP 行为的桩 Handler。</summary>
/// <summary>
/// 忠实模拟 <see cref="HttpClientHandler"/> 的**自动跟随跳转**语义：
/// <see cref="HttpMessageHandler"/> 自身的 <c>SendAsync</c> 不做跳转，所以只提供 stub 的
/// 测试永远看不到「302 被跟随」这条真实行为——issue #342 复审正是这样漏掉的。
/// 用 <see cref="HttpClientHandler"/> 的 <c>AllowAutoRedirect</c> 开关来复现：
/// 开启时沿 <c>Location</c> 继续请求并返回末端响应（<c>Location</c> 已被消费掉），
/// 关闭时原样返回 302（调用方才能读到 <c>Location</c>）。
/// </summary>
internal sealed class RedirectModelingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private readonly bool _followRedirects;

    public RedirectModelingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, bool followRedirects)
    {
        _responder = responder;
        _followRedirects = followRedirects;
    }

    public List<(string Method, string Url, string? Authorization)> Requests { get; } = [];

    public bool SawFollowUpRequest => Requests.Count > 1;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            request.Headers.Authorization?.ToString()));

        var response = _responder(request);

        if (_followRedirects && IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
        {
            // 真实 HttpClientHandler 的行为：跟随 Location、丢弃原响应头
            var target = location.IsAbsoluteUri
                ? location
                : new Uri(request.RequestUri!, location);
            // 跟随请求同样要记录，否则 SawFollowUpRequest 恒为 false（测试替身自身的缺陷）
            Requests.Add((HttpMethod.Get.Method, target.ToString(), null));
            using var followUp = new HttpRequestMessage(HttpMethod.Get, target);
            return Task.FromResult(_responder(followUp));
        }

        return Task.FromResult(response);
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static HttpResponseMessage Redirect(string location) 
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent(string.Empty) };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
}

internal sealed class StubHttpHandler : HttpMessageHandler
{
    public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; init; }

    public List<(string Method, string Url, string? Body, string? Authorization)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null
            ? null
            : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        Requests.Add((
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            body,
            request.Headers.Authorization?.ToString()));
        return Task.FromResult(Responder(request));
    }

    public static HttpResponseMessage Json(int statusCode, object payload, IDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            response.Headers.TryAddWithoutValidation(key, value);
        }
        return response;
    }
}
