using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

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
public sealed class AiTextWireCapture : IDisposable
{
    private static readonly AsyncLocal<AiTextWireCapture?> Slot = new();
    private readonly AiTextWireCapture? _previous = Slot.Value;
    public static AiTextWireCapture? Current => Slot.Value;
    public string? RequestBody { get; private set; }
    public string? ResponseBody { get; private set; }
    public bool ResponseTooLarge { get; private set; }
    public AiTextWireCapture() => Slot.Value = this;
    public void Request(byte[] bytes) => RequestBody = System.Text.Encoding.UTF8.GetString(bytes);
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
            var bytes = await ReadAsync(requestContent, 2 * 1024 * 1024, ct);
            capture?.Request(bytes);
            var replacement = new ByteArrayContent(bytes);
            foreach (var header in requestContent.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = replacement; requestContent.Dispose();
        }
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

    private static async Task<byte[]> ReadAsync(HttpContent content, int max, CancellationToken ct)
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
