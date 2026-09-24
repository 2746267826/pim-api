using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Pim.Module.Files.Providers;

/// <summary>
/// Microsoft Graph 原始 HTTP 客户端（device-code 授权 + delta 读取）。
/// 错误统一抛 OneDriveGraphException；429 附带 Retry-After。请求/响应均不落日志。
/// </summary>
public sealed class OneDriveGraphClient : IOneDriveGraphClient
{
    /// <summary>
    /// 本客户端使用的**命名** HttpClient。注册方必须用这个名字配置超时等策略，
    /// 因为 <see cref="OneDriveGraphClient"/> 通过 <c>IHttpClientFactory.CreateClient(Name)</c>
    /// 取客户端，而不是构造函数注入 <c>HttpClient</c>。
    /// </summary>
    public const string HttpClientName = "onedrive-graph";
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _tenant;

    public OneDriveGraphClient(IHttpClientFactory httpClientFactory, IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _tenant = configuration?["Files:OneDrive:Tenant"] is { Length: > 0 } tenant
            ? tenant
            : OneDriveAuth.DefaultTenant;
    }

    public async Task<OneDriveDeviceCodeStart> RequestDeviceCodeAsync(string clientId, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = OneDriveAuth.Scopes,
        });
        using var response = await Http.PostAsync(TokenEndpoint("devicecode"), content, ct);
        var json = await ReadJsonAsync(response, ct);

        return new OneDriveDeviceCodeStart(
            ReadRequiredString(json, "device_code"),
            ReadRequiredString(json, "user_code"),
            ReadString(json, "verification_uri", "https://www.microsoft.com/link"),
            ReadInt(json, "expires_in", 900));
    }

    public async Task<OneDriveTokenResult> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
        });
        using var response = await Http.PostAsync(TokenEndpoint("token"), content, ct);
        var json = await ReadJsonAsync(response, ct);

        return new OneDriveTokenResult(
            ReadRequiredString(json, "access_token"),
            ReadNullableString(json, "refresh_token"),
            ReadInt(json, "expires_in", 3600),
            ReadNullableString(json, "scope"));
    }

    public async Task<OneDriveTokenResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = OneDriveAuth.Scopes,
        });
        using var response = await Http.PostAsync(TokenEndpoint("token"), content, ct);
        var json = await ReadJsonAsync(response, ct);

        return new OneDriveTokenResult(
            ReadRequiredString(json, "access_token"),
            ReadNullableString(json, "refresh_token"),
            ReadInt(json, "expires_in", 3600),
            ReadNullableString(json, "scope"));
    }

    public async Task<OneDriveDriveInfo> GetDriveAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await GetGraphJsonAsync($"{GraphBaseUrl}/me/drive", accessToken, ct);
        return new OneDriveDriveInfo(
            ReadRequiredString(json, "id"),
            ReadString(json, "driveType", "personal"),
            json.TryGetProperty("quota", out var quota) ? ReadNullableLong(quota, "used") : null,
            json.TryGetProperty("quota", out var quota2) ? ReadNullableLong(quota2, "total") : null);
    }

    public async Task<OneDriveAccountInfo> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await GetGraphJsonAsync($"{GraphBaseUrl}/me", accessToken, ct);
        return new OneDriveAccountInfo(
            ReadString(json, "id", ""),
            ReadNullableString(json, "displayName"));
    }

    public async Task<OneDriveDeltaPage> GetDeltaPageAsync(string accessToken, string url, CancellationToken ct = default)
    {
        var json = await GetGraphJsonAsync(ResolveGraphUrl(url), accessToken, ct);

        var items = new List<OneDriveDeltaItem>();
        if (json.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray())
            {
                items.Add(ReadDeltaItem(entry));
            }
        }

        return new OneDriveDeltaPage(
            items,
            ReadNullableString(json, "@odata.nextLink"),
            ReadNullableString(json, "@odata.deltaLink"));
    }

    /// <summary>
    /// 取条目的预授权下载直链（`@microsoft.graph.downloadUrl`）。
    ///
    /// **请求形状**（issue #342）：必须用**不带 `$select`** 的完整条目请求。
    /// 曾用的 `?$select=id,@microsoft.graph.downloadUrl` 看似是官方文档示例，
    /// 但对该账号（OneDrive 个人版）实测会被微软**静默丢弃注解**——HTTP 200、
    /// 响应里只有 `@odata.context / @odata.etag / id`，于是下游抛「暂未返回下载直链」
    /// 并被兜底映射成 400，所有文件都下不了。实测：去掉 `id` 或去掉整个 `$select` 均正常，
    /// 普通属性（如 `webUrl`）与 `id` 同选则不受影响。
    ///
    /// **兜底**：注解仍缺失时，改问 `/content` 并**不跟随跳转**、只取 `Location` 头。
    /// 这样依然拿的是微软预授权地址（零字节搬运，内容不经过 PIM 服务器），
    /// 与「内容/链接一律微软直连」的硬约束一致。
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        var escaped = Uri.EscapeDataString(itemId);
        var json = await GetGraphJsonAsync($"{GraphBaseUrl}/drive/items/{escaped}", accessToken, ct);
        var annotation = ReadNullableString(json, "@microsoft.graph.downloadUrl");
        if (!string.IsNullOrEmpty(annotation))
        {
            return annotation;
        }

        // 目录没有 /content：探测它只会拿到 404，进而被 RemoteItemExistsAsync 误判成
        // 「文件已从 OneDrive 删除」，把删除/恢复流程搞乱（删除目录时会走这里）。
        // 因此只在确认是**文件**（带 file facet）时才做兜底。
        var isFile = json.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object;
        if (!isFile)
        {
            return null;
        }

        return await GetContentLocationAsync(accessToken, escaped, ct);
    }

    /// <summary>
    /// 向 `/content` 发起请求但**不跟随 302**，只取 `Location`（预授权直链）。
    /// 用 <see cref="HttpCompletionOption.ResponseHeadersRead"/> 避免把响应体读进内存，
    /// 这是「内容不经服务器搬运」的关键：只读响应头，零字节下载。
    /// </summary>
    private async Task<string?> GetContentLocationAsync(string accessToken, string escapedItemId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{GraphBaseUrl}/drive/items/{escapedItemId}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Found)
        {
            // 复用既有错误映射（404/429 透传、其余 502），让上层的归属/敏感路径闸门语义不变
            throw CreateGraphException(response, string.Empty);
        }

        return response.Headers.Location?.ToString();
    }

    public async Task<string?> GetThumbnailUrlAsync(string accessToken, string itemId, string size, CancellationToken ct = default)
    {
        try
        {
            var json = await GetGraphJsonAsync(
                $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}/thumbnails/0/{Uri.EscapeDataString(size)}",
                accessToken, ct);
            return ReadNullableString(json, "url");
        }
        catch (OneDriveGraphException exception) when (exception.StatusCode is 404 or 400)
        {
            // 该类型不支持缩略图属正常情况
            return null;
        }
    }

    public async Task<string?> GetPreviewUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}/preview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        var json = await ReadJsonAsync(response, ct);
        return ReadNullableString(json, "getUrl");
    }

    public async Task<OneDriveSmallContent?> DownloadSmallAsync(string accessToken, string itemId, long maxBytes, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // ResponseHeadersRead：默认的 ResponseContentRead 会在返回前把整个响应体缓冲进内存，
        // 于是「上限检查」形同虚设——没有 Content-Length 的 chunked 响应可以先把内存吃光
        // 再被拒绝（复审 I-9）。改为拿到响应头就返回，边读边计数。
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw CreateGraphException(response, body);
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is { } length && length > maxBytes)
        {
            throw new OneDriveContentTooLargeException(length);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new OneDriveContentTooLargeException(buffer.Length + read);
            }

            buffer.Write(chunk, 0, read);
        }

        return new OneDriveSmallContent(buffer.ToArray(), response.Content.Headers.ContentType?.ToString());
    }

    public async Task PutSmallContentAsync(string accessToken, string itemId, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw CreateGraphException(response, body);
        }
    }

    public async Task<string> PatchItemAsync(string accessToken, string itemId, string? newName, string? newParentId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (newName is not null) body["name"] = newName;
        if (newParentId is not null)
        {
            body["parentReference"] = new Dictionary<string, object> { ["id"] = newParentId };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        var json = await ReadJsonAsync(response, ct);
        return ReadRequiredString(json, "id");
    }

    public async Task DeleteItemAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw CreateGraphException(response, body);
        }
    }

    public async Task<string> PutNewFileByPathAsync(string accessToken, string itemPath, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        var normalized = itemPath.TrimStart('/');
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{GraphBaseUrl}/drive/root:/{Uri.EscapeDataString(normalized)}:/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await Http.SendAsync(request, ct);
        var json = await ReadJsonAsync(response, ct);
        return ReadRequiredString(json, "id");
    }

    public async Task<string?> GetItemWebUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        var json = await GetGraphJsonAsync(
            $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}?$select=id,webUrl",
            accessToken, ct);
        return ReadNullableString(json, "webUrl");
    }

    private HttpClient Http => _httpClientFactory.CreateClient(HttpClientName);

    private string TokenEndpoint(string segment)
        => $"https://login.microsoftonline.com/{Uri.EscapeDataString(_tenant)}/oauth2/v2.0/{segment}";

    /// <summary>
    /// 只允许把 Bearer token 发往 Microsoft Graph 自己的主机。
    ///
    /// <c>@odata.nextLink</c> / <c>@odata.deltaLink</c> 是**服务器返回值**，且 deltaLink 会落库、
    /// 下轮同步再从库里读出来用。若不加校验，任何能影响该字段的路径（被篡改的响应、
    /// 被写入的游标、恶意测试替身）都能让后续请求把用户 token 带给任意主机。
    /// 这里对绝对 URL 做主机白名单校验，相对路径仍按 Graph 基址拼接。
    /// </summary>
    internal static string ResolveGraphUrl(string url)
    {
        // 相对形式（含只有 query 的 "?$deltatoken=..."）按 Graph 基址拼接。
        // 注意 Uri.TryCreate 会把 "?x=1" 解析成绝对 URI（无主机），必须显式识别这种形态。
        if (string.IsNullOrEmpty(url) || url.StartsWith('/') || url.StartsWith('?'))
        {
            return GraphBaseUrl + (url.StartsWith('/') ? url : "/" + url);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute) || string.IsNullOrEmpty(absolute.Host))
        {
            return GraphBaseUrl + "/" + url;
        }

        if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !AllowedGraphHosts.Contains(absolute.Host))
        {
            throw new OneDriveGraphException(
                0, null, $"Graph URL 主机不被允许：{absolute.Host}（可能被篡改的同步游标）");
        }

        return absolute.ToString();
    }

    /// <summary>允许携带 token 的目标主机（Graph 全球版与个人版内容域）。</summary>
    private static readonly HashSet<string> AllowedGraphHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "graph.microsoft.com",
        "login.microsoftonline.com",
    };

    private async Task<JsonElement> GetGraphJsonAsync(string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, ct);
        return await ReadJsonAsync(response, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateGraphException(response, body);
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new OneDriveGraphException(
                (int)response.StatusCode,
                null,
                $"Graph 响应不是有效 JSON：{exception.Message}");
        }
    }

    private static OneDriveGraphException CreateGraphException(HttpResponseMessage response, string body)
    {
        int? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            retryAfter = Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        }
        else if (response.Headers.TryGetValues("Retry-After", out var values)
                 && int.TryParse(values.FirstOrDefault(), out var parsed))
        {
            retryAfter = parsed;
        }

        string detail = body.Length > 300 ? body[..300] : body;
        return new OneDriveGraphException(
            (int)response.StatusCode,
            retryAfter,
            $"Graph {(int)response.StatusCode}：{detail}");
    }

    private static OneDriveDeltaItem ReadDeltaItem(JsonElement entry)
    {
        var isRemoved = entry.TryGetProperty("@removed", out _);
        var isFolder = entry.TryGetProperty("folder", out _) || entry.TryGetProperty("root", out _);
        string? parentPath = null;
        string? parentId = null;
        if (entry.TryGetProperty("parentReference", out var parent)
            && parent.ValueKind == JsonValueKind.Object)
        {
            parentId = ReadNullableString(parent, "id");
            parentPath = ReadNullableString(parent, "path");
        }

        return new OneDriveDeltaItem(
            ReadRequiredString(entry, "id"),
            parentId,
            parentPath,
            ReadString(entry, "name", ""),
            isFolder,
            isRemoved,
            ReadNullableLong(entry, "size"),
            entry.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object
                ? ReadNullableString(file, "mimeType")
                : null,
            ReadNullableString(entry, "ctag"),
            ParseTimestamp(ReadNullableString(entry, "lastModifiedDateTime")));
    }

    private static DateTimeOffset ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private static string ReadRequiredString(JsonElement json, string propertyName)
    {
        if (json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        throw new OneDriveGraphException(0, null, $"Graph 响应缺少字段 {propertyName}");
    }

    private static string ReadString(JsonElement json, string propertyName, string fallback)
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;

    private static string? ReadNullableString(JsonElement json, string propertyName)
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadNullableLong(JsonElement json, string propertyName)
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var value)
            ? value
            : null;

    private static int ReadInt(JsonElement json, string propertyName, int fallback)
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            ? value
            : fallback;
}
