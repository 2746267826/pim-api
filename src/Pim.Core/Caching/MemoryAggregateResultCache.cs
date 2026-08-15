using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Pim.Core.Caching;

public sealed class MemoryAggregateResultCache : IAggregateResultCache
{
    private static readonly TimeZoneInfo ShanghaiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<object?>> _inFlight = new(StringComparer.Ordinal);

    public MemoryAggregateResultCache(IMemoryCache cache, TimeProvider timeProvider)
    {
        _cache = cache;
        _timeProvider = timeProvider;
    }

    public TimeSpan ResolveTtl(DateTimeOffset utcNow)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, ShanghaiTimeZone);
        return localNow.Hour < 6 ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5);
    }

    public async Task<T> GetOrCreateAsync<T>(string key, bool force, Func<Task<T>> factory, CancellationToken ct = default)
    {
        if (force)
        {
            _versions.AddOrUpdate(key, 1, (_, version) => version + 1);
            _cache.Remove(key);
            _inFlight.TryRemove(key, out _);
        }

        ct.ThrowIfCancellationRequested();

        _keys[key] = 0;
        while (true)
        {
            if (!force && _cache.TryGetValue(key, out var cached))
                return (T)cached!;

            var version = _versions.GetOrAdd(key, 0);
            var task = _inFlight.GetOrAdd(key, _ => RunFactoryAsync(key, version, factory));
            try
            {
                var result = (T)(await task.ConfigureAwait(false))!;

                if (_inFlight.TryGetValue(key, out var current) && !ReferenceEquals(current, task))
                    continue;

                return result;
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Task<object?>>(key, task));
            }
        }
    }

    public void EvictByPrefix(string keyPrefix)
    {
        foreach (var key in _keys.Keys)
        {
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                _cache.Remove(key);
                _keys.TryRemove(key, out _);
                _versions.TryRemove(key, out _);
                _inFlight.TryRemove(key, out _);
            }
        }
    }

    private async Task<object?> RunFactoryAsync<T>(string key, int version, Func<Task<T>> factory)
    {
        var value = await factory().ConfigureAwait(false);
        if (_versions.TryGetValue(key, out var current) && current == version)
        {
            using var entry = _cache.CreateEntry(key);
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = ResolveTtl(_timeProvider.GetUtcNow());
            entry.Value = (object?)value;
        }
        return value;
    }
}

public static class CachingServiceCollectionExtensions
{
    public static IServiceCollection AddAggregateResultCaching(this IServiceCollection services)
    {
        services.AddMemoryCache(options => options.SizeLimit = 10_000);
        services.AddSingleton<IAggregateResultCache, MemoryAggregateResultCache>();
        return services;
    }
}
