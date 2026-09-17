using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Api.Tiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Pim.UnitTests.Tiles;

public sealed class TileServiceTests
{
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52
    ];

    [Fact]
    public void AddTileServices_registers_tile_service()
    {
        using var cache = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tiles:CacheDirectory"] = cache.Path,
                ["Tiles:UpstreamBaseUrl"] = "https://tile.example.test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTileServices(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<TileService>());
    }

    [Fact]
    public async Task Second_request_for_same_tile_uses_disk_cache()
    {
        using var cache = new TemporaryDirectory();
        var handler = new CountingHandler((_, _) => PngResponse());
        var service = CreateService(handler, cache.Path);
        var coordinate = new TileCoordinate(10, 845, 396);

        var first = await service.GetTileAsync(coordinate, CancellationToken.None);
        var second = await service.GetTileAsync(coordinate, CancellationToken.None);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(Png, second.Bytes);
    }

    [Fact]
    public async Task Non_image_upstream_response_is_not_cached_and_returns_error()
    {
        using var cache = new TemporaryDirectory();
        var handler = new CountingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>blocked</html>")
        });
        var service = CreateService(handler, cache.Path);

        await Assert.ThrowsAsync<TileUpstreamException>(() => service.GetTileAsync(new TileCoordinate(3, 1, 2), CancellationToken.None));
        await Assert.ThrowsAsync<TileUpstreamException>(() => service.GetTileAsync(new TileCoordinate(3, 1, 2), CancellationToken.None));

        Assert.Equal(2, handler.CallCount);
        Assert.Empty(Directory.EnumerateFiles(cache.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_requests_share_one_upstream_fetch_and_write_a_valid_file()
    {
        using var cache = new TemporaryDirectory();
        var handler = new CountingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(30, cancellationToken);
            return PngResponse();
        });
        var service = CreateService(handler, cache.Path);
        var coordinate = new TileCoordinate(8, 12, 34);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => service.GetTileAsync(coordinate, CancellationToken.None)));

        Assert.Equal(1, handler.CallCount);
        Assert.All(results, result => Assert.Equal(Png, result.Bytes));
        Assert.Single(Directory.EnumerateFiles(cache.Path, "*.png", SearchOption.AllDirectories));
    }

    private static TileService CreateService(HttpMessageHandler handler, string cachePath)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://tile.example.test/") };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PimPlatform", "test"));
        var options = Options.Create(new TileOptions
        {
            CacheDirectory = cachePath,
            UpstreamBaseUrl = "https://tile.example.test",
            CacheTtl = TimeSpan.FromHours(1),
        });
        return new TileService(client, options, TimeProvider.System, NullLogger<TileService>.Instance);
    }

    private static HttpResponseMessage PngResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Png)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return await callback(request, cancellationToken);
        }

        public CountingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback)
            : this((request, _) => Task.FromResult(callback(request, CancellationToken.None))) { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pim-tiles-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
