using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace Pim.Infrastructure.Ai;

public sealed record AiModelConnection(string Endpoint, string Model, string ApiKey, int TimeoutSeconds)
{
    public override string ToString() => $"AiModelConnection {{ Endpoint = {Endpoint}, Model = {Model}, ApiKey = [protected] }}";
}

public interface IAiProfileChatClientFactory
{
    AiProfileClientLease Create(AiModelConnection connection);
}

public sealed class AiProfileClientLease(IChatClient client, IDisposable? transportOwner = null) : IDisposable
{
    public IChatClient Client => client;
    public void Dispose() { client.Dispose(); transportOwner?.Dispose(); }
}

/// <summary>Per logical call, async-flow local capture. Never captures authentication headers.</summary>
public sealed record AiVisionAuditImage(Guid Id, string DataUrl)
{
    public override string ToString() => $"AiVisionAuditImage {{ Id = {Id}, Data = [image] }}";
}

public sealed class AiTextWireCapture : IDisposable
{
    private static readonly AsyncLocal<AiTextWireCapture?> Slot = new();
    private readonly AiTextWireCapture? _previous = Slot.Value;
    public static AiTextWireCapture? Current => Slot.Value;
    public string? RequestBody { get; private set; }
    public string? ResponseBody { get; private set; }
    public bool ResponseTooLarge { get; private set; }
    public bool RequestRejected { get; private set; }
    public bool ProviderAttempted { get; private set; }
    private IReadOnlyList<AiVisionAuditImage>? _images;
    private Func<byte[], string>? _requestAudit;
    private long _requestLimitBytes = 8 * 1024 * 1024;
    public long MaxRequestBytes => _requestAudit is not null ? _requestLimitBytes
        : _images is null ? 2 * 1024 * 1024 : 12 * 1024 * 1024;
    public AiTextWireCapture(IReadOnlyList<AiVisionAuditImage>? images = null) { _images = images; Slot.Value = this; }
    public void SetImages(IReadOnlyList<AiVisionAuditImage> images) => _images = images;
    public void SetAsrAudit(Func<byte[], string> audit, long maxRequestBytes = 8L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequestBytes);
        _requestAudit = audit;
        _requestLimitBytes = maxRequestBytes;
    }
    public void Request(byte[] bytes)
    {
        if (_requestAudit is not null)
        {
            try { RequestBody = _requestAudit(bytes); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            { RequestRejected = true; throw new InvalidDataException("ASR请求与固定输入不一致，本次未发送", error); }
            return;
        }
        if (_images is null) { RequestBody = System.Text.Encoding.UTF8.GetString(bytes); return; }
        // Normalize only the SDK's binary image parts for audit storage. Sent bytes remain untouched.
        // All referenced bytes are separately stored and hashed; text is never summarized/replaced.
        var body = JsonNode.Parse(bytes)!;
        var parts = body["messages"]!.AsArray().SelectMany(m => m?["content"] is JsonArray content
            ? content.OfType<JsonObject>() : Enumerable.Empty<JsonObject>())
            .Where(p => p["type"]?.GetValue<string>() == "image_url").ToArray();
        if (parts.Length != _images.Count) Reject();
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index]["image_url"]?["url"]?.GetValue<string>() != _images[index].DataUrl) Reject();
            parts[index]["image_url"]!["url"] = $"pim-ai-image:{_images[index].Id:D}";
        }
        RequestBody = body.ToJsonString();
    }
    private void Reject() { RequestRejected = true; throw new InvalidDataException("SDK图片清单与固定材料不一致，本次未发送模型"); }
    public void Attempt() => ProviderAttempted = true;
    public void Response(byte[] bytes) => ResponseBody = System.Text.Encoding.UTF8.GetString(bytes);
    public void TooLarge() => ResponseTooLarge = true;
    public void Dispose() => Slot.Value = _previous;
}

/// <summary>Only binds saved settings to the official SDK; protocol/JSON parsing remain in the SDK.</summary>
public sealed class AiProfileChatClientFactory(IHttpClientFactory http) : IAiProfileChatClientFactory
{
    public const string ClientName = "ai-profile-sdk";
    public AiProfileClientLease Create(AiModelConnection connection)
    {
        var transport = http.CreateClient(ClientName);
        try
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(connection.Endpoint),
                Transport = new HttpClientPipelineTransport(transport),
                // One click is one SDK transport attempt in this slice. No multiplied retry layers.
                RetryPolicy = new ClientRetryPolicy(0),
                NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds)
            };
            var client = new ChatClient(connection.Model, new ApiKeyCredential(connection.ApiKey), options).AsIChatClient();
            return new(client, transport);
        }
        catch { transport.Dispose(); throw; }
    }
}

/// <summary>Bounded non-streaming text responses. A future streaming path must use its own streaming limits.</summary>
public sealed class AiTextResponseLimitHandler : DelegatingHandler
{
    public const int MaxBytes = 4 * 1024 * 1024;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var capture = AiTextWireCapture.Current;
        if (request.Content is { } requestContent)
        {
            var bytes = await ReadAsync(requestContent, capture?.MaxRequestBytes ?? 2 * 1024 * 1024, ct);
            capture?.Request(bytes);
            var replacement = new ByteArrayContent(bytes);
            foreach (var header in requestContent.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = replacement; requestContent.Dispose();
        }
        capture?.Attempt();
        var response = await base.SendAsync(request, ct);
        try
        {
            var original = response.Content;
            byte[] bytes;
            try { bytes = await ReadAsync(original, MaxBytes, ct); }
            catch (InvalidDataException)
            {
                capture?.TooLarge(); throw;
            }
            capture?.Response(bytes);
            var replacement = new ByteArrayContent(bytes);
            foreach (var header in original.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = replacement; original.Dispose(); return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static async Task<byte[]> ReadAsync(HttpContent content, long max, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max) throw new InvalidDataException("AI请求或响应超过大小上限");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream(); var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct); if (read == 0) break;
            if (memory.Length + read > max) throw new InvalidDataException("AI请求或响应超过大小上限");
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return memory.ToArray();
    }
}
