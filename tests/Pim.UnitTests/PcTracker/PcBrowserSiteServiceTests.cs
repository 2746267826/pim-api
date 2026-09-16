using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.PcTracker;

public sealed class PcBrowserSiteServiceTests
{
    private static SiteEventUploadDto Focus(string host, long startMs, long endMs, string date)
        => new() { Kind = "focus", Host = host, StartMs = startMs, EndMs = endMs, Date = date };
    private static SiteEventUploadDto Tick(string host, long startMs, long durationMs, string date)
        => new() { Kind = "tick", Host = host, StartMs = startMs, DurationMs = durationMs, Date = date };
    private static SiteEventUploadDto Visit(string host, string date)
        => new() { Kind = "visit", Host = host, Date = date };
    private static SiteEventUploadDto Run(string host, string date, long durationMs)
        => new() { Kind = "run", Host = host, Date = date, DurationMs = durationMs };

    private static PcBrowserSiteService CreateService(Pim.Infrastructure.Data.PimDbContext db)
        => new(db);

    [Fact]
    public async Task Upload_FocusEvent_AccumulatesDailyRow()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        var req = new SiteEventsUploadRequest
        {
            DeviceId = "pc-1",
            Events = { Focus("GitHub.com", 1000, 6000, "2026-09-01") },
        };

        var saved = await svc.UploadAsync(req, CancellationToken.None);

        Assert.Equal(1, saved);
        var row = db.Set<PcBrowserSiteDailyEntity>().Single();
        Assert.Equal("pc-1", row.DeviceId);
        Assert.Equal("2026-09-01", row.Date);
        Assert.Equal("github.com", row.Host);
        Assert.Equal(5000, row.FocusMs);
    }

    [Fact]
    public async Task Upload_SecondBatchOnSameKey_Accumulates()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        await svc.UploadAsync(new SiteEventsUploadRequest { DeviceId = "pc-1", Events = { Focus("github.com", 0, 1000, "2026-09-01"), Visit("github.com", "2026-09-01") } }, CancellationToken.None);
        await svc.UploadAsync(new SiteEventsUploadRequest { DeviceId = "pc-1", Events = { Focus("github.com", 2000, 5000, "2026-09-01") } }, CancellationToken.None);

        var row = db.Set<PcBrowserSiteDailyEntity>().Single();
        Assert.Equal(4000, row.FocusMs);
        Assert.Equal(1, row.VisitCount);
    }

    [Fact]
    public async Task Upload_TickInsertsTimelineRows_RunMediaAccumulate()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        var req = new SiteEventsUploadRequest
        {
            DeviceId = "pc-1",
            Events =
            {
                Tick("github.com", 1_000, 60_000, "2026-09-01"),
                Tick("github.com", 2_000, 30_000, "2026-09-01"),
                Run("github.com", "2026-09-01", 120_000),
                new SiteEventUploadDto { Kind = "media", Host = "youtube.com", Date = "2026-09-01", DurationMs = 5_000 },
            },
        };

        await svc.UploadAsync(req, CancellationToken.None);

        Assert.Equal(2, db.Set<PcBrowserSiteTickEntity>().Count());
        var gh = db.Set<PcBrowserSiteDailyEntity>().Single(r => r.Host == "github.com");
        Assert.Equal(120_000, gh.RunMs);
        var yt = db.Set<PcBrowserSiteDailyEntity>().Single(r => r.Host == "youtube.com");
        Assert.Equal(5_000, yt.MediaMs);
    }

    [Fact]
    public async Task Upload_InvalidEvents_Throw()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadAsync(
            new SiteEventsUploadRequest { DeviceId = "", Events = { Visit("github.com", "2026-09-01") } }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadAsync(
            new SiteEventsUploadRequest { DeviceId = "pc-1", Events = { new SiteEventUploadDto { Kind = "bogus", Host = "github.com" } } }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadAsync(
            new SiteEventsUploadRequest { DeviceId = "pc-1", Events = { Focus("github.com", 5000, 1000, "2026-09-01") } }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UploadAsync(
            new SiteEventsUploadRequest { DeviceId = "pc-1", Events = { Focus("github.com", 1000, 2000, "2026/09/01") } }, CancellationToken.None));
    }

    [Fact]
    public async Task Queries_DailyTimelineSummary()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        await svc.UploadAsync(new SiteEventsUploadRequest
        {
            DeviceId = "pc-1",
            Events =
            {
                Focus("github.com", 0, 60_000, "2026-09-01"),
                Focus("github.com", 0, 30_000, "2026-09-02"),
                Focus("youtube.com", 0, 10_000, "2026-09-01"),
                Visit("github.com", "2026-09-01"),
                Tick("github.com", 1_000, 60_000, "2026-09-01"),
                Tick("youtube.com", 2_000, 10_000, "2026-09-01"),
            },
        }, CancellationToken.None);

        var daily = await svc.GetDailyAsync(new SiteDailyQuery { From = "2026-09-01", To = "2026-09-02" }, CancellationToken.None);
        Assert.Equal(3, daily.Count);
        Assert.Equal(60_000, daily.First(r => r.Date == "2026-09-01" && r.Host == "github.com").FocusMs);

        var timeline = await svc.GetTimelineAsync("2026-09-01", CancellationToken.None);
        Assert.Equal(2, timeline.Count);
        Assert.True(timeline[0].StartMs <= timeline[1].StartMs);

        var summary = await svc.GetSummaryAsync("2026-09-01", "2026-09-01", CancellationToken.None);
        Assert.Equal(70_000, summary.TotalFocusMs);
        Assert.Equal(1, summary.TotalVisits);
        Assert.Equal(2, summary.SiteCount);
        Assert.Equal("github.com", summary.TopHosts[0].Host);
    }

    [Fact]
    public async Task Import_RecordExportJson_OverwritesByKey()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        // tt4b 记录页导出：focus 为秒数字符串
        const string content = """
[
  {"host":"github.com","date":"2026-08-01","alias":"GitHub","focus":"3600","time":12},
  {"host":"youtube.com","date":"2026-08-01","focus":"1:30:00","time":3}
]
""";
        var result = await svc.ImportAsync(new SiteImportRequest { Content = content, Mode = "overwrite" }, CancellationToken.None);

        Assert.Equal(2, result.Rows);
        Assert.Equal(1, result.Dates);
        Assert.Equal("record-json", result.Format);
        var rows = db.Set<PcBrowserSiteDailyEntity>().ToList();
        Assert.Equal(3600_000, rows.Single(r => r.Host == "github.com").FocusMs);
        Assert.Equal(5400_000, rows.Single(r => r.Host == "youtube.com").FocusMs);
        Assert.Equal(12, rows.Single(r => r.Host == "github.com").VisitCount);
    }

    [Fact]
    public async Task Import_BackupMarkdown_ParsesEmbeddedJson()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        var rows = """[{"host":"github.com","date":"2026-08-02","focus":123456,"time":9,"run":1000,"media":2000}]""";
        var content = $"<!-- {{\"version\":1,\"ts\":123}} -->\n<!-- {rows} -->\n|Date|Domain/URL|\n|----|----|\n";

        var result = await svc.ImportAsync(new SiteImportRequest { Content = content, Mode = "overwrite" }, CancellationToken.None);

        Assert.Equal(1, result.Rows);
        Assert.Equal("backup-markdown", result.Format);
        var row = db.Set<PcBrowserSiteDailyEntity>().Single();
        Assert.Equal(123_456, row.FocusMs);
        Assert.Equal(9, row.VisitCount);
        Assert.Equal(1_000, row.RunMs);
        Assert.Equal(2_000, row.MediaMs);
    }

    [Fact]
    public async Task Import_AddMode_AccumulatesOnExisting()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        await svc.UploadAsync(new SiteEventsUploadRequest { DeviceId = "__imported__", Events = { Focus("github.com", 0, 1_000, "2026-08-01"), Visit("github.com", "2026-08-01") } }, CancellationToken.None);

        const string content = """[{"host":"github.com","date":"2026-08-01","focus":"10","time":1}]""";
        await svc.ImportAsync(new SiteImportRequest { Content = content, Mode = "add" }, CancellationToken.None);

        var row = db.Set<PcBrowserSiteDailyEntity>().Single();
        Assert.Equal(11_000, row.FocusMs);
        Assert.Equal(2, row.VisitCount);
    }

    [Fact]
    public async Task Import_SavesAliasIntoMeta()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        const string content = """[{"host":"github.com","date":"2026-08-01","alias":"GitHub","cate":"开发","focus":"10","time":1}]""";

        await svc.ImportAsync(new SiteImportRequest { Content = content }, CancellationToken.None);

        var meta = db.Set<PcBrowserSiteMetaEntity>().Single();
        Assert.Equal("GitHub", meta.Alias);
        Assert.Equal("开发", meta.Cate);
    }

    [Fact]
    public async Task Import_InvalidContent_Throws()
    {
        await using var db = ServiceTestBase.CreateDb();
        var svc = CreateService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ImportAsync(new SiteImportRequest { Content = "not json at all" }, CancellationToken.None));
    }
}
