using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Pim.Module.Calendar.Services;

public sealed class MicrosoftGraphDeviceCodeClient : IMicrosoftGraphClient
{
    private const string Provider = "outlook";
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MicrosoftGraphDeviceCodeClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<DeviceCodeResult> RequestDeviceCodeAsync(
        string tenant,
        string clientId,
        string scopes,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = scopes
        });
        var response = await Http.PostAsync(TokenEndpoint(tenant, "devicecode"), content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);

        return new DeviceCodeResult(
            ReadString(json, "device_code"),
            ReadString(json, "user_code"),
            ReadString(json, "verification_uri", "https://www.microsoft.com/link"),
            ReadString(json, "message"),
            ReadInt(json, "expires_in", 900));
    }

    public async Task<TokenResult> PollDeviceCodeAsync(
        string tenant,
        string clientId,
        string deviceCode,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = deviceCode
        });
        return await RequestTokenAsync(tenant, content, ct);
    }

    public async Task<TokenResult> RefreshAsync(
        string tenant,
        string clientId,
        string refreshToken,
        string scopes,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = scopes
        });
        return await RequestTokenAsync(tenant, content, ct);
    }

    public async Task<GraphDeltaPage> GetDeltaPageAsync(
        string accessToken,
        string url,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, NormalizeGraphUrl(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);

        var events = new List<GraphEvent>();
        if (json.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                events.Add(ReadGraphEvent(item));
            }
        }

        return new GraphDeltaPage(
            events,
            ReadNullableString(json, "@odata.nextLink"),
            ReadNullableString(json, "@odata.deltaLink"));
    }

    public async Task<GraphEvent> PatchEventAsync(
        string accessToken,
        string eventId,
        string changeKey,
        object patch,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{GraphBaseUrl}/me/events/{Uri.EscapeDataString(eventId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("If-Match", changeKey);
        request.Content = new StringContent(JsonSerializer.Serialize(patch, JsonOptions), Encoding.UTF8, "application/json");
        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return ReadGraphEvent(json);
    }

    private HttpClient Http => _httpClientFactory.CreateClient(Provider);

    private async Task<TokenResult> RequestTokenAsync(
        string tenant,
        HttpContent content,
        CancellationToken ct)
    {
        var response = await Http.PostAsync(TokenEndpoint(tenant, "token"), content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return new TokenResult(
            ReadString(json, "access_token"),
            ReadString(json, "refresh_token"),
            ReadInt(json, "expires_in", 3600),
            ReadString(json, "scope"));
    }

    private static string TokenEndpoint(string tenant, string segment)
        => $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/{segment}";

    private static string NormalizeGraphUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out _)
            ? url
            : GraphBaseUrl + (url.StartsWith('/') ? url : "/" + url);

    private static GraphEvent ReadGraphEvent(JsonElement item)
    {
        var location = item.TryGetProperty("location", out var locationElement)
            && locationElement.ValueKind == JsonValueKind.Object
            ? ReadNullableString(locationElement, "displayName")
            : null;

        return new GraphEvent(
            ReadString(item, "id"),
            ReadString(item, "subject"),
            ReadNullableString(item, "bodyPreview"),
            ReadDateTimeTimeZone(item, "start"),
            ReadDateTimeTimeZone(item, "end"),
            ReadNullableString(item, "lastModifiedDateTime"),
            ReadNullableString(item, "iCalUId"),
            ReadNullableString(item, "changeKey"),
            ReadNullableString(item, "@odata.etag"),
            location,
            ReadNullableString(item, "webLink"));
    }

    private static GraphDateTimeTimeZone ReadDateTimeTimeZone(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new GraphDateTimeTimeZone(DateTimeOffset.MinValue.ToString("o"), "UTC");
        }

        return new GraphDateTimeTimeZone(
            ReadString(value, "dateTime", DateTimeOffset.MinValue.ToString("o")),
            ReadNullableString(value, "timeZone"));
    }

    private static string ReadString(JsonElement json, string propertyName, string fallback = "")
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;

    private static string? ReadNullableString(JsonElement json, string propertyName)
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int ReadInt(JsonElement json, string propertyName, int fallback)
        => json.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : fallback;
}
