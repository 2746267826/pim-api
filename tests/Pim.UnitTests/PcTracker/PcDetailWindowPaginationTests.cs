using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// WO-ISSUES-386-390-20261005 · REQ-10 / REQ-11（#390）：`/pc/detail` 改成按窗口逐段合成之后，
/// **结果必须与改造前的整段合成逐字段一致**（`TotalCount` 精确、翻页不重不漏）。
/// <para>
/// 这里刻意把数据种在业务日边界两侧：cluster 聚类会吸附尾部短事件、`input-minute` 记录是相邻两条
/// 采样的增量，两者都最容易在窗口边界上出错。基准 = <see cref="PcTrackerService.BuildCompleteDetailRecordsAsync"/>
/// （整段合成，改造前后都在用）。
/// </para>
/// </summary>
public sealed class PcDetailWindowPaginationTests
{
    /// <summary>业务日 2026-09-25 起算（业务日边界 = 本地 04:00 = UTC 前一天 20:00）。</summary>
    private static readonly DateTime FirstDay = new(2026, 9, 25);
    private static readonly DateTimeOffset Day1Start = new(2026, 9, 24, 20, 0, 0, TimeSpan.Zero);
    private const string Device = "DESKTOP-WINDOW-1";

    [Fact]
    public async Task WindowedPagination_MatchesFullRangeComposition_AndNeverRepeatsOrDrops()
    {
        await using var db = CreateDb();
        Seed(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var q = Query(FirstDay, FirstDay.AddDays(2));

        // 基准：整段范围一次合成（改造前的 /pc/detail 行为）
        var full = await service.BuildCompleteDetailRecordsAsync(q, CancellationToken.None, includeCrossingRecords: false);
        Assert.True(full.Count > 20, $"种子数据太少，对拍没有意义：{full.Count}");

        // 逐页取回全部记录（pageSize=200 恰为对外上限），与整段合成逐字段比对
        var collected = new List<PcDetailRecord>();
        var page = 1;
        int totalCount;
        int totalPages;
        do
        {
            var resp = await service.QueryCompleteDetailAsync(
                q with { Page = page, PageSize = 200 }, CancellationToken.None);
            totalCount = resp.TotalCount;
            totalPages = resp.TotalPages;
            collected.AddRange(resp.Items);
            page++;
        } while (page <= totalPages && page < 200);

        Assert.Equal(full.Count, totalCount);
        Assert.Equal((int)Math.Ceiling(full.Count / 200.0), totalPages);
        Assert.Equal(full.Count, collected.Count);
        Assert.Equal(full.Select(Signature), collected.Select(Signature));
    }

    [Fact]
    public async Task WindowedPagination_MatchesFullRange_WhenFilteringByCategory()
    {
        // 按分类过滤时计数依赖分类补全（分类跳过必须在这种情况下仍然分类）
        await using var db = CreateDb();
        Seed(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var q = Query(FirstDay, FirstDay.AddDays(2)) with { CategoryName = "其他" };

        var full = await service.BuildCompleteDetailRecordsAsync(q, CancellationToken.None, includeCrossingRecords: false);
        var resp = await service.QueryCompleteDetailAsync(q with { Page = 1, PageSize = 200 }, CancellationToken.None);

        Assert.Equal(full.Count, resp.TotalCount);
        Assert.Equal(full.Take(200).Select(Signature), resp.Items.Select(Signature));
    }

    [Fact]
    public async Task WindowedPagination_MatchesFullRange_WhenAscending()
    {
        await using var db = CreateDb();
        Seed(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var q = Query(FirstDay, FirstDay.AddDays(2)) with { SortDir = "asc" };

        var full = await service.BuildCompleteDetailRecordsAsync(q, CancellationToken.None, includeCrossingRecords: false);
        var resp = await service.QueryCompleteDetailAsync(q with { Page = 1, PageSize = 200 }, CancellationToken.None);

        Assert.Equal(full.Count, resp.TotalCount);
        Assert.Equal(full.Take(200).Select(Signature), resp.Items.Select(Signature));
    }

    /// <summary>
    /// 跨窗口的 `input-minute` 记录（相邻两条采样横跨业务日边界）必须恰好出现一次，
    /// 不能因为两个窗口各补一次邻居采样而重复，也不能整体消失。
    /// </summary>
    [Fact]
    public async Task InputMinuteRecord_AcrossWindowBoundary_AppearsExactlyOnce()
    {
        await using var db = CreateDb();
        Seed(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var q = Query(FirstDay, FirstDay.AddDays(2));

        var rows = new List<PcDetailRecord>();
        for (var page = 1; page <= 50; page++)
        {
            var resp = await service.QueryCompleteDetailAsync(q with { Page = page, PageSize = 200 }, CancellationToken.None);
            if (resp.Items.Count == 0) break;
            rows.AddRange(resp.Items);
        }

        var crossing = rows
            .Where(r => r.RecordType == "input-minute"
                && DateTimeOffset.Parse(r.Start) >= Day1Start.AddMinutes(-30)
                && DateTimeOffset.Parse(r.Start) <= Day1Start.AddMinutes(30))
            .ToList();
        Assert.NotEmpty(crossing);
        Assert.Equal(crossing.Count, crossing.Select(Signature).Distinct().Count());
    }

    /// <summary>
    /// 反面回归（cross review Critical）：某一天的网页全是短页（<c>duration &lt;= 5</c>）、前一天才有主角时，
    /// 整段合成会把这段短页**丢掉**；窗口切分后它单独成窗，收尾会走 <c>FromShortEvents</c> 多合成一条记录。
    /// 窗口计划因此必须把这扇窗并回去。
    /// </summary>
    [Fact]
    public async Task ShortWebPageTail_AfterABoundary_DoesNotInventARecord()
    {
        await using var db = CreateDb();
        var dayA = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);
        AddAw(db, dayA.AddHours(18), 300, "web", "chrome.exe", "https://example.com/long");           // 主角
        AddAw(db, dayA.AddDays(1).AddHours(10), 2, "web", "chrome.exe", "https://example.com/short"); // 次日只有短页
        await db.SaveChangesAsync();

        var service = Service(db);
        var q = Query(new DateTime(2026, 5, 20), new DateTime(2026, 5, 21));
        var full = await service.BuildCompleteDetailRecordsAsync(q, CancellationToken.None, includeCrossingRecords: false);
        var paged = await CollectAllPages(service, q);

        Assert.Equal(1, full.Count);                    // 整段合成：短页被吸附判定拒绝后丢弃
        Assert.Equal(full.Select(Signature), paged.Select(Signature));
    }

    /// <summary>
    /// 同类反面：次日那段短页旁边有一条**非网页**的长事件（普通窗口/afk），它不算 cluster 主角 ——
    /// 判据若只看 <c>Duration &gt; 5</c> 就会误判为「可自洽」。
    /// </summary>
    [Fact]
    public async Task ShortWebPageTail_WithLongNonWebEvent_StillDoesNotInventARecord()
    {
        await using var db = CreateDb();
        var dayA = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);
        AddAw(db, dayA.AddHours(18), 300, "web", "chrome.exe", "https://example.com/long");
        AddAw(db, dayA.AddDays(1).AddHours(9), 60, "window", "notepad.exe", null);                   // 非网页长事件
        AddAw(db, dayA.AddDays(1).AddHours(10), 2, "web", "chrome.exe", "https://example.com/short");
        await db.SaveChangesAsync();

        var service = Service(db);
        var q = Query(new DateTime(2026, 5, 20), new DateTime(2026, 5, 21));
        var full = await service.BuildCompleteDetailRecordsAsync(q, CancellationToken.None, includeCrossingRecords: false);
        var paged = await CollectAllPages(service, q);

        Assert.Equal(full.Select(Signature), paged.Select(Signature));
    }

    /// <summary>
    /// tracker 管道方向相反：整段合成会把尾部短 web-page 逐条吐出，而只剩短页的窗口会把它们并成一条。
    /// </summary>
    [Fact]
    public async Task ShortTrackerWebPageChain_DoesNotGetMergedByWindowBoundary()
    {
        await using var db = CreateDb();
        var dayA = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);
        AddTracker(db, dayA.AddHours(18), 60, "web-page", "chrome.exe", "https://example.com/t-long");
        for (var i = 0; i < 3; i++)
            AddTracker(db, dayA.AddDays(1).AddHours(10).AddSeconds(i * 10), 2, "web-page", "chrome.exe", $"https://example.com/t-short-{i}");
        await db.SaveChangesAsync();

        var service = Service(db);
        var q = Query(new DateTime(2026, 5, 20), new DateTime(2026, 5, 21));
        var full = await service.BuildCompleteDetailRecordsAsync(q, CancellationToken.None, includeCrossingRecords: false);
        var paged = await CollectAllPages(service, q);

        Assert.Equal(4, full.Count);                    // 长页 1 条 + 三条短页各一条
        Assert.Equal(full.Select(Signature), paged.Select(Signature));
    }

    private static async Task<List<PcDetailRecord>> CollectAllPages(PcTrackerService service, DetailQueryParams q)
    {
        var collected = new List<PcDetailRecord>();
        for (var page = 1; page <= 200; page++)
        {
            var resp = await service.QueryCompleteDetailAsync(q with { Page = page, PageSize = 200 }, CancellationToken.None);
            if (resp.Items.Count == 0) break;
            collected.AddRange(resp.Items);
        }
        return collected;
    }

    private static string Signature(PcDetailRecord record) => JsonSerializer.Serialize(record);

    private static DetailQueryParams Query(DateTime from, DateTime to) => new(
        from.ToString("yyyy-MM-dd"),
        to.ToString("yyyy-MM-dd"),
        null, null, null, null, null, null,
        "date", null, 1, 200, View: "interpreted");

    private static void Seed(PimDbContext db)
    {
        for (var day = 0; day < 3; day++)
        {
            var dayStart = Day1Start.AddDays(day);
            // 上午的一条长事件 + 一串短事件（cluster 尾部吸附）
            AddAw(db, dayStart.AddHours(1), 600, "web", "chrome.exe", "https://example.com/a");
            AddAw(db, dayStart.AddHours(2), 3, "web", "chrome.exe", "https://example.com/short-1");
            AddAw(db, dayStart.AddHours(2).AddSeconds(10), 4, "web", "chrome.exe", "https://example.com/short-2");
            AddAw(db, dayStart.AddHours(2).AddSeconds(20), 2, "web", "chrome.exe", "https://example.com/short-3");
            AddAw(db, dayStart.AddHours(3), 900, "window", "Code.exe", null);

            // 业务日边界**两侧**：长事件跨过边界，短事件紧贴边界（边界必须落在这条链之外）
            AddAw(db, dayStart.AddHours(23).AddMinutes(55), 600, "web", "chrome.exe", "https://example.com/crossing");
            AddAw(db, dayStart.AddHours(24).AddSeconds(20), 3, "web", "chrome.exe", "https://example.com/after-boundary");
            AddAw(db, dayStart.AddHours(24).AddSeconds(40), 4, "web", "chrome.exe", "https://example.com/after-boundary-2");

            // tracker：短 web-page + 长 web-page（MergeShortWebPages）
            AddTracker(db, dayStart.AddHours(4), 3, "web-page", "chrome.exe", "https://example.com/t-short");
            AddTracker(db, dayStart.AddHours(4).AddSeconds(10), 120, "web-page", "chrome.exe", "https://example.com/t-long");
            AddTracker(db, dayStart.AddHours(5), 300, "window", "Code.exe", null);

            // 采样：一条在边界前、一条在边界后 —— input-minute 记录跨窗口
            AddSample(db, dayStart.AddMinutes(-10), keyPresses: 100);
            AddSample(db, dayStart.AddMinutes(5), keyPresses: 160);
            foreach (var minute in new[] { 60, 65, 70, 75 })
                AddSample(db, dayStart.AddMinutes(minute), keyPresses: 200 + minute);
        }
    }

    private static void AddAw(PimDbContext db, DateTimeOffset timestamp, double duration, string eventType, string app, string? url)
        => db.Set<AwEventEntity>().Add(new AwEventEntity
        {
            DeviceId = Device,
            Timestamp = timestamp,
            Duration = duration,
            EventType = eventType,
            AppName = app,
            AppNameNormalized = app.Replace(".exe", string.Empty),
            WindowTitle = url ?? "Window",
            DataJson = url is null ? "{}" : $"{{\"url\":\"{url}\",\"title\":\"{url}\"}}",
        });

    private static void AddTracker(PimDbContext db, DateTimeOffset timestamp, double duration, string eventType, string app, string? url)
        => db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = Device,
            Timestamp = timestamp,
            Duration = duration,
            EventType = eventType,
            AppName = app,
            WindowTitle = url ?? "Window",
            Url = url,
            Domain = url is null ? null : "example.com",
            RawJson = "{}",
            Date = timestamp.UtcDateTime.Date,
        });

    private static void AddSample(PimDbContext db, DateTimeOffset sampledAt, int keyPresses)
        => db.Set<KeystatsSampleEntity>().Add(new KeystatsSampleEntity
        {
            PimDeviceId = Device,
            SampledAtUtc = sampledAt,
            StatsDate = sampledAt.UtcDateTime.Date,
            KeyPresses = keyPresses,
            KeyCountsJson = "{\"A\":10}",
            RawJson = "{}",
        });

    private static PcTrackerService Service(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }
}
