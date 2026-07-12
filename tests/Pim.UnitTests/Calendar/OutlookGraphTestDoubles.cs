using System.Net;
using System.Text;
using System.Text.Json;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Services;

namespace Pim.UnitTests.Calendar;

internal sealed class FakeMicrosoftGraphClient : IMicrosoftGraphClient
{
    public DeviceCodeResult DeviceCode { get; set; } = new(
        "device-code",
        "USER-CODE",
        "https://www.microsoft.com/link",
        "Open link.",
        900);

    public TokenResult Token { get; set; } = new(
        "access-token",
        "refresh-token",
        3600,
        "Calendars.ReadWrite offline_access");

    public Queue<GraphDeltaPage> DeltaPages { get; } = new();

    public List<PatchRequest> PatchRequests { get; } = [];

    public Task<DeviceCodeResult> RequestDeviceCodeAsync(
        string tenant,
        string clientId,
        string scopes,
        CancellationToken ct)
        => Task.FromResult(DeviceCode);

    public Task<TokenResult> PollDeviceCodeAsync(
        string tenant,
        string clientId,
        string deviceCode,
        CancellationToken ct)
        => Task.FromResult(Token);

    public Task<TokenResult> RefreshAsync(
        string tenant,
        string clientId,
        string refreshToken,
        string scopes,
        CancellationToken ct)
        => Task.FromResult(Token);

    public Task<GraphDeltaPage> GetDeltaPageAsync(
        string accessToken,
        string url,
        CancellationToken ct)
        => Task.FromResult(DeltaPages.Count == 0
            ? new GraphDeltaPage([], null, "delta-link")
            : DeltaPages.Dequeue());

    public Task<GraphEvent> PatchEventAsync(
        string accessToken,
        string eventId,
        string changeKey,
        object patch,
        CancellationToken ct)
    {
        PatchRequests.Add(new PatchRequest(eventId, changeKey, JsonSerializer.Serialize(patch)));
        return Task.FromResult(GraphEventFactory.Create(eventId, "Patched", changeKey: "patched-change"));
    }

    public sealed record PatchRequest(string EventId, string ChangeKey, string Body);
}

internal static class GraphEventFactory
{
    public static GraphEvent Create(
        string id,
        string subject,
        string? location = null,
        string changeKey = "change-key")
        => new(
            id,
            subject,
            "Preview",
            new GraphDateTimeTimeZone("2026-07-08T09:00:00Z", "UTC"),
            new GraphDateTimeTimeZone("2026-07-08T10:00:00Z", "UTC"),
            "2026-07-08T01:00:00Z",
            "ical-" + id,
            changeKey,
            "etag-" + id,
            location ?? "Room A",
            null);
}

internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public string Unprotect(string protectedValue)
        => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["protected:".Length..]));
}

internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _scripts = new();

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<HttpRequestMessage> OriginalRequests { get; } = [];

    public void Enqueue(HttpStatusCode statusCode, string? body = null, string? retryAfter = null)
    {
        _scripts.Enqueue(request =>
        {
            var response = new HttpResponseMessage(statusCode);
            if (body is not null)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (retryAfter is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        });
    }

    public void EnqueueException(HttpRequestException exception)
    {
        _scripts.Enqueue(_ => throw exception);
    }

    public void EnqueueTimeout()
    {
        _scripts.Enqueue(_ => throw new OperationCanceledException());
    }

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _scripts.Enqueue(handler);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);

        var script = _scripts.Count > 0 ? _scripts.Dequeue() : null;
        if (script is null)
            throw new InvalidOperationException(
                $"No response queued for {request.Method} {request.RequestUri}");

        OriginalRequests.Add(request);
        Requests.Add(SnapshotRequest(request));

        try
        {
            return Task.FromResult(script(request));
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }

    private static HttpRequestMessage SnapshotRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version
        };

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (original.Content is not null)
        {
            var body = original.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8, original.Content.Headers.ContentType?.MediaType ?? "application/json");

            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
        => new(_handler, disposeHandler: false);
}

internal sealed class FakeOutlookAccessTokenProvider : IOutlookAccessTokenProvider
{
    public string Token { get; set; } = "test-access-token";
    public int CallCount { get; private set; }
    public bool LastForceRefresh { get; private set; }

    public Task<string> AcquireAccessTokenAsync(Guid connectionId, bool forceRefresh, CancellationToken ct)
    {
        CallCount++;
        LastForceRefresh = forceRefresh;
        return Task.FromResult(Token);
    }
}

internal sealed class StubTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNowValue { get; set; } = DateTimeOffset.UtcNow;
    public List<TimeSpan> RequestedDelays { get; } = [];

    public override DateTimeOffset GetUtcNow() => UtcNowValue;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            RequestedDelays.Add(dueTime);

        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            _ = Task.Run(() => callback(state));

        return new StubTimer();
    }
}

internal sealed class StubTimer : ITimer
{
    public bool Change(TimeSpan dueTime, TimeSpan period) => true;
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class NonSeekableReadStream : Stream
{
    private readonly Stream _inner;
    public NonSeekableReadStream(Stream inner) => _inner = inner;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
