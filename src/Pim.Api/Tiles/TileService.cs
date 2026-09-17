using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Pim.Api.Tiles;

public sealed record TileResult(byte[] Bytes, bool FromCache);

public sealed class TileUpstreamException(string message) : Exception(message);

public sealed class TileService
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private readonly HttpClient _httpClient;
    private readonly TileOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TileService> _logger;
    private readonly ConcurrentDictionary<TileCoordinate, SemaphoreSlim> _locks = new();

    public TileService(
        HttpClient httpClient,
        IOptions<TileOptions> options,
        TimeProvider timeProvider,
        ILogger<TileService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        Directory.CreateDirectory(_options.CacheDirectory);
    }

    public async Task<TileResult> GetTileAsync(TileCoordinate coordinate, CancellationToken cancellationToken)
    {
        var path = GetCachePath(coordinate);
        var cached = await ReadFreshCacheAsync(path, cancellationToken);
        if (cached is not null)
            return new TileResult(cached, FromCache: true);

        var gate = _locks.GetOrAdd(coordinate, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            cached = await ReadFreshCacheAsync(path, cancellationToken);
            if (cached is not null)
                return new TileResult(cached, FromCache: true);

            var bytes = await DownloadAsync(coordinate, cancellationToken);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            return new TileResult(bytes, FromCache: false);
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetCachePath(TileCoordinate coordinate)
        => Path.Combine(_options.CacheDirectory, coordinate.CacheRelativePath);

    private async Task<byte[]?> ReadFreshCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        var modified = File.GetLastWriteTimeUtc(path);
        if (_timeProvider.GetUtcNow() - new DateTimeOffset(modified, TimeSpan.Zero) >= _options.CacheTtl)
            return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return IsPng(bytes) ? bytes : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<byte[]> DownloadAsync(TileCoordinate coordinate, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"{coordinate.Z}/{coordinate.X}/{coordinate.Y}.png",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Tile upstream returned HTTP {StatusCode} for z={Zoom} x={X} y={Y}",
                response.StatusCode, coordinate.Z, coordinate.X, coordinate.Y);
            throw new TileUpstreamException($"Tile upstream returned {(int)response.StatusCode}");
        }

        var contentType = response.Content.Headers.ContentType;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!IsPng(bytes) || contentType is null || !IsImageContentType(contentType))
        {
            _logger.LogWarning(
                "Tile upstream returned a non-PNG response for z={Zoom} x={X} y={Y}",
                coordinate.Z, coordinate.X, coordinate.Y);
            throw new TileUpstreamException("Tile upstream returned a non-image response");
        }

        return bytes;
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length >= PngSignature.Length
            && PngSignature.AsSpan().SequenceEqual(bytes.AsSpan(0, PngSignature.Length));

    private static bool IsImageContentType(MediaTypeHeaderValue contentType)
        => contentType.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
}
