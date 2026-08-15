using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Pim.Core.Caching;

public sealed class MemoryAggregateResultCache : IAggregateResultCache
{
    private static readonly TimeZoneInfo ShanghaiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;

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
            _cache.Remove(key);

        ct.ThrowIfCancellationRequested();

        var value = await _cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ResolveTtl(_timeProvider.GetUtcNow());
            return factory();
        });
        return value;
    }
}

public static class CachingServiceCollectionExtensions
{
    public static IServiceCollection AddAggregateResultCaching(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<IAggregateResultCache, MemoryAggregateResultCache>();
        return services;
    }
}
