using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Pim.Core.Caching;
using Xunit;

namespace Pim.UnitTests.Caching;

public sealed class MemoryAggregateResultCacheTests
{
    private sealed class FakeTimeProvider : TimeProvider, ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static MemoryAggregateResultCache CreateCache(FakeTimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider();
        var memoryCache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        return new MemoryAggregateResultCache(memoryCache, clock);
    }

    [Fact]
    public async Task GetOrCreateAsync_SameKeySecondCall_HitsCache()
    {
        var cache = CreateCache();
        var calls = 0;

        Task<int> Factory()
        {
            calls++;
            return Task.FromResult(42);
        }

        var first = await cache.GetOrCreateAsync("key", false, Factory);
        var second = await cache.GetOrCreateAsync("key", false, Factory);

        Assert.Equal(42, first);
        Assert.Equal(42, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_DifferentKeys_AreIndependent()
    {
        var cache = CreateCache();
        var calls = 0;

        Task<string> Factory(string value)
        {
            calls++;
            return Task.FromResult(value);
        }

        var firstA = await cache.GetOrCreateAsync("a", false, () => Factory("a-1"));
        var firstB = await cache.GetOrCreateAsync("b", false, () => Factory("b-1"));
        var secondA = await cache.GetOrCreateAsync("a", false, () => Factory("a-2"));

        Assert.Equal("a-1", firstA);
        Assert.Equal("b-1", firstB);
        Assert.Equal("a-1", secondA);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_ForceTrue_SkipsCacheAndBackfills()
    {
        var cache = CreateCache();
        var calls = 0;

        Task<int> Factory(int value)
        {
            calls++;
            return Task.FromResult(value);
        }

        var first = await cache.GetOrCreateAsync("key", false, () => Factory(1));
        var forced = await cache.GetOrCreateAsync("key", true, () => Factory(2));
        var second = await cache.GetOrCreateAsync("key", false, () => Factory(3));

        Assert.Equal(1, first);
        Assert.Equal(2, forced);
        Assert.Equal(2, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ResolveTtl_Daytime_ReturnsFiveMinutes()
    {
        var cache = CreateCache();
        var utcNow = new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero);

        var ttl = cache.ResolveTtl(utcNow);

        Assert.Equal(TimeSpan.FromMinutes(5), ttl);
    }

    [Fact]
    public void ResolveTtl_LateNight_ReturnsThirtyMinutes()
    {
        var cache = CreateCache();
        var utcNow = new DateTimeOffset(2026, 8, 15, 18, 0, 0, TimeSpan.Zero);

        var ttl = cache.ResolveTtl(utcNow);

        Assert.Equal(TimeSpan.FromMinutes(30), ttl);
    }

    [Fact]
    public void ResolveTtl_LateNightBoundary_ReturnsThirtyMinutes()
    {
        var cache = CreateCache();
        var utcNow = new DateTimeOffset(2026, 8, 15, 21, 59, 59, TimeSpan.Zero);

        var ttl = cache.ResolveTtl(utcNow);

        Assert.Equal(TimeSpan.FromMinutes(30), ttl);
    }

    [Fact]
    public void BuildKey_ExcludesForceAndSortsParameters()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/pc/summary";
        context.Request.QueryString = new QueryString("?b=2&force=true&a=1");
        var first = AggregateResultCacheKeys.Build(context.Request);

        context.Request.QueryString = new QueryString("?a=1&b=2");
        var second = AggregateResultCacheKeys.Build(context.Request);

        Assert.Equal(second, first);
        Assert.StartsWith("/api/v1/pc/summary?", first);
        Assert.Contains("a=1", first);
        Assert.Contains("b=2", first);
        Assert.DoesNotContain("force", first);
    }

    [Fact]
    public async Task GetOrCreateAsync_FactoryThrows_PropagatesAndDoesNotCache()
    {
        var cache = CreateCache();
        var calls = 0;

        Task<int> Factory()
        {
            calls++;
            throw new InvalidOperationException("boom");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync("key", false, Factory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync("key", false, Factory));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_AfterTtlElapsed_ReExecutesFactory()
    {
        var clock = new FakeTimeProvider();
        var cache = CreateCache(clock);
        var calls = 0;

        Task<int> Factory()
        {
            calls++;
            return Task.FromResult(calls);
        }

        var first = await cache.GetOrCreateAsync("key", false, Factory);

        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        var second = await cache.GetOrCreateAsync("key", false, Factory);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, calls);
    }
}
