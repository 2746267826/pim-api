using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Pim.Module.Files.Services;

/// <summary>
/// Real embedding via OpenAI text-embedding-3-small (1536 dims, truncated to 384) or fallback to hashing.
/// Requires OPENAI_API_KEY env/config; otherwise uses HashingFileEmbeddingService internally.
/// </summary>
public sealed class OpenAiFileEmbeddingService : IFileEmbeddingService
{
    private const string DefaultModel = "text-embedding-3-small";
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<OpenAiFileEmbeddingService> _logger;
    private readonly HashingFileEmbeddingService _fallback;

    public int Dimensions { get; }

    public OpenAiFileEmbeddingService(HttpClient http, IConfiguration config, ILogger<OpenAiFileEmbeddingService> logger, int dimensions = HashingFileEmbeddingService.DefaultDimensions)
    {
        _http = http;
        _config = config;
        _logger = logger;
        Dimensions = dimensions;
        _fallback = new HashingFileEmbeddingService(dimensions);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var apiKey = _config["OPENAI_API_KEY"] ?? _config["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return await _fallback.EmbedAsync(text, ct);

        try
        {
            var baseUrl = _config["OPENAI_BASE_URL"] ?? _config["OpenAI:BaseUrl"] ?? "https://api.openai.com/v1";
            var model = _config["OPENAI_EMBEDDING_MODEL"] ?? DefaultModel;
            var req = new EmbeddingRequest(model, text);
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/embeddings")
            {
                Content = JsonContent.Create(req)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(message, ct);
            resp.EnsureSuccessStatusCode();
            var result = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);
            var vector = result?.Data?.FirstOrDefault()?.Embedding;
            if (vector is null || vector.Length == 0)
                return await _fallback.EmbedAsync(text, ct);

            // truncate or pad to Dimensions
            var output = new float[Dimensions];
            var len = Math.Min(vector.Length, Dimensions);
            Array.Copy(vector, output, len);
            Normalize(output);
            return output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "OpenAI embedding failed, falling back to hashing");
            return await _fallback.EmbedAsync(text, ct);
        }
    }

    private static void Normalize(float[] vector)
    {
        var magnitudeSquared = 0f;
        foreach (var v in vector) magnitudeSquared += v * v;
        if (magnitudeSquared == 0f) return;
        var mag = MathF.Sqrt(magnitudeSquared);
        for (var i = 0; i < vector.Length; i++) vector[i] /= mag;
    }

    private sealed record EmbeddingRequest([property: JsonPropertyName("model")] string Model, [property: JsonPropertyName("input")] string Input);
    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] EmbeddingData[] Data);
    private sealed record EmbeddingData([property: JsonPropertyName("embedding")] float[] Embedding);
}
