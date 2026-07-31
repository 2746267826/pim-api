using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pim.Module.Calendar.Services;

public sealed record GraphPage(IReadOnlyList<JsonElement> Items, string? NextLink);

public sealed record GraphBinaryContent(Stream Content, string ContentType, string? FileName)
{
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "attachment";

        var sanitized = new string(fileName
            .Where(ch => ch >= 0x20 && ch != '"' && ch != '/' && ch != '\\' && ch != ';')
            .ToArray()).Trim();

        return string.IsNullOrEmpty(sanitized) ? "attachment" : sanitized;
    }
}

public sealed class GraphRequestException : HttpRequestException
{
    public GraphRequestException(HttpStatusCode statusCode, string message)
        : base(message, null, statusCode) { }
}

public sealed class GraphCalendarClient
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0/";
    private const int MaxReadAttempts = 3;
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly string CalendarEventSelect =
        "id,subject,body,start,end,location,isAllDay,type,seriesMasterId,recurrence,iCalUId,changeKey,originalStartTimeZone,originalEndTimeZone" +
        ",importance,sensitivity,showAs,categories,isReminderOn,reminderMinutesBeforeStart" +
        ",organizer,attendees,isOnlineMeeting,onlineMeetingProvider,onlineMeeting,webLink" +
        ",responseRequested,allowNewTimeProposals,hideAttendees,hasAttachments";

    private readonly HttpClient _httpClient;
    private readonly IOutlookAccessTokenProvider _tokenProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _bodyReadTimeout;

    public GraphCalendarClient(
        IHttpClientFactory httpClientFactory,
        IOutlookAccessTokenProvider tokenProvider,
        TimeProvider timeProvider,
        TimeSpan? bodyReadTimeout = null)
    {
        _httpClient = httpClientFactory.CreateClient("outlook");
        _tokenProvider = tokenProvider;
        _timeProvider = timeProvider;
        _bodyReadTimeout = bodyReadTimeout ?? PerAttemptTimeout;
    }

    public async Task<JsonElement> GetMeAsync(Guid connectionId, CancellationToken ct)
    {
        var result = await ReadSingleAsync(connectionId, token => BuildGet(
            "me?$select=id,displayName,userPrincipalName", token), ct);
        return result!.Value;
    }

    public IAsyncEnumerable<GraphPage> GetCalendarGroupsAsync(Guid connectionId, CancellationToken ct)
        => GetPagesAsync(connectionId,
            $"{GraphBase}me/calendarGroups?$select=id,name", ct);

    public IAsyncEnumerable<GraphPage> GetGroupCalendarsAsync(Guid connectionId, string groupId, CancellationToken ct)
        => GetPagesAsync(connectionId,
            $"{GraphBase}me/calendarGroups/{EscapeDataString(groupId)}/calendars?$select=id,name,color,owner,isDefaultCalendar,canEdit,canViewPrivateItems",
            ct);

    public IAsyncEnumerable<GraphPage> GetCalendarsAsync(Guid connectionId, CancellationToken ct)
        => GetPagesAsync(connectionId,
            $"{GraphBase}me/calendars?$select=id,name,color,owner,isDefaultCalendar,canEdit,canViewPrivateItems",
            ct);

    public IAsyncEnumerable<GraphPage> GetCalendarViewAsync(
        Guid connectionId, string calendarId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
        => GetPagesAsync(connectionId,
            $"{GraphBase}me/calendars/{EscapeDataString(calendarId)}/calendarView" +
            $"?startDateTime={Uri.EscapeDataString(FormatUtc(start))}" +
            $"&endDateTime={Uri.EscapeDataString(FormatUtc(end))}" +
            $"&$select={CalendarEventSelect}", ct);

    private static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "Z";

    public IAsyncEnumerable<GraphPage> GetEventsAsync(Guid connectionId, string calendarId, CancellationToken ct)
        => GetPagesAsync(connectionId,
            $"{GraphBase}me/calendars/{EscapeDataString(calendarId)}/events?$select={CalendarEventSelect}", ct);

    public Task<JsonElement?> GetEventAsync(
        Guid connectionId, string calendarId, string eventId, CancellationToken ct)
        => ReadSingleAsync(connectionId, token => BuildGet(
            $"me/calendars/{EscapeDataString(calendarId)}/events/{EscapeDataString(eventId)}?$select={CalendarEventSelect}",
            token), ct, allowNull: true);

    public IAsyncEnumerable<GraphPage> GetEventAttachmentsAsync(
        Guid connectionId, string calendarId, string eventId, CancellationToken ct)
        => GetPagesAsync(connectionId,
            $"{GraphBase}me/calendars/{EscapeDataString(calendarId)}/events/{EscapeDataString(eventId)}" +
            "/attachments?$select=id,name,contentType,size,isInline,@odata.type", ct);

    public async Task<GraphBinaryContent> DownloadEventAttachmentAsync(
        Guid connectionId, string calendarId, string eventId, string attachmentId, CancellationToken ct)
    {
        var token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, false, ct);
        var replayed401 = false;

        for (var attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            using var request = BuildGet(
                $"me/calendars/{EscapeDataString(calendarId)}/events/{EscapeDataString(eventId)}" +
                $"/attachments/{EscapeDataString(attachmentId)}/$value", token);
            HttpResponseMessage? response = null;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerAttemptTimeout);

                try
                {
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt < MaxReadAttempts)
                    {
                        await BackoffAsync(null, attempt, ct);
                        continue;
                    }
                    throw;
                }
                catch (HttpRequestException)
                {
                    if (attempt < MaxReadAttempts)
                    {
                        await BackoffAsync(null, attempt, ct);
                        continue;
                    }
                    throw;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    response = null;
                    if (replayed401)
                        throw new OutlookReauthenticationRequiredException("graph-unauthorized");
                    if (attempt == MaxReadAttempts)
                        throw new GraphRequestException(
                            HttpStatusCode.Unauthorized,
                            "Read request failed after retries");
                    replayed401 = true;
                    token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, true, ct);
                    continue;
                }

                if (IsRetryableStatusCode(response.StatusCode))
                {
                    var retryAfter = response.Headers.RetryAfter;
                    var lastStatus = response.StatusCode;
                    response.Dispose();
                    response = null;
                    if (attempt < MaxReadAttempts)
                    {
                        await BackoffAsync(retryAfter, attempt, ct);
                        continue;
                    }
                    throw new GraphRequestException(lastStatus, "Read request failed after retries");
                }

                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    var fileName = GraphBinaryContent.SanitizeFileName(ParseContentDispositionFileName(response));
                    var content = new GraphBinaryContent(
                        new ResponseDisposingStream(stream, response, _bodyReadTimeout), contentType, fileName);
                    response = null;
                    return content;
                }

                var status = response.StatusCode;
                response.Dispose();
                response = null;
                throw new GraphRequestException(status, "Read request failed");
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    private static string? ParseContentDispositionFileName(HttpResponseMessage response)
    {
        if (!response.Content.Headers.TryGetValues("Content-Disposition", out var values))
            return null;

        var raw = string.Join(";", values);
        foreach (var part in raw.Split(';'))
        {
            var trimmed = part.Trim();
            if (!trimmed.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = trimmed["filename=".Length..].Trim().Trim('"');
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private sealed class ResponseDisposingStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly TimeSpan _bodyReadTimeout;

        public ResponseDisposingStream(Stream inner, HttpResponseMessage response, TimeSpan bodyReadTimeout)
        {
            _inner = inner;
            _response = response;
            _bodyReadTimeout = bodyReadTimeout;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
            => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin)
            => _inner.Seek(offset, origin);

        public override void SetLength(long value)
            => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
            => _inner.Write(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_bodyReadTimeout);

            try
            {
                return await _inner.ReadAsync(buffer, timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The response body read timed out.", ex);
            }
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _response.Dispose();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public Task<JsonElement> CreateEventAsync(
        Guid connectionId, string calendarId,
        IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
        => WriteAsync(connectionId, token => BuildPost(
            $"me/calendars/{EscapeDataString(calendarId)}/events", token, payload), ct);

    public Task<JsonElement> UpdateEventAsync(
        Guid connectionId, string calendarId, string eventId, string etag,
        IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
        => WriteAsync(connectionId, token => BuildPatch(
            $"me/calendars/{EscapeDataString(calendarId)}/events/{EscapeDataString(eventId)}",
            token, payload, etag), ct);

    public Task DeleteEventAsync(
        Guid connectionId, string calendarId, string eventId, string etag, CancellationToken ct)
        => DeleteCoreAsync(connectionId, token => BuildDelete(
            $"me/calendars/{EscapeDataString(calendarId)}/events/{EscapeDataString(eventId)}",
            token, etag), ct);

    public static bool IsAllowedNextLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!uri.IsDefaultPort)
            return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (!string.IsNullOrEmpty(uri.Fragment))
            return false;

        if (HasRawDotSegments(uri))
            return false;

        var path = uri.AbsolutePath;
        if (!path.StartsWith("/v1.0/", StringComparison.Ordinal))
            return false;

        var remaining = path.AsSpan("/v1.0/".Length);
        while (remaining.Length > 0 && remaining[^1] == '/')
            remaining = remaining[..^1];
        if (remaining.Length == 0)
            return false;

        return true;
    }

    private static bool HasRawDotSegments(Uri uri)
    {
        var original = uri.OriginalString.AsSpan();
        var schemeEnd = original.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;

        var afterScheme = original[(schemeEnd + 3)..];
        var slashIdx = afterScheme.IndexOf('/');
        if (slashIdx < 0) return false;

        var rawPath = afterScheme[slashIdx..];
        var qIdx = rawPath.IndexOf('?');
        var fIdx = rawPath.IndexOf('#');
        var pathLen = rawPath.Length;
        if (qIdx >= 0 && qIdx < pathLen) pathLen = qIdx;
        if (fIdx >= 0 && fIdx < pathLen) pathLen = fIdx;
        rawPath = rawPath[..pathLen];

        var segments = rawPath.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var decoded = segment;
            for (var passes = 0; passes < 2; passes++)
            {
                if (decoded is "." or "..")
                    return true;
                decoded = Uri.UnescapeDataString(decoded);
            }
            if (decoded is "." or "..")
                return true;
        }
        return false;
    }

    private static string EscapeDataString(string value)
        => Uri.EscapeDataString(value);

    private static HttpRequestMessage BuildGet(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GraphBase + path);
        AddAuthAndPrefer(request, token);
        return request;
    }

    private static HttpRequestMessage BuildPost(string path, string token, IReadOnlyDictionary<string, object?> payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GraphBase + path)
        {
            Content = SerializePayload(payload)
        };
        AddAuthAndPrefer(request, token);
        return request;
    }

    private static HttpRequestMessage BuildPatch(string path, string token, IReadOnlyDictionary<string, object?> payload, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, GraphBase + path)
        {
            Content = SerializePayload(payload)
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        AddAuthAndPrefer(request, token);
        return request;
    }

    private static HttpRequestMessage BuildDelete(string path, string token, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, GraphBase + path);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        AddAuthAndPrefer(request, token);
        return request;
    }

    private static void AddAuthAndPrefer(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
    }

    private static StringContent SerializePayload(IReadOnlyDictionary<string, object?> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }

    private async Task<JsonElement?> ReadSingleAsync(
        Guid connectionId,
        Func<string, HttpRequestMessage> buildRequest,
        CancellationToken ct,
        bool allowNull = false)
    {
        var token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, false, ct);
        var replayed401 = false;

        for (var attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            using var request = buildRequest(token);
            HttpResponseMessage? response = null;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerAttemptTimeout);

                try
                {
                    response = await _httpClient.SendAsync(request, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt < MaxReadAttempts)
                    {
                        await BackoffAsync(null, attempt, ct);
                        continue;
                    }
                    throw;
                }
                catch (HttpRequestException)
                {
                    if (attempt < MaxReadAttempts)
                    {
                        await BackoffAsync(null, attempt, ct);
                        continue;
                    }
                    throw;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    response = null;
                    if (replayed401)
                        throw new OutlookReauthenticationRequiredException("graph-unauthorized");
                    if (attempt == MaxReadAttempts)
                        throw new GraphRequestException(
                            HttpStatusCode.Unauthorized,
                            "Read request failed after retries");
                    replayed401 = true;
                    token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, true, ct);
                    continue;
                }

                if (IsRetryableStatusCode(response.StatusCode))
                {
                    var retryAfter = response.Headers.RetryAfter;
                    var lastStatus = response.StatusCode;
                    response.Dispose();
                    response = null;
                    if (attempt < MaxReadAttempts)
                    {
                        await BackoffAsync(retryAfter, attempt, ct);
                        continue;
                    }
                    throw new GraphRequestException(lastStatus, "Read request failed after retries");
                }

                if (allowNull && response.StatusCode == HttpStatusCode.NotFound)
                {
                    response.Dispose();
                    return null;
                }

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    return doc.RootElement.Clone();
                }

                var status = response.StatusCode;
                response.Dispose();
                throw new GraphRequestException(status, "Read request failed");
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    private async IAsyncEnumerable<GraphPage> GetPagesAsync(
        Guid connectionId,
        string initialUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var nextLink = (string?)null;

        while (true)
        {
            string url;
            if (nextLink is not null)
            {
                if (!IsAllowedNextLink(nextLink))
                    throw new InvalidOperationException("Invalid nextLink rejected");
                url = nextLink;
            }
            else
            {
                url = initialUrl;
            }

            var json = await ReadSingleAsync(connectionId, token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthAndPrefer(request, token);
                return request;
            }, ct);

            if (json is null)
                yield break;

            var doc = json.GetValueOrDefault();
            var items = new List<JsonElement>();
            if (doc.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    items.Add(item.Clone());
            }

            var next = (string?)null;
            if (doc.TryGetProperty("@odata.nextLink", out var nextProp) && nextProp.ValueKind == JsonValueKind.String)
                next = nextProp.GetString();

            nextLink = next;
            yield return new GraphPage(items, nextLink);

            if (nextLink is null)
                yield break;
        }
    }

    private async Task<JsonElement> WriteAsync(
        Guid connectionId,
        Func<string, HttpRequestMessage> buildRequest,
        CancellationToken ct)
    {
        var token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, false, ct);
        var replayed = false;

        while (true)
        {
            using var request = buildRequest(token);
            HttpResponseMessage? response = null;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerAttemptTimeout);

                response = await _httpClient.SendAsync(request, timeoutCts.Token);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    if (replayed)
                        throw new OutlookReauthenticationRequiredException("graph-unauthorized");
                    replayed = true;
                    token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, true, ct);
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length == 0)
                        return default;
                    using var doc = JsonDocument.Parse(bytes);
                    return doc.RootElement.Clone();
                }

                var status = response.StatusCode;
                response.Dispose();
                throw new GraphRequestException(status, "Write request failed");
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private async Task DeleteCoreAsync(
        Guid connectionId,
        Func<string, HttpRequestMessage> buildRequest,
        CancellationToken ct)
    {
        var token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, false, ct);
        var replayed = false;

        while (true)
        {
            using var request = buildRequest(token);
            HttpResponseMessage? response = null;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerAttemptTimeout);

                response = await _httpClient.SendAsync(request, timeoutCts.Token);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    if (replayed)
                        throw new OutlookReauthenticationRequiredException("graph-unauthorized");
                    replayed = true;
                    token = await _tokenProvider.AcquireAccessTokenAsync(connectionId, true, ct);
                    continue;
                }

                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                    return;

                var status = response.StatusCode;
                response.Dispose();
                throw new GraphRequestException(status, "Delete request failed");
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 408 || code == 429 || code >= 500;
    }

    private async Task BackoffAsync(RetryConditionHeaderValue? retryAfter, int attempt, CancellationToken ct)
    {
        TimeSpan delay;

        if (retryAfter?.Delta is TimeSpan delta)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is DateTimeOffset date)
        {
            delay = date - _timeProvider.GetUtcNow();
        }
        else
        {
            delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        }

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        if (delay > MaxBackoff)
            delay = MaxBackoff;

        await Task.Delay(delay, _timeProvider, ct);
    }
}
