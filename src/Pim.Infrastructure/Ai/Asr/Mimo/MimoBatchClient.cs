using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

/// <summary>
/// Thin MiMo Batch transport. It never sets credentials, never follows redirects and never
/// retries create; the caller owns authorization and all long-lived persistence.
/// </summary>
public sealed class MimoBatchClient
{
    private readonly HttpClient _http;
    private readonly MimoBatchClientOptions _options;
    private readonly Uri _apiRoot;

    public MimoBatchClient(HttpClient http, MimoBatchClientOptions? options = null)
    {
        _http = http;
        _options = options ?? (http.BaseAddress is { } baseAddress
            ? MimoBatchClientOptions.Default(baseAddress)
            : throw new ArgumentException("HttpClient.BaseAddress is required", nameof(http)));
        ValidateBaseAddress(_options.BaseAddress);
        if (_http.BaseAddress is null) _http.BaseAddress = _options.BaseAddress;
        else
        {
            ValidateBaseAddress(_http.BaseAddress);
            if (!SameOrigin(_http.BaseAddress, _options.BaseAddress))
                throw new ArgumentException("options BaseAddress must match HttpClient.BaseAddress", nameof(options));
        }
        _apiRoot = new UriBuilder(_http.BaseAddress!) { Path = "/", Query = "", Fragment = "" }.Uri;
        if (_options.MaxJsonlBytes <= 0 || _options.MaxResponseBytes <= 0 || _options.CreateTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Batch budgets and timeout must be positive");
    }

    /// <summary>Builds a client whose handler refuses redirects when the handler type exposes that switch.</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler handler, Uri baseAddress)
    {
        ValidateBaseAddress(baseAddress);
        if (!DisableRedirects(handler))
            throw new MimoBatchException(42211, "Batch HttpClient handler不支持关闭自动重定向，拒绝创建客户端");
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<MimoFileRef> UploadJsonlAsync(Stream content, long length, string filename,
        CancellationToken ct)
    {
        if (length <= 0 || length > _options.MaxJsonlBytes)
            throw new MimoBatchException(42203, "Batch JSONL大小超出128MiB上限，未上传");
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 200 ||
            filename.Any(char.IsControl) || filename.IndexOfAny(['/', '\\']) >= 0)
            throw new MimoBatchException(42204, "Batch JSONL文件名无效，未上传");
        if (content.CanSeek && content.Length - content.Position != length)
            throw new MimoBatchException(42210, "Batch JSONL声明长度与实际流长度不一致，未上传");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("batch", Encoding.UTF8), "purpose");
        var file = new StreamContent(new BoundedReadStream(content, _options.MaxJsonlBytes));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        file.Headers.ContentLength = length;
        form.Add(file, "file", filename);
        using var request = new HttpRequestMessage(HttpMethod.Post, Api("v1/files")) { Content = form };
        using var response = await SendAsync(request, ct);
        EnsureSuccess(response, "上传Batch输入文件");
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response, ct),
            new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var id = RequiredString(root, "id");
        var expireAt = UnixSeconds(root, "expire_at");
        return new MimoFileRef(id, expireAt);
    }

    /// <summary>Creates one batch. A timeout/transport failure without a verified id is UNKNOWN, never retried.</summary>
    public async Task<MimoBatchCreateOutcome> CreateBatchAsync(string inputFileId, string endpoint, string name,
        string? completionWindow = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(inputFileId) || inputFileId.Length > 200 || inputFileId.Any(char.IsControl))
            throw new MimoBatchException(42205, "Batch input_file_id无效，未创建任务");
        if (!string.Equals(endpoint, MimoBatchDefaults.ChatCompletionsEndpoint, StringComparison.Ordinal))
            throw new MimoBatchException(42206, "Batch当前仅允许 /v1/chat/completions 接入点");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
            throw new MimoBatchException(42207, "Batch name必填且不能超过200个控制安全字符");
        var window = completionWindow ?? MimoBatchDefaults.CompletionWindow;
        if (!string.Equals(window, MimoBatchDefaults.CompletionWindow, StringComparison.Ordinal))
            throw new MimoBatchException(42208, "Batch completion_window当前仅使用文档明确值24h");

        var payload = JsonSerializer.Serialize(new
        {
            input_file_id = inputFileId,
            endpoint,
            completion_window = window,
            name
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, Api("v1/batches"))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.CreateTimeout);
        var sendStarted = false;
        try
        {
            sendStarted = true;
            using var response = await SendAsync(request, timeout.Token);
            if ((int)response.StatusCode is >= 500 or 408)
                return new MimoBatchCreateOutcome.Unknown("create_upstream_unavailable");
            EnsureSuccess(response, "创建Batch任务");
            try
            {
                using var document = JsonDocument.Parse(await ReadBoundedAsync(response, timeout.Token),
                    new JsonDocumentOptions { MaxDepth = 64 });
                return new MimoBatchCreateOutcome.Known(ParseBatch(document.RootElement));
            }
            catch (Exception error) when (error is JsonException or MimoBatchException or InvalidOperationException or FormatException)
            {
                // The request may already have created a batch; without a verified id this is UNKNOWN.
                return new MimoBatchCreateOutcome.Unknown("create_response_unverified");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && sendStarted)
        {
            // The caller cancelled after the request could have reached the provider; hand off UNKNOWN.
            return new MimoBatchCreateOutcome.Unknown("create_cancelled_unverified");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MimoBatchCreateOutcome.Unknown("create_timeout");
        }
        catch (HttpRequestException)
        {
            return new MimoBatchCreateOutcome.Unknown("create_transport_unknown");
        }
    }

    /// <summary>
    /// Audio Batch is not upstream-verified. Callers must explicitly enable the capability
    /// config before submitting a batch that contains audio.
    /// </summary>
    public void EnsureAudioBatchAllowed(bool containsAudio)
    {
        if (containsAudio && !_options.AudioBatchEnabled)
            throw new MimoBatchException(42212, "MiMo Batch音频能力尚未验证，拒绝提交音频批次");
    }

    /// <summary>Gated overload for audio-containing batches; keeps the original signature compatible.</summary>
    public Task<MimoBatchCreateOutcome> CreateBatchAsync(string inputFileId, string endpoint, string name,
        string? completionWindow, bool containsAudio, CancellationToken ct)
    {
        EnsureAudioBatchAllowed(containsAudio);
        return CreateBatchAsync(inputFileId, endpoint, name, completionWindow, ct);
    }

    public async Task<MimoBatchRef> GetBatchAsync(string batchId, CancellationToken ct)
    {
        ValidateId(batchId, "batch id");
        using var request = new HttpRequestMessage(HttpMethod.Get, Api($"v1/batches/{Uri.EscapeDataString(batchId)}"));
        using var response = await SendAsync(request, ct);
        EnsureSuccess(response, "查询Batch任务");
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response, ct),
            new JsonDocumentOptions { MaxDepth = 64 });
        return ParseBatch(document.RootElement);
    }

    public Task<string> DownloadOutputAsync(string fileId, CancellationToken ct) =>
        DownloadFileAsync(fileId, ct);

    public Task<string> DownloadErrorAsync(string fileId, CancellationToken ct) =>
        DownloadFileAsync(fileId, ct);

    public async Task<MimoBatchRef> CancelAsync(string batchId, CancellationToken ct)
    {
        ValidateId(batchId, "batch id");
        using var request = new HttpRequestMessage(HttpMethod.Post, Api($"v1/batches/{Uri.EscapeDataString(batchId)}/cancel"));
        using var response = await SendAsync(request, ct);
        EnsureSuccess(response, "取消Batch任务");
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response, ct),
            new JsonDocumentOptions { MaxDepth = 64 });
        return ParseBatch(document.RootElement);
    }

    /// <summary>DELETE only removes a terminal batch resource; it is not cancellation or media erasure.</summary>
    public async Task DeleteAsync(MimoBatchRef batch, CancellationToken ct)
    {
        if (!batch.IsTerminal)
            throw new MimoBatchException(40905, "Batch仅允许删除completed/failed/expired/cancelled终态");
        ValidateId(batch.BatchId, "batch id");
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            Api($"v1/batches/{Uri.EscapeDataString(batch.BatchId)}"));
        using var response = await SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.NoContent)
            EnsureSuccess(response, "删除Batch任务");
    }

    private async Task<string> DownloadFileAsync(string fileId, CancellationToken ct)
    {
        ValidateId(fileId, "file id");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            Api($"v1/files/{Uri.EscapeDataString(fileId)}/content"));
        using var response = await SendAsync(request, ct);
        EnsureSuccess(response, "下载Batch结果文件");
        return await ReadBoundedAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is null || !SameOrigin(_apiRoot, request.RequestUri))
            throw new MimoBatchException(40302, "Batch目标地址与配置origin不一致，已拒绝发送");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            response.Dispose();
            throw new MimoBatchException(50204, "Batch服务返回重定向，已拒绝跟随，避免凭据泄漏");
        }
        return response;
    }

    private async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;
            total += read;
            if (total > _options.MaxResponseBytes)
                throw new MimoBatchException(41301, "Batch响应超过大小上限，未保存");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private Uri Api(string relative) => new(_apiRoot, relative);

    private static MimoBatchRef ParseBatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new MimoBatchException(50205, "Batch响应不是JSON对象");
        var id = RequiredString(root, "id");
        var statusRaw = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString() ?? "" : "";
        int? total = null, completed = null, failed = null;
        if (root.TryGetProperty("request_counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
        {
            total = Int(counts, "total");
            completed = Int(counts, "completed");
            failed = Int(counts, "failed");
        }
        return new MimoBatchRef(id, statusRaw, MimoBatchStatus.Normalize(statusRaw),
            UnixSeconds(root, "expires_at"), total, completed, failed,
            OptionalString(root, "input_file_id"), OptionalString(root, "output_file_id"),
            OptionalString(root, "error_file_id"));
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new MimoBatchException(50205, $"Batch响应缺少{name}");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed) ? parsed : null;

    private static DateTimeOffset? UnixSeconds(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var seconds) || seconds < 0 || seconds > 253402300799) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new MimoBatchException(50205,
                $"{operation}返回HTTP {(int)response.StatusCode}，本次未自动重试");
    }

    private static void ValidateId(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl) ||
            id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new MimoBatchException(42205, $"{label}无效");
    }

    private static void ValidateBaseAddress(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new MimoBatchException(42209, "Batch BaseAddress必须是HTTPS配置origin（本地回环可用HTTP）");
    }

    private static bool SameOrigin(Uri configured, Uri request)
    {
        var absoluteRequest = request.IsAbsoluteUri ? request : new Uri(configured, request);
        return string.Equals(configured.Scheme, absoluteRequest.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(configured.Host, absoluteRequest.Host, StringComparison.OrdinalIgnoreCase) &&
               configured.Port == absoluteRequest.Port;
    }

    private static bool DisableRedirects(HttpMessageHandler? handler) => handler switch
    {
        HttpClientHandler clientHandler => Set(clientHandler),
        SocketsHttpHandler socketsHandler => Set(socketsHandler),
        DelegatingHandler delegating => DisableRedirects(delegating.InnerHandler),
        _ => false
    };

    private static bool Set(HttpClientHandler handler)
    {
        handler.AllowAutoRedirect = false;
        return true;
    }

    private static bool Set(SocketsHttpHandler handler)
    {
        handler.AllowAutoRedirect = false;
        return true;
    }

    /// <summary>Second line of defense when a non-seekable caller stream lies about its declared length.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Account(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            Account(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Account(read);
            return read;
        }

        private void Account(int read)
        {
            if (read <= 0) return;
            _read += read;
            if (_read > limit)
                throw new MimoBatchException(42203, "Batch JSONL实际字节数超过128MiB上限，已中止上传");
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
