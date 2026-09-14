using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 手机质量报告的"信号要说真话"回归测试（#244 汇总断流仍报正常 / #245 元数据口径漂移）。
/// </summary>
public sealed class MobileQualitySignalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T04:00:00Z");

    private static MobileQualityService Quality(PimDbContext db)
        => new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));

    private static MobileUsageEventEntity Event(string packageName, DateTimeOffset at) => new()
    {
        UserId = MobileTestHelpers.UserId,
        DeviceId = "android-main",
        PackageName = packageName,
        EventType = "MOVE_TO_FOREGROUND",
        EventTimestampUtc = at,
        SourceWindowStartUtc = at.AddMinutes(-1),
        SourceWindowEndUtc = at.AddMinutes(1),
        CollectedAtUtc = at.AddMinutes(1),
        RawJson = "{}",
        QualityFlagsJson = "[]",
        CreatedAt = at
    };

    private static MobileUsageSummaryEntity Summary(string packageName, DateTimeOffset windowEnd, long ms) => new()
    {
        UserId = MobileTestHelpers.UserId,
        DeviceId = "android-main",
        PackageName = packageName,
        WindowStartUtc = windowEnd.AddHours(-2),
        WindowEndUtc = windowEnd,
        TotalTimeVisibleMs = ms,
        LastTimeUsedUtc = windowEnd,
        SourceKind = "usage-stats-fallback",
        RawJson = "{}",
        QualityFlagsJson = "[]",
        CreatedAt = windowEnd,
        UpdatedAt = windowEnd
    };

    // ===================== #244 汇总新鲜度 =====================

    [Fact]
    public async Task Quality_FlagsSummaryPipelineThatStalledWhileEventsKeepArriving()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", Now.AddHours(-2)));
        // 汇总停在 13 小时前（超过 4 小时告警线），事件仍在入库
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", Now.AddHours(-13), 60_000));
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-1), Now, CancellationToken.None);

        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal(PimHealthStatus.Warning, usage.Status);
        // 滞后以"所选窗口的结束时刻"（此处为 now）为评估点：now - (now-13h) = 13h
        Assert.Equal("13.0", usage.Details["summaryLagHours"]);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "mobile-usage-summary-stale" && issue.Severity == PimHealthStatus.Warning);
    }

    [Fact]
    public async Task Quality_DoesNotFlagHistoricalRangeCoveredByLaterSummary()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 历史窗口：范围内有事件、范围内的汇总也齐全；之后（窗口之外）还有更新汇总
        var historicalEnd = Now.AddDays(-3);
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", historicalEnd.AddHours(-1)));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", historicalEnd, 60_000));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", Now.AddHours(-1), 60_000));
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-3).AddHours(-6), historicalEnd, CancellationToken.None);

        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal("0.0", usage.Details["summaryLagHours"]);
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "mobile-usage-summary-stale");
    }

    [Fact]
    public async Task Quality_FlagsStaleSummaryInsideHistoricalRangeEvenWhenNewerSummaryExists()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var historicalEnd = Now.AddDays(-3);
        // 历史窗口里事件在进，但该窗口内的汇总停在 30 小时前；窗口之外另有一条新汇总
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", historicalEnd.AddHours(-1)));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", historicalEnd.AddHours(-30), 60_000));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", Now.AddHours(-1), 60_000));
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(historicalEnd.AddHours(-48), historicalEnd, CancellationToken.None);

        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal("30.0", usage.Details["summaryLagHours"]);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "mobile-usage-summary-stale" && issue.Severity == PimHealthStatus.Critical);
    }

    [Fact]
    public async Task Quality_ReportsCriticalWhenSummaryPipelineIsBrokenForMoreThanADay()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", Now.AddHours(-2)));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", Now.AddHours(-30), 60_000));
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-2), Now, CancellationToken.None);

        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal(PimHealthStatus.Critical, usage.Status);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "mobile-usage-summary-stale" && issue.Severity == PimHealthStatus.Critical);
    }

    [Fact]
    public async Task Quality_WarnsWhenEventsArriveWithoutAnySummaryEverReceived()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 从未收到过任何汇总：可能只是窗口内一直有事件、用不到兜底汇总 ⇒ 报警告而不是红线
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", Now.AddHours(-2)));
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-1), Now, CancellationToken.None);

        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal(PimHealthStatus.Warning, usage.Status);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "mobile-usage-summary-stale" && issue.Severity == PimHealthStatus.Warning);
    }

    [Fact]
    public async Task Quality_ReportsCriticalWhenSummaryPipelineStoppedWithoutAnySummaryInRange()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 历史上收到过汇总（说明兜底链路本来是通的），但所选窗口内一条都没有
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", Now.AddHours(-2)));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", Now.AddDays(-5), 60_000));
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-1), Now, CancellationToken.None);

        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal(PimHealthStatus.Critical, usage.Status);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "mobile-usage-summary-stale" && issue.Severity == PimHealthStatus.Critical);
    }

    [Fact]
    public async Task Quality_DoesNotFlagStaleSummaryWhenSummaryKeepsUpWithEvents()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", Now.AddHours(-2)));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", Now.AddHours(-2), 60_000));
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-1), Now, CancellationToken.None);

        var usage = Assert.Single(quality.Components, component => component.Key == "mobile-usage-coverage");
        Assert.Equal("2.0", usage.Details["summaryLagHours"]);
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "mobile-usage-summary-stale");
        // 唯一剩下的告警是"该范围只有 fallback 汇总"，与汇总新鲜度无关
        Assert.Equal(PimHealthStatus.Warning, usage.Status);
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-usage-fallback-only");
    }

    // ===================== #245 应用元数据口径 =====================

    [Fact]
    public async Task Quality_MissingAppMetadataUsesOneComparableScope()
    {
        await using var db = MobileTestHelpers.CreateDb();
        // 该窗口内用了两个包，其中只有一个有元数据（且元数据来自另一台设备）
        db.Set<MobileUsageEventEntity>().AddRange(
            Event("com.example.with-metadata", Now.AddHours(-2)),
            Event("com.example.missing", Now.AddHours(-1)));
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-other",
            PackageName = "com.example.with-metadata",
            DisplayName = "With Metadata",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-1), Now, CancellationToken.None);

        var metadata = Assert.Single(quality.Components, component => component.Key == "mobile-app-metadata");
        Assert.Equal(PimHealthStatus.Warning, metadata.Status);
        Assert.Equal("2", metadata.Details["usedPackageCount"]);
        Assert.Equal("1", metadata.Details["missingAppMetadataCount"]);
        Assert.Equal("com.example.missing", metadata.Details["missingPackages"]);
        Assert.Contains(quality.Issues, issue => issue.Code == "mobile-app-metadata-missing");
    }

    [Fact]
    public async Task GetMissingAppMetadataAsync_ListsPackagesWithoutCatalogRows()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageEventEntity>().AddRange(
            Event("com.tencent.mm", Now.AddHours(-4)),
            Event("com.tencent.mm", Now.AddHours(-3)),
            Event("com.taobao.taobao", Now.AddHours(-2)),
            Event("com.example.known", Now.AddHours(-1)));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.unknown.from.summary", Now.AddHours(-2), 120_000));
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.known",
            DisplayName = "Known",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var response = await Quality(db).GetMissingAppMetadataAsync(
            Now.AddDays(-1),
            Now,
            "android-main",
            ct: CancellationToken.None);

        Assert.Equal(3, response.MissingPackageCount);
        Assert.Equal(
            ["com.taobao.taobao", "com.tencent.mm", "com.unknown.from.summary"],
            response.Packages.Select(package => package.PackageName).OrderBy(name => name, StringComparer.Ordinal));
        var wechat = Assert.Single(response.Packages, package => package.PackageName == "com.tencent.mm");
        Assert.Equal(2, wechat.EventCount);
        var summaryOnly = Assert.Single(response.Packages, package => package.PackageName == "com.unknown.from.summary");
        Assert.Equal(0, summaryOnly.EventCount);
        Assert.Equal(120_000, summaryOnly.ForegroundMs);
    }

    [Fact]
    public async Task Quality_AppMetadataHealthyWhenEveryUsedPackageHasMetadata()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileUsageEventEntity>().Add(Event("com.example.app", Now.AddHours(-2)));
        db.Set<MobileUsageSummaryEntity>().Add(Summary("com.example.app", Now.AddHours(-2), 60_000));
        db.Set<MobileAppCatalogEntity>().Add(new MobileAppCatalogEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = "com.example.app",
            DisplayName = "Example",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var quality = await Quality(db).GetQualityAsync(Now.AddDays(-1), Now, CancellationToken.None);

        var metadata = Assert.Single(quality.Components, component => component.Key == "mobile-app-metadata");
        Assert.Equal(PimHealthStatus.Healthy, metadata.Status);
        Assert.Equal("0", metadata.Details["missingAppMetadataCount"]);
        Assert.Equal(string.Empty, metadata.Details["missingPackages"]);
    }
}
