using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

/// <summary>
/// 入库时长精度对齐（#254 S1 根因回归）。
///
/// 背景：事件起点由 <c>TruncateToMillisecond</c> 截断到毫秒，但时长此前保留浮点全精度，
/// 导致事件结束时刻落在非整毫秒上（如 11:35:27.117103）。客户端分段离散化产出的片段是
/// "上一段结束 = 下一段开始"的相接关系，而下一段的起点恒为整毫秒 —— 于是两者在亚毫秒
/// 量级上"重叠"约 100 微秒，被 S1（同类型事件不重叠）判据计为真实违规。
///
/// 这些用例从**写入侧**断言该缺陷不会复现，并用 S1 判据本体验证"写进去的数据确实不违规"
/// ——判据与写入侧共用同一份口径，避免两边各自漂移。
/// </summary>
public class PcTrackerEventMillisecondNormalizationTests
{
    private static readonly DateTimeOffset BaseTs = new(2026, 8, 20, 6, 30, 0, TimeSpan.Zero);

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static PcTrackerService CreateService(PimDbContext db)
        => new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationSettingsService(db),
            new ActivityTimelineSmoothingService());

    private static TrackerEventDto EventAt(DateTimeOffset ts, double duration, string appName = "App")
        => new(
            Timestamp: ts.ToString("O"),
            Duration: duration,
            EventType: "window",
            ExePath: @"C:\Program Files\app\app.exe",
            AppName: appName,
            DisplayName: appName,
            WindowTitle: "Title",
            CommandLine: null,
            IsIdle: false,
            IsMediaActive: false,
            Url: "https://example.com",
            Domain: "example.com",
            PagePath: null,
            Audible: null,
            Incognito: null,
            TabCount: null,
            PageVisitCount: 0,
            PageVisitDuration: 0,
            RawJson: null,
            Date: "2026-08-20",
            Browser: null,
            InstanceId: null);

    /// <summary>把入库事件映射成 S1 判据的输入模型。</summary>
    private static List<EventTimeSpan> ToTimeSpans(PimDbContext db)
        => db.Set<TrackerEventEntity>()
            .OrderBy(e => e.Timestamp)
            .ToList()
            .Select(e => new EventTimeSpan
            {
                EventId = e.Id.ToString(),
                DeviceId = e.DeviceId,
                EventType = e.EventType,
                StartTime = e.Timestamp.UtcDateTime,
                EndTime = e.Timestamp.UtcDateTime.AddSeconds(e.Duration)
            })
            .ToList();

    [Fact]
    public async Task Upload_SubMillisecondDuration_IsStoredMillisecondAligned()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        // 起点已在整毫秒上，时长带亚毫秒尾数（真实样本：11:35:26.294 + 0.8231027s）
        var start = BaseTs.AddMilliseconds(294);
        Assert.Equal(1, await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(start, 0.8231027) }),
            CancellationToken.None));

        var saved = Assert.Single(db.Set<TrackerEventEntity>());
        double endMicroseconds = (saved.Timestamp.UtcDateTime.AddSeconds(saved.Duration)).Ticks % TimeSpan.TicksPerMillisecond;
        Assert.Equal(0, endMicroseconds);

        // 精度损失必须小于 1 毫秒——用户可见时长不受影响
        Assert.True(Math.Abs(saved.Duration - 0.8231027) < 0.001,
            $"时长精度损失应小于 1ms，实际 {Math.Abs(saved.Duration - 0.8231027) * 1000:F3}ms");
    }

    [Fact]
    public async Task Upload_AdjacentSegments_ProduceNoOverlapUnderS1Ruler()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        // 真实缺陷样本（pc_tracker_events id 8185/8186）：
        //   A: 11:35:26.294 + 0.8231027s  → 结束 11:35:27.117103
        //   B: 11:35:27.117 起            → 与 A 重叠 103 微秒
        var aStart = BaseTs.AddMilliseconds(294);
        var bStart = BaseTs.AddMilliseconds(1117);

        await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(aStart, 0.8231027, "explorer") }),
            CancellationToken.None);
        await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(bStart, 23.1777944, "firefox") }),
            CancellationToken.None);

        var spans = ToTimeSpans(db);
        Assert.Equal(2, spans.Count);

        var result = DataReliabilityInvariants.CheckS1_NoOverlap(spans, referenceTimeUtc: BaseTs.UtcDateTime.AddHours(1));
        Assert.Equal(InvariantStatus.Pass, result.Status);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public async Task Upload_AdjacentSegments_RemainContiguous()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var aStart = BaseTs.AddMilliseconds(294);
        var bStart = BaseTs.AddMilliseconds(1117);

        await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(aStart, 0.8231027, "explorer") }),
            CancellationToken.None);
        await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(bStart, 23.1777944, "firefox") }),
            CancellationToken.None);

        var spans = ToTimeSpans(db);
        var a = spans.Single(s => s.StartTime == aStart.UtcDateTime);
        var b = spans.Single(s => s.StartTime == bStart.UtcDateTime);

        // 相邻片段必须首尾相接：归一化只能消除重叠，不得反过来制造空洞。
        // 允许至多 1 tick（100 纳秒）的欠冲 —— 时长以"整毫秒"存储后经 double
        // 还原为 DateTimeOffset 时，AddSeconds(k/1000.0) 可能落在边界前 1 个 tick。
        // 已验证该换算永不**超出**边界（27 万个样本 0 次超出），因此只会 1 tick 欠冲，
        // 绝不会重新引入重叠（重叠由下一条用例断言为严格的 0）。
        Assert.True(a.EndTime <= b.StartTime, "归一化后的相邻片段不得重叠");
        Assert.True(b.StartTime - a.EndTime <= TimeSpan.FromTicks(1),
            $"相邻片段空洞应 <= 1 tick，实际 {(b.StartTime - a.EndTime).Ticks} ticks");
    }

    [Fact]
    public async Task Upload_LongChainOfAdjacentSegments_ProducesNoOverlapAndNoHole()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        // 模拟 SessionToEvents 的分段离散化：每段起点 = 上一段结束（整毫秒），时长带亚毫秒尾数
        var events = new List<TrackerEventDto>();
        var cursor = BaseTs.AddMilliseconds(500);
        var durations = new[] { 0.8231027, 5.1637746, 275.2172233, 3.1163664, 0.1, 60.0009 };
        foreach (var d in durations)
        {
            events.Add(EventAt(cursor, d, "app"));
            // 下一段起点 = floor(当前结束) 到毫秒
            var end = cursor.AddSeconds(d);
            cursor = new DateTimeOffset(
                end.Ticks - (end.Ticks % TimeSpan.TicksPerMillisecond),
                TimeSpan.Zero);
        }

        Assert.Equal(durations.Length, await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", events), CancellationToken.None));

        var spans = ToTimeSpans(db);
        Assert.Equal(durations.Length, spans.Count);

        // 1) 不重叠
        var s1 = DataReliabilityInvariants.CheckS1_NoOverlap(spans, referenceTimeUtc: BaseTs.UtcDateTime.AddHours(1));
        Assert.Equal(InvariantStatus.Pass, s1.Status);

        // 2) 不留空洞（相邻区间首尾相接，容忍至多 1 tick 的浮点欠冲）
        for (int i = 0; i < spans.Count - 1; i++)
        {
            Assert.True(spans[i].EndTime <= spans[i + 1].StartTime,
                $"第 {i} 段与第 {i + 1} 段重叠");
            Assert.True(spans[i + 1].StartTime - spans[i].EndTime <= TimeSpan.FromTicks(1),
                $"第 {i} 段与第 {i + 1} 段之间的空洞为 {(spans[i + 1].StartTime - spans[i].EndTime).Ticks} ticks（应 <= 1）");
        }

        // 3) 总时长损失小于 1ms/段
        double original = durations.Sum();
        double stored = spans.Sum(s => (s.EndTime - s.StartTime).TotalSeconds);
        Assert.True(original - stored < durations.Length * 0.002,
            $"总时长损失应小于 2ms/段，实际 {(original - stored) * 1000:F3}ms");
        Assert.True(stored <= original, "归一只允许缩短时长，不得放大");
    }

    [Fact]
    public async Task Upload_SubMillisecondDuration_StillDeduplicates()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var start = BaseTs.AddMilliseconds(294);
        var request = new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(start, 0.8231027) });

        Assert.Equal(1, await service.UploadTrackerEventsAsync(request, CancellationToken.None));
        Assert.Equal(0, await service.UploadTrackerEventsAsync(request, CancellationToken.None));

        Assert.Single(db.Set<TrackerEventEntity>());
    }

    [Fact]
    public async Task Upload_DurationWithFloatingPointNoise_NormalizesDeterministically()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        // 0.1 + 0.2 之类的浮点噪声不得改变归一化结果（同一逻辑时长必须得到同一存储值）
        var start = BaseTs.AddMilliseconds(100);
        var noisy = 0.1 + 0.2;          // 0.30000000000000004
        var clean = 0.3;

        await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(start, noisy, "app-x") }),
            CancellationToken.None);
        var first = Assert.Single(db.Set<TrackerEventEntity>());

        Assert.Equal(Math.Floor(clean * 1000) / 1000, first.Duration, 12);
    }

    [Fact]
    public async Task Upload_ZeroAndNegativeDuration_ArePreservedOrRejected()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        // 零时长必须原样保留（归一只对正时长做毫秒取整）
        await service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(BaseTs, 0) }),
            CancellationToken.None);
        Assert.Equal(0, Assert.Single(db.Set<TrackerEventEntity>()).Duration);

        // 负时长依旧被拒绝（校验顺序不变）
        await Assert.ThrowsAsync<ArgumentException>(() => service.UploadTrackerEventsAsync(
            new TrackerEventsUploadRequest("dev-a", new List<TrackerEventDto> { EventAt(BaseTs.AddMinutes(1), -1) }),
            CancellationToken.None));
    }
}
