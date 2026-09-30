using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// WO-PC-BACKEND-20260930 · PC 记录接口的 HTTP 级验证（真实宿主装配 + 真实路由 + 隔离内存库）。
/// 覆盖 REQ-3（字段名/量纲）、REQ-4（day 桶边界）、REQ-5（建议归日与空闲）、REQ-6（hour 跨日 400）、
/// REQ-7（键鼠范围聚合）。
/// </summary>
public sealed class PcTrackerEndpointsE2ETests
{
    private static readonly DateTime Day = new(2026, 9, 27);

    [Fact]
    public async Task ActivityAnalysis_DifferentBlockMinutesDoNotShareTheAggregateCacheEntry()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // 聚合结果缓存的键包含除 force 外的全部查询参数；本用例从 HTTP 层证明
        // blockMinutes 不同不会命中同一份缓存（review 提出后补的回归）。
        var hour = await GetJsonAsync(client, "/api/v1/pc/activity-analysis?date=2026-09-27&blockMinutes=60");
        var fourHours = await GetJsonAsync(client, "/api/v1/pc/activity-analysis?date=2026-09-27&blockMinutes=240");
        var quarter = await GetJsonAsync(client, "/api/v1/pc/activity-analysis?date=2026-09-27&blockMinutes=15");

        Assert.Equal(60, hour.GetProperty("data").GetProperty("blockMinutes").GetInt32());
        Assert.Equal(24, hour.GetProperty("data").GetProperty("blocks").GetArrayLength());
        Assert.Equal(240, fourHours.GetProperty("data").GetProperty("blockMinutes").GetInt32());
        Assert.Equal(6, fourHours.GetProperty("data").GetProperty("blocks").GetArrayLength());
        Assert.Equal(15, quarter.GetProperty("data").GetProperty("blockMinutes").GetInt32());
        Assert.Equal(96, quarter.GetProperty("data").GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public async Task HeatmapGrid_HourAcrossDays_Returns400WithExplicitMessage()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        // AC-6.1（P-4 方案 a）：跨日 + hour = 400 + 明确文案，不再静默返回起始日单行。
        var response = await client.GetAsync(
            "/api/v1/pc/heatmap/grid?start=2026-09-26&end=2026-09-28&dimension=hour");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(400, document.RootElement.GetProperty("code").GetInt32());
        var message = document.RootElement.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains("hour", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("单日", message);
        Assert.Contains("2026-09-26", message);
        Assert.Contains("2026-09-28", message);
    }

    [Fact]
    public async Task HeatmapGrid_SingleDayHour_StillReturns24BusinessDayBuckets()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/pc/heatmap/grid?start=2026-09-27&end=2026-09-27&dimension=hour");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var grid = document.RootElement.GetProperty("data").GetProperty("grid");
        Assert.Equal(1, grid.GetArrayLength());
        var row = grid[0];
        Assert.Equal(24, row.GetArrayLength());
        // AC-6.2：单日 hour 自业务日起点起算。
        Assert.Equal(
            PcTrackerService.GetBusinessDayStartForQuery(Day).ToString("O"),
            row[0].GetProperty("start").GetString());
    }

    [Fact]
    public async Task IntensityFields_AreConsistentAndQuantitativelyUnifiedAcrossEndpoints()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, db =>
        {
            var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
            db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(1), 3600, "Code.exe"));
            db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(2), 900, "Code.exe"));
        });
        var client = factory.CreateClient();

        var analysis = await GetJsonAsync(client, "/api/v1/pc/activity-analysis?date=2026-09-27&blockMinutes=60");
        var summary = await GetJsonAsync(client, "/api/v1/pc/summary?date=2026-09-27");
        var grid = await GetJsonAsync(client, "/api/v1/pc/heatmap/grid?start=2026-09-27&end=2026-09-27&dimension=day");
        var hourGrid = await GetJsonAsync(client, "/api/v1/pc/heatmap/grid?start=2026-09-27&end=2026-09-27&dimension=hour");

        // AC-3.1：三个接口强度字段名一致，且都带 intensityMax；旧名 intensityScore 已消失。
        var blocks = analysis.GetProperty("data").GetProperty("blocks");
        var heatmap = summary.GetProperty("data").GetProperty("heatmap");
        var cells = grid.GetProperty("data").GetProperty("grid")[0];
        Assert.Equal(24, blocks.GetArrayLength());
        Assert.Equal(24, heatmap.GetArrayLength());
        Assert.All(blocks.EnumerateArray(), block =>
        {
            Assert.InRange(block.GetProperty("intensityLevel").GetInt32(), 0, 5);
            Assert.Equal(5, block.GetProperty("intensityMax").GetInt32());
            Assert.False(block.TryGetProperty("intensityScore", out _));
        });
        Assert.All(heatmap.EnumerateArray(), bucket =>
        {
            Assert.InRange(bucket.GetProperty("intensityLevel").GetInt32(), 0, 5);
            Assert.Equal(5, bucket.GetProperty("intensityMax").GetInt32());
            Assert.False(bucket.TryGetProperty("intensityScore", out _));
        });
        Assert.All(cells.EnumerateArray(), cell =>
        {
            Assert.InRange(cell.GetProperty("intensityLevel").GetInt32(), 0, 5);
            Assert.Equal(5, cell.GetProperty("intensityMax").GetInt32());
            Assert.True(cell.TryGetProperty("keyPressCount", out _));
            Assert.False(cell.TryGetProperty("intensityScore", out _));
        });
        Assert.All(hourGrid.GetProperty("data").GetProperty("grid")[0].EnumerateArray(), cell =>
        {
            Assert.InRange(cell.GetProperty("intensityLevel").GetInt32(), 0, 5);
            Assert.Equal(5, cell.GetProperty("intensityMax").GetInt32());
            Assert.True(cell.TryGetProperty("keyPressCount", out _));
        });

        // AC-3.2：同一业务日逐小时，activity-analysis 与 summary.heatmap 的档位逐条相同（全量对照）。
        for (var hour = 0; hour < 24; hour++)
        {
            Assert.Equal(
                heatmap[hour].GetProperty("intensityLevel").GetInt32(),
                blocks[hour].GetProperty("intensityLevel").GetInt32());
        }
    }

    [Fact]
    public async Task HeatmapGrid_DayBucketsUseBusinessDayWindowThatFrontendSeesAs0400()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var grid = await GetJsonAsync(client, "/api/v1/pc/heatmap/grid?start=2026-09-21&end=2026-09-23&dimension=day");
        var buckets = grid.GetProperty("data").GetProperty("grid")[0];

        // AC-4.1 / AC-4.3：桶起点 = 业务日起点，前端转 +08:00 后落点为 04:00。
        Assert.Equal(
            PcTrackerService.GetBusinessDayStartForQuery(new DateTime(2026, 9, 21)).ToString("O"),
            buckets[0].GetProperty("start").GetString());
        for (var i = 0; i < buckets.GetArrayLength(); i++)
        {
            var start = DateTimeOffset.Parse(buckets[i].GetProperty("start").GetString()!);
            var end = DateTimeOffset.Parse(buckets[i].GetProperty("end").GetString()!);
            Assert.Equal(4, start.ToOffset(TimeSpan.FromHours(8)).Hour);
            Assert.Equal(TimeSpan.FromHours(24), end - start);
        }
    }

    [Fact]
    public async Task ClassificationSuggestions_ScanWholeBusinessDayAndCarryGeneratedForDate()
    {
        using var factory = CreateFactory();
        var dayStart = PcTrackerService.GetBusinessDayStartForQuery(Day);
        await SeedAsync(factory, db =>
        {
            // 前 210 条（升序第一页 = 200 条）都是已知形态的早期记录；
            // 只有最后一条「Mystery.exe」需要建议 —— 旧实现只扫第 1 页时它根本不会出现。
            for (var i = 0; i < 210; i++)
                db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddMinutes(i * 4), 60, "Code.exe"));
            db.Set<AwEventEntity>().Add(WindowEvent(dayStart.AddHours(20), 1200, "Mystery.exe"));
        });
        var client = factory.CreateClient();
        var detail = await GetJsonAsync(
            client, "/api/v1/pc/detail?dateFrom=2026-09-27&dateTo=2026-09-27&view=interpreted&pageSize=1&page=1");
        var totalCount = detail.GetProperty("data").GetProperty("totalCount").GetInt32();
        Assert.True(totalCount > 200, $"扫描输入必须超过旧的 200 条分页上限，实际 totalCount={totalCount}。");

        var suggestions = await GetJsonAsync(client, "/api/v1/pc/classification/suggestions?date=2026-09-27");
        var items = suggestions.GetProperty("data");
        Assert.True(items.GetArrayLength() > 0);
        // AC-5.1：每条建议都带业务日。
        Assert.All(items.EnumerateArray(), item =>
        {
            Assert.True(item.TryGetProperty("generatedForDate", out var date));
            Assert.False(string.IsNullOrWhiteSpace(date.GetString()));
        });
        // AC-5.3（同 AC-1.3）：第一页之外的建议也能被扫到。
        Assert.Contains(items.EnumerateArray(), item =>
            string.Equals(item.GetProperty("clusterKey").GetString(), "app:mystery", StringComparison.OrdinalIgnoreCase));
        // AC-5.2：空闲哨兵不再成为建议簇。
        Assert.DoesNotContain(items.EnumerateArray(), item =>
            (item.GetProperty("clusterKey").GetString() ?? string.Empty).Contains("__idle__", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task KeystatsRangeEndpoint_IsShapedLikeTheSingleDayKeystatsAndMatchesPerDayAggregate()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, db =>
        {
            for (var i = 0; i < 30; i++)
            {
                var day = new DateTime(2026, 8, 1).AddDays(i);
                var row = new KeystatsDailyEntity
                {
                    DeviceId = "device-1",
                    SnapshotDate = day,
                    KeyPresses = 1000 + i,
                    LeftClicks = 10 + i,
                    RightClicks = 4,
                    MiddleClicks = 2,
                    SideBackClicks = 1,
                    SideForwardClicks = 1,
                    ScrollDistance = 50 + i,
                    PeakKps = 3 + (i % 5),
                    PeakCps = 2 + (i % 3),
                    CreatedAt = new DateTimeOffset(day, TimeSpan.Zero).AddHours(12)
                };
                row.KeyCounts.Add(new KeystatsKeyCountEntity { KeyName = "A", Count = 600 + i });
                row.KeyCounts.Add(new KeystatsKeyCountEntity { KeyName = "B", Count = 400 });
                db.Set<KeystatsDailyEntity>().Add(row);
            }
        });
        var client = factory.CreateClient();

        var range = await GetJsonAsync(client, "/api/v1/pc/aggregation/keystats?start=2026-08-01&end=2026-08-30");
        var data = range.GetProperty("data");

        // AC-7.3：字段与单日版 summary.keystats 同构（同名同量纲）。
        foreach (var field in new[]
                 {
                     "keyPressCounts", "topKeys", "leftClicks", "middleClicks", "rightClicks",
                     "scrollDistance", "peakKps", "peakCps", "totalKeyPresses", "totalClicks"
                 })
        {
            Assert.True(data.TryGetProperty(field, out _), $"范围响应缺少字段 {field}");
        }

        var summary = await GetJsonAsync(client, "/api/v1/pc/summary?date=2026-08-01");
        var single = summary.GetProperty("data").GetProperty("keystats");
        // AC-7.3：范围响应是单日版的超集，前端可直接复用同一组件（含 keyPresses / 侧键 / mouseDistance）。
        Assert.All(new[]
            {
                "keyPressCounts", "topKeys", "leftClicks", "middleClicks", "rightClicks", "scrollDistance",
                "peakKps", "peakCps", "keyPresses", "sideBackClicks", "sideForwardClicks", "mouseDistance"
            },
            field => Assert.True(single.TryGetProperty(field, out _), $"单日响应缺少字段 {field}"));
        Assert.All(new[]
            {
                "keyPresses", "sideBackClicks", "sideForwardClicks", "mouseDistance"
            },
            field => Assert.True(data.TryGetProperty(field, out _), $"范围响应缺少字段 {field}"));
        Assert.Equal(data.GetProperty("totalKeyPresses").GetInt32(), data.GetProperty("keyPresses").GetInt32());

        // AC-7.1：范围结果 = 逐日单日 summary.keystats 的聚合（逐键 + 各计数）。
        var expectedKeys = 0;
        var expectedLeft = 0;
        var expectedMiddle = 0;
        var expectedRight = 0;
        var expectedClicks = 0;
        var expectedA = 0;
        var expectedB = 0;
        for (var i = 0; i < 30; i++)
        {
            var day = new DateTime(2026, 8, 1).AddDays(i);
            var perDay = await GetJsonAsync(client, $"/api/v1/pc/summary?date={day:yyyy-MM-dd}");
            var keystats = perDay.GetProperty("data").GetProperty("keystats");
            expectedKeys += keystats.GetProperty("keyPresses").GetInt32();
            expectedLeft += keystats.GetProperty("leftClicks").GetInt32();
            expectedMiddle += keystats.GetProperty("middleClicks").GetInt32();
            expectedRight += keystats.GetProperty("rightClicks").GetInt32();
            expectedClicks += keystats.GetProperty("totalClicks").GetInt32();
            expectedA += keystats.GetProperty("keyPressCounts").GetProperty("A").GetInt32();
            expectedB += keystats.GetProperty("keyPressCounts").GetProperty("B").GetInt32();
        }

        Assert.Equal(expectedKeys, data.GetProperty("totalKeyPresses").GetInt32());
        Assert.Equal(expectedLeft, data.GetProperty("leftClicks").GetInt32());
        Assert.Equal(expectedMiddle, data.GetProperty("middleClicks").GetInt32());
        Assert.Equal(expectedRight, data.GetProperty("rightClicks").GetInt32());
        Assert.Equal(expectedClicks, data.GetProperty("totalClicks").GetInt32());
        Assert.Equal(expectedA, data.GetProperty("keyPressCounts").GetProperty("A").GetInt32());
        Assert.Equal(expectedB, data.GetProperty("keyPressCounts").GetProperty("B").GetInt32());
    }

    [Fact]
    public async Task KeystatsRangeEndpoint_EmptyRangeReturnsZerosNotAnError()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var range = await GetJsonAsync(client, "/api/v1/pc/aggregation/keystats?start=2026-08-01&end=2026-08-05");
        var data = range.GetProperty("data");
        Assert.Equal(0, data.GetProperty("totalKeyPresses").GetInt32());
        Assert.Equal(0, data.GetProperty("totalClicks").GetInt32());
        Assert.Empty(data.GetProperty("keyPressCounts").EnumerateObject());
    }

    /// <summary>每个 factory 绑定一个固定的内存库名（名字必须在所有 scope 间稳定，否则各 scope 各建一个库）。</summary>
    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = $"pc-e2e-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(databaseName));
            });
        });
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, Action<PimDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AwEventEntity WindowEvent(DateTimeOffset timestamp, double duration, string appName) => new()
    {
        Id = Random.Shared.NextInt64(1, long.MaxValue),
        DeviceId = "device-1",
        Timestamp = timestamp,
        Duration = duration,
        EventType = "window",
        AppName = appName,
        AppNameNormalized = AppNameNormalizer.Normalize(appName),
        WindowTitle = "A",
        DataJson = "{}"
    };
}
