using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileTimelineBlockServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
    private static readonly DateTimeOffset RangeStart = DateTimeOffset.Parse("2026-07-07T00:00:00Z");
    private static readonly DateTimeOffset RangeEnd = DateTimeOffset.Parse("2026-07-08T00:00:00Z");

    [Fact]
    public async Task GetBlocksAsync_GroupsNearbyItemsAndFiltersDefaultNoiseAndShortEvents()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var qqSessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var wechatSessionId = Guid.Parse("10000000-0000-0000-0000-000000000002");

        db.Set<MobileAppCatalogEntity>().AddRange(
            Catalog("com.tencent.mobileqq", "QQ", "聊天"),
            Catalog("com.tencent.mm", "微信", "聊天"),
            Catalog("com.android.systemui", "系统界面", "工具/系统", isSystemApp: true),
            Catalog("com.reader.news", "每日阅读", "学习"));
        db.Set<MobileAppCatalogOverrideEntity>().AddRange(
            Override("com.tencent.mobileqq", "聊天"),
            Override("com.tencent.mm", "聊天"),
            Override("com.android.systemui", "工具/系统", isSystemNoise: true),
            Override("com.reader.news", "学习"));
        db.Set<MobileUsageSessionEntity>().AddRange(
            Session(qqSessionId, "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-07T10:00:00Z"), 300, "[\"前台事件\"]"),
            Session(wechatSessionId, "com.tencent.mm", DateTimeOffset.Parse("2026-07-07T10:06:00Z"), 240, "[\"跨天修正\"]"),
            Session(Guid.Parse("10000000-0000-0000-0000-000000000003"), "com.android.systemui", DateTimeOffset.Parse("2026-07-07T10:04:00Z"), 120),
            Session(Guid.Parse("10000000-0000-0000-0000-000000000004"), "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-07T10:30:00Z"), 1),
            Session(Guid.Parse("10000000-0000-0000-0000-000000000005"), "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-07T10:40:00Z"), 300, userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));
        db.Set<MobileUsageSummaryEntity>().Add(Summary(
            "com.reader.news",
            DateTimeOffset.Parse("2026-07-07T09:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T09:30:00Z"),
            900,
            "[\"仅汇总数据\"]"));
        await db.SaveChangesAsync();

        var page = await Service(db).GetBlocksAsync(Query(), CancellationToken.None);

        Assert.Null(page.NextCursor);
        Assert.False(page.HasMore);
        Assert.Equal(2, page.Items.Count);

        var social = page.Items[0];
        Assert.Equal("聊天", social.LifeCategory);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T10:00:00Z"), social.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T10:10:00Z"), social.EndUtc);
        Assert.Equal("2026-07-07 18:00", social.LocalStart);
        Assert.Equal("2026-07-07 18:10", social.LocalEnd);
        Assert.Equal(540, social.ForegroundSeconds);
        Assert.Equal(2, social.SessionCount);
        Assert.Equal(2, social.AppCount);
        Assert.False(social.IncludesSystemNoise);
        Assert.Equal(540, social.SourceMix!["events"]);
        Assert.Contains("前台事件", social.QualityFlags);
        Assert.Contains("跨天修正", social.QualityFlags);
        Assert.DoesNotContain(social.TopApps, app => app.PackageName == "com.android.systemui");
        Assert.Collection(
            social.TopApps,
            app =>
            {
                Assert.Equal("com.tencent.mobileqq", app.PackageName);
                Assert.Equal("QQ", app.DisplayName);
                Assert.Equal(300, app.ForegroundSeconds);
            },
            app =>
            {
                Assert.Equal("com.tencent.mm", app.PackageName);
                Assert.Equal("微信", app.DisplayName);
                Assert.Equal(240, app.ForegroundSeconds);
            });

        var fallback = page.Items[1];
        Assert.Equal("学习", fallback.LifeCategory);
        Assert.Equal(900, fallback.ForegroundSeconds);
        Assert.Equal(1, fallback.SessionCount);
        Assert.Equal(900, fallback.SourceMix!["fallback"]);
        Assert.Contains("仅汇总数据", fallback.QualityFlags);
    }

    [Fact]
    public async Task GetBlocksAsync_CursorPaginatesDescendingAndClampsPageSize()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileAppCatalogEntity>().Add(Catalog("com.tencent.mobileqq", "QQ", "聊天"));
        db.Set<MobileAppCatalogOverrideEntity>().Add(Override("com.tencent.mobileqq", "聊天"));

        for (var i = 0; i < 205; i++)
        {
            db.Set<MobileUsageSessionEntity>().Add(Session(
                Guid.Parse($"20000000-0000-0000-0000-{i + 1:000000000000}"),
                "com.tencent.mobileqq",
                DateTimeOffset.Parse("2026-07-01T00:00:00Z").AddMinutes(i * 20),
                300));
        }

        await db.SaveChangesAsync();

        var service = Service(db);
        var firstPage = await service.GetBlocksAsync(Query(
            start: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            end: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            pageSize: 2), CancellationToken.None);
        var secondPage = await service.GetBlocksAsync(Query(
            start: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            end: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            cursor: firstPage.NextCursor,
            pageSize: 2), CancellationToken.None);
        var defaultPage = await service.GetBlocksAsync(Query(
            start: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            end: DateTimeOffset.Parse("2026-07-04T00:00:00Z")), CancellationToken.None);
        var cappedPage = await service.GetBlocksAsync(Query(
            start: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            end: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            pageSize: 999), CancellationToken.None);

        Assert.Equal(2, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.NotNull(firstPage.NextCursor);
        Assert.True(firstPage.Items[0].StartUtc > firstPage.Items[1].StartUtc);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.DoesNotContain(secondPage.Items, item => firstPage.Items.Select(first => first.Id).Contains(item.Id));
        Assert.True(secondPage.Items[0].StartUtc < firstPage.Items[1].StartUtc);
        Assert.Equal(50, defaultPage.Items.Count);
        Assert.Equal(200, cappedPage.Items.Count);
        Assert.True(cappedPage.HasMore);
    }

    [Fact]
    public async Task GetBlocksAsync_PageNumberPaginatesDescendingAndReturnsTotals()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileAppCatalogEntity>().Add(Catalog("com.tencent.mobileqq", "QQ", "聊天"));
        db.Set<MobileAppCatalogOverrideEntity>().Add(Override("com.tencent.mobileqq", "聊天"));

        for (var i = 0; i < 5; i++)
        {
            db.Set<MobileUsageSessionEntity>().Add(Session(
                Guid.Parse($"21000000-0000-0000-0000-{i + 1:000000000000}"),
                "com.tencent.mobileqq",
                DateTimeOffset.Parse("2026-07-07T00:00:00Z").AddHours(i),
                300));
        }

        await db.SaveChangesAsync();

        var page = await Service(db).GetBlocksAsync(Query(page: 2, pageSize: 2), CancellationToken.None);

        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasMore);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T02:00:00Z"), page.Items[0].StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T01:00:00Z"), page.Items[1].StartUtc);
    }

    [Fact]
    public async Task GetBlocksAsync_AppliesPackageCategorySourceAndNoiseOptions()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileAppCatalogEntity>().AddRange(
            Catalog("com.tencent.mobileqq", "QQ", "聊天"),
            Catalog("com.android.systemui", "系统界面", "工具/系统", isSystemApp: true));
        db.Set<MobileAppCatalogOverrideEntity>().AddRange(
            Override("com.tencent.mobileqq", "聊天"),
            Override("com.android.systemui", "工具/系统", isSystemNoise: true));
        db.Set<MobileUsageSessionEntity>().AddRange(
            Session(Guid.Parse("30000000-0000-0000-0000-000000000001"), "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-07T10:00:00Z"), 300),
            Session(Guid.Parse("30000000-0000-0000-0000-000000000002"), "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-07T10:30:00Z"), 1),
            Session(Guid.Parse("30000000-0000-0000-0000-000000000003"), "com.android.systemui", DateTimeOffset.Parse("2026-07-07T10:40:00Z"), 120));
        db.Set<MobileUsageSummaryEntity>().Add(Summary(
            "com.tencent.mobileqq",
            DateTimeOffset.Parse("2026-07-07T09:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T09:20:00Z"),
            600));
        await db.SaveChangesAsync();

        var service = Service(db);

        var eventsOnly = await service.GetBlocksAsync(Query(packageName: "com.tencent.mobileqq", source: "events"), CancellationToken.None);
        var fallbackOnly = await service.GetBlocksAsync(Query(packageName: "com.tencent.mobileqq", source: "fallback"), CancellationToken.None);
        var toolsWithNoise = await service.GetBlocksAsync(Query(
            lifeCategory: "工具/系统",
            includeSystemNoise: true,
            minDurationSeconds: 0), CancellationToken.None);
        var qqWithShortEvents = await service.GetBlocksAsync(Query(
            packageName: "com.tencent.mobileqq",
            source: "events",
            minDurationSeconds: 0), CancellationToken.None);

        var eventsBlock = Assert.Single(eventsOnly.Items);
        Assert.Equal("events", Assert.Single(eventsBlock.SourceMix!).Key);
        Assert.Equal(300, eventsBlock.ForegroundSeconds);

        var fallbackBlock = Assert.Single(fallbackOnly.Items);
        Assert.Equal("fallback", Assert.Single(fallbackBlock.SourceMix!).Key);
        Assert.Equal(600, fallbackBlock.ForegroundSeconds);

        var toolsBlock = Assert.Single(toolsWithNoise.Items);
        Assert.Equal("工具/系统", toolsBlock.LifeCategory);
        Assert.True(toolsBlock.IncludesSystemNoise);
        Assert.Equal("系统界面", Assert.Single(toolsBlock.TopApps).DisplayName);

        Assert.Equal(2, qqWithShortEvents.Items.Count);
        Assert.Contains(qqWithShortEvents.Items, block => block.ForegroundSeconds == 1);
    }

    [Fact]
    public async Task GetBlocksAsync_ProratesPartiallyOverlappingFallbackSummaries()
    {
        await using var db = MobileTestHelpers.CreateDb();
        db.Set<MobileAppCatalogEntity>().Add(Catalog("com.tencent.mobileqq", "QQ", "聊天"));
        db.Set<MobileAppCatalogOverrideEntity>().Add(Override("com.tencent.mobileqq", "聊天"));
        db.Set<MobileUsageSummaryEntity>().Add(Summary(
            "com.tencent.mobileqq",
            DateTimeOffset.Parse("2026-07-07T09:00:00Z"),
            DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
            3600));
        await db.SaveChangesAsync();

        var page = await Service(db).GetBlocksAsync(Query(
            start: DateTimeOffset.Parse("2026-07-07T09:30:00Z"),
            end: DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
            source: "fallback"), CancellationToken.None);

        var block = Assert.Single(page.Items);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T09:30:00Z"), block.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T10:00:00Z"), block.EndUtc);
        Assert.Equal(1800, block.ForegroundSeconds);
        Assert.Equal(1800, block.SourceMix!["fallback"]);
    }

    [Fact]
    public async Task Drilldown_ReconstructsBlockSessionsAndReturnsSessionEventsForCurrentUser()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var sessionId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var otherSessionId = Guid.Parse("40000000-0000-0000-0000-000000000002");

        db.Set<MobileAppCatalogEntity>().AddRange(
            Catalog("com.tencent.mobileqq", "QQ", "聊天"),
            Catalog("com.tencent.mm", "微信", "聊天"));
        db.Set<MobileAppCatalogOverrideEntity>().AddRange(
            Override("com.tencent.mobileqq", "聊天"),
            Override("com.tencent.mm", "聊天"));
        db.Set<MobileUsageSessionEntity>().AddRange(
            Session(sessionId, "com.tencent.mobileqq", DateTimeOffset.Parse("2026-07-07T10:00:00Z"), 300, "[\"前台事件\"]"),
            Session(otherSessionId, "com.tencent.mm", DateTimeOffset.Parse("2026-07-07T10:06:00Z"), 240));
        db.Set<MobileUsageEventEntity>().AddRange(
            Event("com.tencent.mobileqq", "ACTIVITY_RESUMED", DateTimeOffset.Parse("2026-07-07T10:00:00Z"), "MainActivity", "{\"事件\":\"打开\"}"),
            Event("com.tencent.mobileqq", "ACTIVITY_PAUSED", DateTimeOffset.Parse("2026-07-07T10:05:00Z"), "MainActivity", "{\"事件\":\"离开\"}"),
            Event("com.tencent.mobileqq", "ACTIVITY_RESUMED", DateTimeOffset.Parse("2026-07-07T10:01:00Z"), "OtherUserActivity", "{\"事件\":\"其他用户\"}", userId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));
        await db.SaveChangesAsync();

        var service = Service(db);
        var block = Assert.Single((await service.GetBlocksAsync(Query(), CancellationToken.None)).Items);

        var sessions = await service.GetSessionsForBlockAsync(block.Id, Query(), CancellationToken.None);
        var events = await service.GetSessionEventsAsync(sessions[0].Id, CancellationToken.None);

        Assert.Collection(
            sessions,
            session =>
            {
                Assert.Equal(sessionId.ToString("N"), session.Id);
                Assert.Equal("QQ", session.DisplayName);
                Assert.Equal("聊天", session.LifeCategory);
                Assert.Equal("events", session.Source);
                Assert.Contains("前台事件", session.QualityFlags);
            },
            session =>
            {
                Assert.Equal(otherSessionId.ToString("N"), session.Id);
                Assert.Equal("微信", session.DisplayName);
            });
        Assert.Collection(
            events,
            item =>
            {
                Assert.Equal(sessionId.ToString("N"), item.SessionId);
                Assert.Equal("ACTIVITY_RESUMED", item.EventType);
                Assert.Equal("{\"事件\":\"打开\"}", item.RawJson);
            },
            item =>
            {
                Assert.Equal("ACTIVITY_PAUSED", item.EventType);
                Assert.Equal("{\"事件\":\"离开\"}", item.RawJson);
            });
    }

    private static MobileTimelineBlockService Service(PimDbContext db)
    {
        var currentUser = MobileTestHelpers.CurrentUser();
        return new MobileTimelineBlockService(
            db,
            currentUser,
            MobileTestHelpers.Time(Now),
            new MobileAppClassificationService(db, currentUser));
    }

    private static MobileAnalyticsQueryRequest Query(
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        string? deviceId = "android-main",
        string? lifeCategory = null,
        string? packageName = null,
        string? source = null,
        bool? includeSystemNoise = null,
        int? minDurationSeconds = null,
        string? cursor = null,
        int? page = null,
        int? pageSize = null)
        => new(
            start ?? RangeStart,
            end ?? RangeEnd,
            "Asia/Shanghai",
            deviceId,
            lifeCategory,
            packageName,
            source,
            includeSystemNoise,
            minDurationSeconds,
            null,
            cursor,
            page,
            pageSize);

    private static MobileAppCatalogEntity Catalog(
        string packageName,
        string displayName,
        string category,
        bool isSystemApp = false)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = packageName,
            DisplayName = displayName,
            Category = category,
            IsSystemApp = isSystemApp,
            UpdatedAt = Now
        };

    private static MobileAppCatalogOverrideEntity Override(
        string packageName,
        string lifeCategory,
        bool isSystemNoise = false)
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            PackageName = packageName,
            LifeCategory = lifeCategory,
            IsSystemNoise = isSystemNoise,
            HideShortEvents = false,
            UpdatedAt = Now
        };

    private static MobileUsageSessionEntity Session(
        Guid id,
        string packageName,
        DateTimeOffset start,
        int seconds,
        string qualityFlagsJson = "[]",
        Guid? userId = null)
        => new()
        {
            Id = id,
            UserId = userId ?? MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = packageName,
            StartUtc = start,
            EndUtc = start.AddSeconds(seconds),
            DurationMs = seconds * 1000L,
            QualityFlagsJson = qualityFlagsJson
        };

    private static MobileUsageSummaryEntity Summary(
        string packageName,
        DateTimeOffset start,
        DateTimeOffset end,
        int seconds,
        string qualityFlagsJson = "[]")
        => new()
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = packageName,
            WindowStartUtc = start,
            WindowEndUtc = end,
            TotalTimeVisibleMs = seconds * 1000L,
            SourceKind = "fallback",
            QualityFlagsJson = qualityFlagsJson,
            UpdatedAt = Now
        };

    private static MobileUsageEventEntity Event(
        string packageName,
        string eventType,
        DateTimeOffset timestamp,
        string className,
        string rawJson,
        Guid? userId = null)
        => new()
        {
            UserId = userId ?? MobileTestHelpers.UserId,
            DeviceId = "android-main",
            PackageName = packageName,
            EventType = eventType,
            EventTimestampUtc = timestamp,
            ClassName = className,
            SourceWindowStartUtc = timestamp.AddMinutes(-1),
            SourceWindowEndUtc = timestamp.AddMinutes(1),
            CollectedAtUtc = timestamp.AddSeconds(5),
            RawJson = rawJson
        };
}
