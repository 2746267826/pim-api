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
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new(StringComparer.Ordinal);

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
        ct.ThrowIfCancellationRequested();

        if (force)
            BumpGeneration(key);

        _keys[key] = 0;
        while (true)
        {
            if (!force && _cache.TryGetValue(key, out var cached))
                return (T)cached!;

            var version = _versions.GetOrAdd(key, 0);
            var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<object?>>(
                () => RunFactoryAsync(key, version, factory),
                LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var result = (T)(await lazy.Value.ConfigureAwait(false))!;

                if (_inFlight.TryGetValue(key, out var current) && !ReferenceEquals(current, lazy))
                    continue;

                return result;
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, lazy));
            }
        }
    }

    public void EvictByPrefix(string keyPrefix)
    {
        // _keys 在工厂启动前登记（见 GetOrCreateAsync），且写端点在驱逐前已完成提交，
        // 因此快照之后才登记的新键必然读取写后的数据，不会回填旧值。
        foreach (var key in _keys.Keys)
        {
            if (MatchesPrefix(key, keyPrefix))
            {
                _cache.Remove(key);
                BumpGeneration(key);
            }
        }
    }

    private void BumpGeneration(string key)
    {
        _versions.AddOrUpdate(key, 1, (_, version) => version + 1);
        _cache.Remove(key);
        _inFlight.TryRemove(key, out _);
    }

    private static bool MatchesPrefix(string key, string keyPrefix)
    {
        var separatorIndex = key.IndexOf('|');
        var pathPart = separatorIndex >= 0 ? key.AsSpan(separatorIndex + 1) : key.AsSpan();
        return pathPart.StartsWith(keyPrefix, StringComparison.Ordinal);
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
            entry.RegisterPostEvictionCallback(OnEvicted, version);
            _keys[key] = 0;
        }
        return value;
    }

    private void OnEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        if (key is not string cacheKey) return;
        if (reason is EvictionReason.Removed or EvictionReason.Replaced) return;
        var stateVersion = state is int version ? version : -1;
        if (_versions.TryGetValue(cacheKey, out var current) && current == stateVersion)
        {
            _keys.TryRemove(cacheKey, out _);
            _versions.TryRemove(cacheKey, out _);
        }
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
