using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Quality;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Calendar;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Calendar;

/// <summary>
/// 工单 01 §A2.2「收集质量」模块：报告要复用**现成质量服务**的口径（不新造指标），
/// 取不到数据的来源必须显式标注「本周期无数据」（§A2.4）。
///
/// <para>
/// 这里同时钉住两侧：报告侧的消费契约（<see cref="ICollectionQualityProvider"/>），
/// 以及两个真实实现把既有组件字段搬进报告的映射。
/// </para>
/// </summary>
public class ReportCollectionQualityTests
{
    private static readonly Guid UserId = ServiceTestBase.DefaultUserId;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    private const string NoData = ReportService.NoDataMark;

    // ---- 报告侧：provider 给什么，报告就出什么 ----

    [Fact]
    public async Task Report_IncludesFiguresAndConclusionFromRegisteredProviders()
    {
        await using var db = CreateDb();
        var service = CreateService(db, [new FakeProvider(
            CollectionQualitySources.Mobile,
            "手机（Android）",
            "Warning",
            "移动使用汇总已滞后约 3.2 小时。",
            new Dictionary<string, long?> { ["UsageEvents"] = 1204, ["LocationPoints"] = null })]);

        var report = await service.GenerateAsync(
            new GenerateReportRequest("Daily", new DateOnly(2026, 9, 10), null), CancellationToken.None);

        var metrics = JsonDocument.Parse(report.MetricsJson).RootElement;
        Assert.Equal(1204, metrics.GetProperty("mobileUsageEvents").GetInt64());
        // 该来源的定位点取不到 → 显式无数据，而不是 0
        Assert.Equal(NoData, metrics.GetProperty("mobileLocationPoints").GetString());
        // 未注册的数据源（PC）同样必须显式出现，不能缺字段
        Assert.Equal(NoData, metrics.GetProperty("pcEvents").GetString());

        Assert.Contains("手机（Android）：Warning — 移动使用汇总已滞后约 3.2 小时。（使用事件 1204）", report.ContentMarkdown);
        Assert.Contains("PC（Windows）：本周期无数据", report.ContentMarkdown);
    }

    [Fact]
    public async Task Report_ForUnknownSourceKeys_StillEmitsEveryKnownField()
    {
        await using var db = CreateDb();
        var service = CreateService(db, []);

        var report = await service.GenerateAsync(
            new GenerateReportRequest("Daily", new DateOnly(2026, 9, 10), null), CancellationToken.None);

        foreach (var key in new[] { "mobileUsageEvents", "mobileLocationPoints", "pcEvents", "pcMissingHours" })
        {
            Assert.Equal(NoData, JsonDocument.Parse(report.MetricsJson).RootElement.GetProperty(key).GetString());
        }

        Assert.Contains("手机（Android）：本周期无数据", report.ContentMarkdown);
        Assert.Contains("PC（Windows）：本周期无数据", report.ContentMarkdown);
    }

    // ---- 手机实现：搬运既有 MobileQualityService 的口径 ----

    [Fact]
    public async Task MobileProvider_MapsExistingUsageComponentFigures()
    {
        await using var db = ServiceTestBase.CreateDb();
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = UserId, DeviceId = "phone-main", DeviceHash = "hash"
        });
        for (var i = 0; i < 2; i++)
        {
            db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
            {
                UserId = UserId,
                DeviceId = "phone-main",
                PackageName = "com.test.app",
                EventType = "ACTIVITY_RESUMED",
                EventTimestampUtc = Now.AddMinutes(-30 + i),
                SourceWindowStartUtc = Now.AddHours(-1),
                SourceWindowEndUtc = Now
            });
        }

        await db.SaveChangesAsync();

        var provider = new MobileCollectionQualityProvider(
            new MobileQualityService(db, ServiceTestBase.CurrentUser(), new StubTimeProvider { UtcNowValue = Now }));

        var snapshot = await provider.GetForPeriodAsync(Now.AddHours(-1), Now, CancellationToken.None);

        Assert.Equal(CollectionQualitySources.Mobile, snapshot.Key);
        Assert.Equal(2, snapshot.Figures["UsageEvents"]);
        // 本周期没有任何定位点 → 该口径取不到数据（组件判 Unknown），不能写 0
        Assert.Null(snapshot.Figures["LocationPoints"]);
        Assert.True(snapshot.HasAnyFigure);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Conclusion));
    }

    [Fact]
    public async Task MobileProvider_ForEmptyPeriod_ReportsNoFigures()
    {
        await using var db = ServiceTestBase.CreateDb();
        var provider = new MobileCollectionQualityProvider(
            new MobileQualityService(db, ServiceTestBase.CurrentUser(), new StubTimeProvider { UtcNowValue = Now }));

        var snapshot = await provider.GetForPeriodAsync(Now.AddHours(-1), Now, CancellationToken.None);

        Assert.Null(snapshot.Figures["UsageEvents"]);
        Assert.Null(snapshot.Figures["LocationPoints"]);
        Assert.False(snapshot.HasAnyFigure);
    }

    // ---- PC 实现：搬运既有 PcTrackerQualityService 的口径 ----

    [Fact]
    public async Task PcProvider_MapsExistingTrackerEventComponentFigures()
    {
        await using var db = CreateDb();
        db.Set<TrackerEventEntity>().Add(new TrackerEventEntity
        {
            DeviceId = "DESKTOP",
            Timestamp = DateTimeOffset.Parse("2026-09-10T06:10:00Z"),
            Duration = 60,
            EventType = "window",
            AppName = "Code.exe",
            WindowTitle = "Project",
            IsIdle = false,
            Date = new DateTime(2026, 9, 10),
            RawJson = "{}",
            CreatedAt = Now
        });
        await db.SaveChangesAsync();

        var provider = new PcCollectionQualityProvider(
            new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = Now }));

        // 业务日 2026-09-10 = [09-09 20:00Z, 09-10 20:00Z)
        var snapshot = await provider.GetForPeriodAsync(
            DateTimeOffset.Parse("2026-09-09T20:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T20:00:00Z"),
            CancellationToken.None);

        Assert.Equal(CollectionQualitySources.Pc, snapshot.Key);
        Assert.Equal(1, snapshot.Figures["Events"]);
        Assert.NotNull(snapshot.Figures["MissingHours"]);
    }

    [Fact]
    public async Task PcProvider_ForPeriodWithoutTrackerEvents_ReportsNoFigures()
    {
        await using var db = CreateDb();
        var provider = new PcCollectionQualityProvider(
            new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = Now }));

        var snapshot = await provider.GetForPeriodAsync(
            DateTimeOffset.Parse("2026-09-09T20:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T20:00:00Z"),
            CancellationToken.None);

        Assert.Null(snapshot.Figures["Events"]);
        Assert.Null(snapshot.Figures["MissingHours"]);
        Assert.False(snapshot.HasAnyFigure);
    }

    // ---- helpers ----

    private static ReportService CreateService(PimDbContext db, IEnumerable<ICollectionQualityProvider> providers)
        => new(db, new FixedCurrentUserService(UserId), new OperationConfirmationService(db), providers);

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(ReportArtifactEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(MobileUsageEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"report-collection-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class FakeProvider(
        string key,
        string label,
        string status,
        string conclusion,
        IReadOnlyDictionary<string, long?> figures) : ICollectionQualityProvider
    {
        public string Key => key;

        public string Label => label;

        public Task<CollectionQualitySnapshot> GetForPeriodAsync(
            DateTimeOffset rangeStartUtc,
            DateTimeOffset rangeEndUtc,
            CancellationToken ct = default)
            => Task.FromResult(new CollectionQualitySnapshot(key, label, status, conclusion, figures));
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
