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
        Assert.StartsWith("u:anon|/api/v1/pc/summary?", first);
        Assert.Contains("a=1", first);
        Assert.Contains("b=2", first);
        Assert.DoesNotContain("force", first);
    }

    [Fact]
    public void BuildKey_IncludesUserIdentity()
    {
        var userContext = new DefaultHttpContext();
        userContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-7")],
                "test"));
        userContext.Request.Path = "/api/v1/mobile/location/analytics/tracks";

        var anonymousContext = new DefaultHttpContext();
        anonymousContext.Request.Path = "/api/v1/mobile/location/analytics/tracks";

        var userKey = AggregateResultCacheKeys.Build(userContext.Request);
        var anonymousKey = AggregateResultCacheKeys.Build(anonymousContext.Request);

        Assert.StartsWith("u:user-7|", userKey);
        Assert.StartsWith("u:anon|", anonymousKey);
        Assert.NotEqual(userKey, anonymousKey);
    }

    [Fact]
    public void BuildKey_OverridesReplaceQueryParameters()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/pc/aw/heatmap";
        context.Request.QueryString = new QueryString("?start=2026-01-01");
        var overrides = new List<KeyValuePair<string, string>>
        {
            new("start", "2026-08-08"),
            new("end", "2026-08-15"),
        };

        var key = AggregateResultCacheKeys.Build(context.Request, overrides: overrides);

        Assert.Contains("start=2026-08-08", key);
        Assert.Contains("end=2026-08-15", key);
        Assert.DoesNotContain("2026-01-01", key);
    }

    [Fact]
    public async Task GetOrCreateAsync_ConcurrentSameKey_FactoryRunsOnce()
    {
        var cache = CreateCache();
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory()
        {
            calls++;
            await gate.Task;
            return 42;
        }

        var first = cache.GetOrCreateAsync("key", false, Factory);
        var second = cache.GetOrCreateAsync("key", false, Factory);
        var third = cache.GetOrCreateAsync("key", false, Factory);

        gate.SetResult();
        var results = await Task.WhenAll(first, second, third);

        Assert.All(results, result => Assert.Equal(42, result));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_ForceDuringInFlight_StaleFactoryDoesNotOverwrite()
    {
        var cache = CreateCache();
        var slowFactoryGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = cache.GetOrCreateAsync("key", false, () => slowFactoryGate.Task);

        var forced = await cache.GetOrCreateAsync("key", true, () => Task.FromResult(99));
        Assert.Equal(99, forced);

        slowFactoryGate.SetResult(1);
        var stale = await inFlight;
        Assert.Equal(1, stale);

        var afterForce = await cache.GetOrCreateAsync("key", false, () => Task.FromResult(100));
        Assert.Equal(99, afterForce);
    }

    [Fact]
    public async Task EvictByPrefix_RemovesMatchingEntries()
    {
        var cache = CreateCache();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/pc/summary";
        context.Request.QueryString = new QueryString("?date=2026-08-15");
        var pcSummaryKey = AggregateResultCacheKeys.Build(context.Request);
        context.Request.Path = "/api/v1/pc/aw/heatmap";
        context.Request.QueryString = QueryString.Empty;
        var pcHeatmapKey = AggregateResultCacheKeys.Build(context.Request);
        context.Request.Path = "/api/v1/mobile/summary";
        var mobileKey = AggregateResultCacheKeys.Build(context.Request);

        await cache.GetOrCreateAsync(pcSummaryKey, false, () => Task.FromResult(1));
        await cache.GetOrCreateAsync(pcHeatmapKey, false, () => Task.FromResult(2));
        await cache.GetOrCreateAsync(mobileKey, false, () => Task.FromResult(3));

        cache.EvictByPrefix("/api/v1/pc/");

        var pcSummaryCalls = 0;
        var pcHeatmapCalls = 0;
        var mobileCalls = 0;

        var pcSummary = await cache.GetOrCreateAsync(pcSummaryKey, false, () =>
        {
            pcSummaryCalls++;
            return Task.FromResult(11);
        });
        var pcHeatmap = await cache.GetOrCreateAsync(pcHeatmapKey, false, () =>
        {
            pcHeatmapCalls++;
            return Task.FromResult(12);
        });
        var mobile = await cache.GetOrCreateAsync(mobileKey, false, () =>
        {
            mobileCalls++;
            return Task.FromResult(13);
        });

        Assert.Equal(11, pcSummary);
        Assert.Equal(12, pcHeatmap);
        Assert.Equal(3, mobile);
        Assert.Equal(1, pcSummaryCalls);
        Assert.Equal(1, pcHeatmapCalls);
        Assert.Equal(0, mobileCalls);
    }

    [Fact]
    public async Task EvictByPrefix_RemovesEntriesAcrossAllUsers()
    {
        var cache = CreateCache();
        var userOne = new DefaultHttpContext();
        userOne.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-1")],
                "test"));
        userOne.Request.Path = "/api/v1/mobile/analytics/overview";
        var userOneKey = AggregateResultCacheKeys.Build(userOne.Request);

        var userTwo = new DefaultHttpContext();
        userTwo.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-2")],
                "test"));
        userTwo.Request.Path = "/api/v1/mobile/analytics/overview";
        var userTwoKey = AggregateResultCacheKeys.Build(userTwo.Request);

        await cache.GetOrCreateAsync(userOneKey, false, () => Task.FromResult(1));
        await cache.GetOrCreateAsync(userTwoKey, false, () => Task.FromResult(2));

        cache.EvictByPrefix("/api/v1/mobile/");

        var calls = 0;
        var first = await cache.GetOrCreateAsync(userOneKey, false, () =>
        {
            calls++;
            return Task.FromResult(10);
        });
        var second = await cache.GetOrCreateAsync(userTwoKey, false, () =>
        {
            calls++;
            return Task.FromResult(20);
        });

        Assert.Equal(10, first);
        Assert.Equal(20, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EvictByPrefix_DuringInFlight_StaleFactoryDoesNotRepopulate()
    {
        var cache = CreateCache();
        var slowFactoryGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/pc/summary";
        var key = AggregateResultCacheKeys.Build(context.Request);

        var inFlight = cache.GetOrCreateAsync(key, false, () => slowFactoryGate.Task);

        cache.EvictByPrefix("/api/v1/pc/");

        slowFactoryGate.SetResult(1);
        var stale = await inFlight;
        Assert.Equal(1, stale);

        var calls = 0;
        var fresh = await cache.GetOrCreateAsync(key, false, () =>
        {
            calls++;
            return Task.FromResult(99);
        });

        Assert.Equal(99, fresh);
        Assert.Equal(1, calls);
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
