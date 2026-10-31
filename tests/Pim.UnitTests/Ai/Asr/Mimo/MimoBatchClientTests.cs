using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Xunit;

namespace Pim.UnitTests.Ai.Asr.Mimo;

public sealed class MimoBatchClientTests
{
    [Fact]
    public async Task Upload_SendsMultipartBatchPurpose_AndMapsFileExpiry()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            new { id = "file-1", purpose = "batch", filename = "input.jsonl", expire_at = 1780038877L })));
        using var content = new MemoryStream("{\"custom_id\":\"a\"}"u8.ToArray());
        var file = await client.UploadJsonlAsync(content, content.Length, "input.jsonl", default);
        Assert.Equal("file-1", file.FileId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780038877), file.ExpireAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/files", request.Path);
        Assert.Contains("purpose", request.Body);
        Assert.Contains("batch", request.Body);
        Assert.Contains("input.jsonl", request.Body);
    }

    [Fact]
    public async Task Upload_RejectsOversizeBeforeAnyRequest()
    {
        var (client, handler) = Create((_, _) => throw new InvalidOperationException("should not send"));
        using var content = new MemoryStream([1]);
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            client.UploadJsonlAsync(content, MimoBatchDefaults.MaxJsonlBytes + 1, "input.jsonl", default));
        Assert.Equal(42203, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upload_RejectsUnsafeFilename()
    {
        var (client, handler) = Create((_, _) => throw new InvalidOperationException("should not send"));
        using var content = new MemoryStream("{}"u8.ToArray());
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            client.UploadJsonlAsync(content, content.Length, "../input.jsonl", default));
        Assert.Equal(42204, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upload_RejectsDeclaredLengthMismatchBeforeSending()
    {
        var (client, handler) = Create((_, _) => throw new InvalidOperationException("should not send"));
        using var content = new MemoryStream("{}"u8.ToArray());
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            client.UploadJsonlAsync(content, content.Length + 1, "input.jsonl", default));
        Assert.Equal(42210, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_SendsRequiredNameAndReturnsKnownHandle()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_1", "validating", 1711488800L))));
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "nightly", "24h");
        var known = Assert.IsType<MimoBatchCreateOutcome.Known>(outcome);
        Assert.Equal("batch_1", known.Batch.BatchId);
        Assert.Equal(MimoBatchStatusKind.Validating, known.Batch.Status);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("file-1", body.RootElement.GetProperty("input_file_id").GetString());
        Assert.Equal("/v1/chat/completions", body.RootElement.GetProperty("endpoint").GetString());
        Assert.Equal("nightly", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("24h", body.RootElement.GetProperty("completion_window").GetString());
    }

    [Fact]
    public async Task CreateTimeout_ReturnsUnknownWithoutRetry()
    {
        var (client, handler) = Create(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Json(HttpStatusCode.OK, BatchJson("late", "validating", null));
        }, createTimeout: TimeSpan.FromMilliseconds(50));
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "timed");
        var unknown = Assert.IsType<MimoBatchCreateOutcome.Unknown>(outcome);
        Assert.Equal("create_timeout", unknown.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateTransportFailure_ReturnsUnknownWithoutRetry()
    {
        var (client, handler) = Create((_, _) => throw new HttpRequestException("synthetic"));
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "transport");
        var unknown = Assert.IsType<MimoBatchCreateOutcome.Unknown>(outcome);
        Assert.Equal("create_transport_unknown", unknown.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateBadRequest_ThrowsInsteadOfPretendingUnknownSuccess()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest,
            new { error = new { message = "invalid" } })));
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            client.CreateBatchAsync("file-1", "/v1/chat/completions", "bad"));
        Assert.Equal(50205, error.ErrorCode);
    }

    [Fact]
    public async Task CreateMalformedSuccess_ReturnsUnknownWithoutRetry()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, new { })));
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "malformed");
        var unknown = Assert.IsType<MimoBatchCreateOutcome.Unknown>(outcome);
        Assert.Equal("create_response_unverified", unknown.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateNonJsonSuccess_ReturnsUnknownWithoutRetry()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        }));
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "malformed-json");
        var unknown = Assert.IsType<MimoBatchCreateOutcome.Unknown>(outcome);
        Assert.Equal("create_response_unverified", unknown.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateOversizedSuccess_ReturnsUnknownWithoutRetry()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 100), Encoding.UTF8, "application/json")
        }), maxResponseBytes: 16);
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "oversized");
        var unknown = Assert.IsType<MimoBatchCreateOutcome.Unknown>(outcome);
        Assert.Equal("create_response_unverified", unknown.Reason);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CreateUpstreamTransientStatus_ReturnsUnknownWithoutRetry(HttpStatusCode status)
    {
        var (client, handler) = Create((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "transient");
        var unknown = Assert.IsType<MimoBatchCreateOutcome.Unknown>(outcome);
        Assert.Equal("create_upstream_unavailable", unknown.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateCallerCancellationAfterSend_ReturnsUnknownWithoutRetry()
    {
        var (client, handler) = Create(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Json(HttpStatusCode.OK, BatchJson("late", "validating", null));
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var outcome = await client.CreateBatchAsync("file-1", "/v1/chat/completions", "cancelled",
            ct: cancellation.Token);
        var unknown = Assert.IsType<MimoBatchCreateOutcome.Unknown>(outcome);
        Assert.Equal("create_cancelled_unverified", unknown.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateCallerCancellationBeforeSend_PropagatesWithoutRequest()
    {
        var (client, handler) = Create((_, _) => throw new InvalidOperationException("should not send"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateBatchAsync("file-1", "/v1/chat/completions", "cancelled", ct: cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AudioBatchGate_DeniesByDefault_AndAllowsOnlyExplicitConfig()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_1", "validating", null))));
        var error = Assert.Throws<MimoBatchException>(() => client.EnsureAudioBatchAllowed(true));
        Assert.Equal(42212, error.ErrorCode);
        client.EnsureAudioBatchAllowed(false);
        Assert.Empty(handler.Requests);

        var (enabled, enabledHandler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_2", "validating", null))), audioBatchEnabled: true);
        var outcome = await enabled.CreateBatchAsync("file-1", "/v1/chat/completions", "audio", "24h",
            containsAudio: true, ct: default);
        Assert.IsType<MimoBatchCreateOutcome.Known>(outcome);
        Assert.Single(enabledHandler.Requests);
    }

    [Fact]
    public async Task Get_NormalizesKnownCaseAndPreservesRawStatus()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_1", "Completed", 1711488800L))));
        var batch = await client.GetBatchAsync("batch_1", default);
        Assert.Equal("Completed", batch.StatusRaw);
        Assert.Equal(MimoBatchStatusKind.Completed, batch.Status);
        Assert.True(batch.IsTerminal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1711488800), batch.ExpiresAt);
        Assert.Equal(2, batch.Total);
        Assert.Equal(1, batch.Completed);
    }

    [Fact]
    public async Task Get_MapsFileHandlesFromTheSameBoundedResponse()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_1", "completed", 1711488800L, "file-in", "file-out", "file-err"))));
        var batch = await client.GetBatchAsync("batch_1", default);
        Assert.Equal("file-in", batch.InputFileId);
        Assert.Equal("file-out", batch.OutputFileId);
        Assert.Equal("file-err", batch.ErrorFileId);
    }

    [Fact]
    public async Task Get_UnknownStatusStaysUnknown()
    {
        var (client, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_1", "mystery", null))));
        var batch = await client.GetBatchAsync("batch_1", default);
        Assert.Equal(MimoBatchStatusKind.Unknown, batch.Status);
        Assert.False(batch.IsTerminal);
    }

    [Fact]
    public async Task BaseAddressWithV1_DoesNotDuplicateApiPath()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_1", "validating", null))));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://batch.invalid/v1/") };
        var client = new MimoBatchClient(http);
        await client.GetBatchAsync("batch_1", default);
        Assert.Equal("/v1/batches/batch_1", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task Cancel_IsAsyncAndReportsCancelling()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            BatchJson("batch_1", "cancelling", null))));
        var batch = await client.CancelAsync("batch_1", default);
        Assert.Equal(MimoBatchStatusKind.Cancelling, batch.Status);
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
        Assert.EndsWith("/cancel", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task Delete_AllowsTerminalOnly()
    {
        var (client, handler) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        await client.DeleteAsync(new MimoBatchRef("batch_1", "completed", MimoBatchStatusKind.Completed,
            null, null, null, null), default);
        Assert.Equal(HttpMethod.Delete, Assert.Single(handler.Requests).Method);

        var (rejectedClient, rejectedHandler) = Create((_, _) => throw new InvalidOperationException("should not send"));
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            rejectedClient.DeleteAsync(new MimoBatchRef("batch_2", "in_progress", MimoBatchStatusKind.InProgress,
                null, null, null, null), default));
        Assert.Equal(40905, error.ErrorCode);
        Assert.Empty(rejectedHandler.Requests);
    }

    [Fact]
    public async Task Download_IsBounded()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"custom_id\":\"a\"}\n", Encoding.UTF8, "application/jsonl")
        }));
        var content = await client.DownloadOutputAsync("file-1", default);
        Assert.Contains("custom_id", content);

        var (smallClient, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 100), Encoding.UTF8)
        }), maxResponseBytes: 16);
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            smallClient.DownloadOutputAsync("file-1", default));
        Assert.Equal(41301, error.ErrorCode);
    }

    [Fact]
    public async Task Redirect_IsRejected()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.invalid/steal") }
        }));
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            client.GetBatchAsync("batch_1", default));
        Assert.Equal(50204, error.ErrorCode);
    }

    [Fact]
    public void Factory_DisablesAutoRedirect()
    {
        var handler = new HttpClientHandler();
        using var http = MimoBatchClient.CreateHttpClient(handler, new Uri("https://batch.invalid/"));
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void Factory_DisablesAutoRedirectThroughDelegatingHandler()
    {
        var inner = new HttpClientHandler();
        using var http = MimoBatchClient.CreateHttpClient(new PassThroughHandler(inner),
            new Uri("https://batch.invalid/"));
        Assert.False(inner.AllowAutoRedirect);
    }

    [Fact]
    public void Factory_RejectsUnknownHandler()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("unused"));
        Assert.Throws<MimoBatchException>(() =>
            MimoBatchClient.CreateHttpClient(handler, new Uri("https://batch.invalid/")));
    }

    [Fact]
    public void NonLoopbackHttpOrigin_IsRejected()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("unused"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://batch.invalid/") };
        Assert.Throws<MimoBatchException>(() => new MimoBatchClient(http));
    }

    [Fact]
    public void OptionsBaseAddress_IsValidatedIndependently()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("unused"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://batch.invalid/") };
        var options = new MimoBatchClientOptions(new Uri("http://batch.invalid/"), TimeSpan.FromSeconds(5));
        Assert.Throws<MimoBatchException>(() => new MimoBatchClient(http, options));
    }

    private static (MimoBatchClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        TimeSpan? createTimeout = null, long maxResponseBytes = 64L * 1024 * 1024,
        bool audioBatchEnabled = false)
    {
        var handler = new StubHandler(responder);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://batch.invalid/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-key");
        return (new MimoBatchClient(http, new MimoBatchClientOptions(http.BaseAddress!,
            createTimeout ?? TimeSpan.FromSeconds(5), maxResponseBytes,
            AudioBatchEnabled: audioBatchEnabled)), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private static object BatchJson(string id, string status, long? expiresAt,
        string? inputFileId = null, string? outputFileId = null, string? errorFileId = null) => new
    {
        id, status, expires_at = expiresAt,
        request_counts = new { total = 2, completed = 1, failed = 0 },
        input_file_id = inputFileId, output_file_id = outputFileId, error_file_id = errorFileId
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri?.AbsolutePath ?? "", body));
            return await responder(request, cancellationToken);
        }
    }

    private sealed class PassThroughHandler(HttpMessageHandler inner) : DelegatingHandler(inner);
}
