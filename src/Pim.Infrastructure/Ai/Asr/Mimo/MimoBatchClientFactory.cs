using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

public interface IMimoBatchClientFactory
{
    IMimoBatchClientLease Create(Uri baseAddress, string apiKey);
}

public interface IMimoBatchClientLease : IDisposable
{
    HttpClient Http { get; }
    MimoBatchClient Client { get; }
}

public sealed class MimoBatchClientFactory(IOptions<NoteAsrBatchOptions>? options = null) : IMimoBatchClientFactory
{
    public IMimoBatchClientLease Create(Uri baseAddress, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
            throw new ArgumentException("MiMo Batch API key is required", nameof(apiKey));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false
        };
        var http = MimoBatchClient.CreateHttpClient(handler, baseAddress);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return new Lease(http, new MimoBatchClient(http, new MimoBatchClientOptions(baseAddress,
            TimeSpan.FromSeconds(60), AudioBatchEnabled: options?.Value.AllowUnverifiedAudioBatch == true)));
    }

    private sealed class Lease(HttpClient http, MimoBatchClient client) : IMimoBatchClientLease
    {
        public HttpClient Http { get; } = http;
        public MimoBatchClient Client { get; } = client;
        public void Dispose() => Http.Dispose();
    }
}
