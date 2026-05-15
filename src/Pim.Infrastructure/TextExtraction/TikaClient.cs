using System.Net.Http.Headers;

namespace Pim.Infrastructure.TextExtraction;

public class TikaClient
{
    private readonly HttpClient _httpClient;

    public TikaClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<string> ExtractTextAsync(
        Stream fileStream, string fileName, CancellationToken ct = default)
    {
        using var content = new StreamContent(fileStream);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");

        var response = await _httpClient.PutAsync("/tika", content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> ExtractTextAsync(
        byte[] fileBytes, string fileName, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(fileBytes);
        return await ExtractTextAsync(stream, fileName, ct);
    }
}
