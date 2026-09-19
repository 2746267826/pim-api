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
    private const string HttpClientName = "onedrive-graph";
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
        var json = await GetGraphJsonAsync(NormalizeGraphUrl(url), accessToken, ct);

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

    public async Task<string?> GetDownloadUrlAsync(string accessToken, string itemId, CancellationToken ct = default)
    {
        var json = await GetGraphJsonAsync(
            $"{GraphBaseUrl}/drive/items/{Uri.EscapeDataString(itemId)}?$select=id,@microsoft.graph.downloadUrl",
            accessToken, ct);
        return ReadNullableString(json, "@microsoft.graph.downloadUrl");
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
        using var response = await Http.SendAsync(request, ct);
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

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > maxBytes)
        {
            throw new OneDriveContentTooLargeException(bytes.Length);
        }

        return new OneDriveSmallContent(bytes, response.Content.Headers.ContentType?.ToString());
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

    private HttpClient Http => _httpClientFactory.CreateClient(HttpClientName);

    private string TokenEndpoint(string segment)
        => $"https://login.microsoftonline.com/{Uri.EscapeDataString(_tenant)}/oauth2/v2.0/{segment}";

    private static string NormalizeGraphUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out _)
            ? url
            : GraphBaseUrl + (url.StartsWith('/') ? url : "/" + url);

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
