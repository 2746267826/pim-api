using System;
using System.Collections.Generic;
using System.Linq;
using Pim.UnitTests.Harness.Generators;
using Pim.UnitTests.Harness.Invariants;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class CrossModulePropertyTests
{
    [Fact]
    public void OverviewTotal_ShouldEqualHeatmapSum()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var sessions = OverlappingSessionGenerator.Generate(30, seed: seed);
            var hourBuckets = AggregateToHourBuckets(sessions);
            var total = hourBuckets.Values.Sum();
            var (pass, detail) = DataConsistencyInvariants.CheckOverviewEqualsHeatmapSum(total, hourBuckets);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void OverviewTotal_ShouldEqualDailyTrendSum()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var sessions = OverlappingSessionGenerator.Generate(25, seed: seed);
            var dailyTotals = AggregateToDailyTotals(sessions);
            var total = dailyTotals.Values.Sum();
            var trend = dailyTotals.Select(kv => (kv.Key, kv.Value)).ToList();
            var (pass, detail) = DataConsistencyInvariants.CheckOverviewEqualsDailyTrendSum(total, trend);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void AppCount_ShouldEqualDistinctPackages()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var sessions = OverlappingSessionGenerator.Generate(20, seed: seed);
            var packages = sessions.Select(s => s.packageName).ToHashSet();
            var (pass, detail) = DataConsistencyInvariants.CheckAppCountConsistency(packages.Count, packages);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void CategoryShare_ShouldSumToHundred()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var categories = new[] { "聊天", "视频", "办公", "游戏" };
            var remaining = 100.0;
            var list = new List<(string category, double percentage)>();
            for (int i = 0; i < categories.Length - 1; i++)
            {
                var pct = Math.Round(faker.Random.Double(0, remaining), 1);
                list.Add((categories[i], pct));
                remaining -= pct;
            }
            list.Add((categories[^1], Math.Round(remaining, 1)));
            var (pass, detail) = DataConsistencyInvariants.CheckCategoryShareSumToHundred(list);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void HeatmapHourCoverage_ShouldBeComplete()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var dict = Enumerable.Range(0, 24).ToDictionary(h => h, h => (double)(seed % 10 + h));
            var (pass, detail) = DataConsistencyInvariants.CheckHeatmapHourCoverage(dict);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void HeatmapGranularityBucketCount_ShouldBeValid()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var days = faker.Random.Int(1, 7);
            var granularity = faker.PickRandom(new[] { "hour", "30m", "15m", "day" });
            var bucketCount = granularity switch
            {
                "hour" => days * 24,
                "30m" => days * 48,
                "15m" => days * 96,
                _ => days
            };
            var (pass, detail) = DataConsistencyInvariants.CheckHeatmapGranularityBucketCount(bucketCount, days, granularity);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void TimelineBlocks_ShouldBeOrdered()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var baseTime = DateTimeOffset.Parse("2026-07-06T08:00:00+08:00");
            var blocks = new List<(DateTimeOffset start, DateTimeOffset end, string lifeCategory)>();
            var cursor = baseTime;
            var cats = new[] { "办公", "视频", "聊天" };
            for (int i = 0; i < 5; i++)
            {
                var cat = faker.PickRandom(cats);
                var gap = faker.Random.Int(6, 20);
                var dur = faker.Random.Int(10, 30);
                var s = cursor.AddMinutes(gap);
                var e = s.AddMinutes(dur);
                blocks.Add((s, e, cat));
                cursor = e;
            }
            var (pass, detail) = DataConsistencyInvariants.CheckTimelineBlocksOrdered(blocks);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void DeviceMergeDataIntegrity_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var perDevice = MultiDeviceGenerator.GenerateFourDevices(10, seed: seed);
            var (pre, post) = MultiDeviceGenerator.MergeCounts(perDevice);
            var (pass, detail) = DataConsistencyInvariants.CheckDeviceMergeDataIntegrity(pre, "android-main", post);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void DeviceDeleteCascade_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var remaining = new Dictionary<string, int>
            {
                ["mobile_usage_sessions"] = 0,
                ["mobile_usage_summaries"] = 0,
                ["mobile_location_points"] = 0
            };
            var (pass, detail) = DataConsistencyInvariants.CheckDeviceDeleteCascade(remaining);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void QualityCompletenessFallbackRelation_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var fallback = Math.Round(faker.Random.Double(0, 1), 2);
            var completeness = Math.Round(1 - fallback, 2);
            var (pass, detail) = DataConsistencyInvariants.CheckQualityCompletenessFallbackRelation(completeness, fallback);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // helpers to mirror MobilePropertyTests aggregation
    private static Dictionary<int, double> AggregateToHourBuckets(List<(string packageName, DateTimeOffset start, DateTimeOffset end)> sessions)
    {
        var perHour = new Dictionary<int, List<(DateTimeOffset start, DateTimeOffset end)>>();
        foreach (var s in sessions)
        {
            var cur = s.start;
            while (cur < s.end)
            {
                var h = cur.Hour;
                var hs = new DateTimeOffset(cur.Year, cur.Month, cur.Day, h, 0, 0, cur.Offset);
                var ne = hs.AddHours(1);
                var segEnd = s.end < ne ? s.end : ne;
                if (segEnd <= cur) break;
                if (!perHour.ContainsKey(h)) perHour[h] = new();
                perHour[h].Add((cur, segEnd));
                cur = segEnd;
            }
        }
        var buckets = new Dictionary<int, double>();
        foreach (var kv in perHour)
        {
            var merged = Merge(kv.Value);
            buckets[kv.Key] = merged.Sum(p => (p.end - p.start).TotalSeconds);
        }
        return buckets;
    }

    private static Dictionary<string, double> AggregateToDailyTotals(List<(string packageName, DateTimeOffset start, DateTimeOffset end)> sessions)
    {
        var perDay = new Dictionary<string, List<(DateTimeOffset start, DateTimeOffset end)>>();
        foreach (var s in sessions)
        {
            var cur = s.start;
            while (cur < s.end)
            {
                var dayStart = new DateTimeOffset(cur.Year, cur.Month, cur.Day, 0, 0, 0, cur.Offset);
                var ne = dayStart.AddDays(1);
                var segEnd = s.end < ne ? s.end : ne;
                if (segEnd <= cur) break;
                var key = cur.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
                if (!perDay.ContainsKey(key)) perDay[key] = new();
                perDay[key].Add((cur, segEnd));
                cur = segEnd;
            }
        }
        var totals = new Dictionary<string, double>();
        foreach (var kv in perDay)
        {
            var merged = Merge(kv.Value);
            totals[kv.Key] = merged.Sum(p => (p.end - p.start).TotalSeconds);
        }
        return totals;
    }

    private static List<(DateTimeOffset start, DateTimeOffset end)> Merge(List<(DateTimeOffset start, DateTimeOffset end)> intervals)
    {
        if (intervals.Count == 0) return new();
        var sorted = intervals.OrderBy(p => p.start).ToList();
        var merged = new List<(DateTimeOffset start, DateTimeOffset end)> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            var cur = sorted[i];
            if (cur.start <= last.end)
                merged[^1] = (last.start, cur.end > last.end ? cur.end : last.end);
            else merged.Add(cur);
        }
        return merged;
    }
}
