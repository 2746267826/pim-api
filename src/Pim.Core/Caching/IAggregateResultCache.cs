namespace Pim.Core.Caching;

public interface IAggregateResultCache
{
    Task<T> GetOrCreateAsync<T>(string key, bool force, Func<Task<T>> factory, CancellationToken ct = default);

    TimeSpan ResolveTtl(DateTimeOffset utcNow);
}
