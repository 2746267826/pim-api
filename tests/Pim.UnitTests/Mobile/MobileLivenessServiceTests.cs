using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Liveness;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// REQ-5 / REQ-6 / REQ-7 / REQ-9 / REQ-11 / REQ-13 的服务端验证。
/// 用真实 EF 查询路径（InMemory provider）验证取数与聚合，纯口径由
/// <see cref="DeviceLivenessCalculatorTests"/> 覆盖。
/// </summary>
public sealed class MobileLivenessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private const string PhoneId = "android-phone";
    private const string TabletId = "android-tablet";

    private static MobileForensicIngestService IngestService(PimDbContext db) =>
        new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now),
            NullLogger<MobileForensicIngestService>.Instance);

    private static MobileLivenessService LivenessService(PimDbContext db) =>
        new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));

    private static MobileForensicsUploadRequest Upload(
        string deviceId,
        IEnumerable<MobileForensicEventUploadItem> events,
        IEnumerable<MobileDroppedReasonSummaryItem>? summaries = null) =>
        new(deviceId, "batch-1", events.ToList(), summaries?.ToList());

    private static MobileForensicEventUploadItem Heartbeat(string key, DateTimeOffset at) =>
        new(key, ForensicEventTypes.Heartbeat, at, "{\"bootElapsedMs\":1000,\"standbyBucket\":30}");

    private static async Task SeedDeviceAsync(
        PimDbContext db,
        string deviceId,
        string displayName,
        string? metadataJson = null)
    {
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            DisplayName = displayName,
            MetadataJson = metadataJson ?? "{}",
        });
        await db.SaveChangesAsync();
    }

    // ===== REQ-5 / AC-5.2：幂等 =====

    [Fact]
    public async Task Ingest_RepeatedBatch_IsIdempotent()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);
        var request = Upload(PhoneId, new[]
        {
            Heartbeat("hb-1", Now.AddMinutes(-30)),
            Heartbeat("hb-2", Now.AddMinutes(-15)),
        });

        var first = await service.IngestAsync(request, CancellationToken.None);
        var second = await service.IngestAsync(request, CancellationToken.None);

        Assert.Equal(2, first.AcceptedCount);
        Assert.Equal(0, first.SkippedCount);
        Assert.Equal(0, second.AcceptedCount);
        Assert.Equal(2, second.SkippedCount);
        Assert.Equal(2, await db.Set<MobileForensicEventEntity>().CountAsync());
    }

    [Fact]
    public async Task Ingest_UnknownEventType_IsRejectedNotSilentlyDropped()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);

        var result = await service.IngestAsync(
            Upload(PhoneId, new[] { new MobileForensicEventUploadItem("x-1", "not-a-type", Now, "{}") }),
            CancellationToken.None);

        Assert.Equal(1, result.RejectedCount);
        Assert.Contains("x-1", result.RejectedKeys);
        Assert.Empty(result.AcceptedKeys);
    }

    [Fact]
    public async Task Ingest_MalformedPayload_DoesNotFailTheBatch()
    {
        // AC-30.2：旧客户端可能带未知/异常字段，服务端不得因此报错丢掉整批。
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);

        var result = await service.IngestAsync(
            Upload(PhoneId, new[]
            {
                new MobileForensicEventUploadItem("bad", ForensicEventTypes.Heartbeat, Now, "{not json"),
                Heartbeat("good", Now),
            }),
            CancellationToken.None);

        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.RejectedCount);
    }

    // ===== REQ-9 / AC-9.2：丢弃原因按天统计 =====

    [Fact]
    public async Task Ingest_DroppedReasonSummary_OverwritesInsteadOfAccumulating()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);

        await service.IngestAsync(
            Upload(PhoneId, Array.Empty<MobileForensicEventUploadItem>(), new[]
            {
                new MobileDroppedReasonSummaryItem("2026-09-22", "horizontal-accuracy-too-low", 12),
            }),
            CancellationToken.None);

        await service.IngestAsync(
            Upload(PhoneId, Array.Empty<MobileForensicEventUploadItem>(), new[]
            {
                new MobileDroppedReasonSummaryItem("2026-09-22", "horizontal-accuracy-too-low", 12),
                new MobileDroppedReasonSummaryItem("2026-09-23", "missing-horizontal-accuracy", 3),
            }),
            CancellationToken.None);

        var rows = await db.Set<MobileDroppedReasonDailyEntity>()
            .Where(row => row.DeviceId == PhoneId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(12, rows.Single(r => r.LocalDate == "2026-09-22").Count);
        Assert.Equal(3, rows.Single(r => r.LocalDate == "2026-09-23").Count);
    }

    [Fact]
    public async Task DroppedReasons_ReturnsDeviceCountsByDay()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var ingest = IngestService(db);
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110");

        await ingest.IngestAsync(
            Upload(PhoneId, Array.Empty<MobileForensicEventUploadItem>(), new[]
            {
                new MobileDroppedReasonSummaryItem("2026-09-22", "accuracy", 5),
                new MobileDroppedReasonSummaryItem("2026-09-22", "policy", 2),
            }),
            CancellationToken.None);

        var response = await LivenessService(db).GetDroppedReasonsAsync(
            PhoneId,
            Now.AddDays(-7),
            Now,
            CancellationToken.None);

        Assert.Equal(2, response.Items.Count);
        Assert.Equal(7, response.TotalCount);
    }

    // ===== REQ-7 / REQ-13：存活页数据 =====

    [Fact]
    public async Task Overview_SeparatesPhoneTabletAndUnclassified()
    {
        // AC-7.3：手机与平板分别成块显示，统计不混算。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");
        await SeedDeviceAsync(db, TabletId, "OPPO OPD2409", "{\"deviceKind\":\"tablet\"}");
        await SeedDeviceAsync(db, "android-legacy", "旧客户端设备", "{\"smallestScreenWidthDp\":720}");
        await SeedDeviceAsync(db, "android-mystery", "未知机型");

        var overview = await LivenessService(db).GetOverviewAsync(
            Now.AddDays(-7),
            Now,
            CancellationToken.None);

        Assert.Equal(PhoneId, Assert.Single(overview.Phones).DeviceId);
        // smallestScreenWidthDp >= 600 是 Android 官方平板判定线，旧客户端靠它回退分块。
        Assert.Equal(2, overview.Tablets.Count);
        Assert.Contains(overview.Tablets, t => t.DeviceId == TabletId);
        Assert.Contains(overview.Tablets, t => t.DeviceId == "android-legacy");
        Assert.Equal("android-mystery", Assert.Single(overview.Unclassified).DeviceId);
        Assert.Equal(DeviceLivenessRules.ExpectedHeartbeatIntervalMinutes,
            overview.ExpectedHeartbeatIntervalMinutes);
    }

    [Fact]
    public async Task Overview_DeviceWithoutEvidence_NeverReportsFullCoverage()
    {
        // AC-7.5：从未上报的设备显示"无数据/未上报"，不得显示为 100% 存活。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");

        var overview = await LivenessService(db).GetOverviewAsync(
            Now.AddDays(-7),
            Now,
            CancellationToken.None);

        var block = Assert.Single(overview.Phones);
        Assert.False(block.HasData);
        Assert.Null(block.CoverageByHour);
        Assert.Null(block.CoverageByExpectedHeartbeat);
        Assert.Equal(DeviceLivenessRules.NoDataConclusion, block.Conclusion);
    }

    [Fact]
    public async Task DeviceLiveness_UsageEventsAloneDoNotCountAsLivenessEvidence()
    {
        // AC-7.6 / AC-13.3：只有使用事件（可被系统统计回补）时不构成可信存活证据。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");

        db.Set<MobileUsageEventEntity>().Add(new MobileUsageEventEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = PhoneId,
            PackageName = "com.example",
            EventType = "ACTIVITY_RESUMED",
            EventTimestampUtc = Now.AddHours(-2),
            SourceWindowStartUtc = Now.AddHours(-3),
            SourceWindowEndUtc = Now.AddHours(-2),
            CollectedAtUtc = Now.AddHours(-1),
            RawJson = "{}",
        });
        await db.SaveChangesAsync();

        var block = await LivenessService(db).GetDeviceLivenessAsync(
            PhoneId,
            Now.AddDays(-7),
            Now,
            CancellationToken.None);

        Assert.NotNull(block);
        Assert.False(block!.HasData);
        Assert.Null(block.CoverageByHour);
    }

    [Fact]
    public async Task DeviceLiveness_SyncBatchArrivalCountsButBackfillWindowDoesNot()
    {
        // AC-13.3：批次**到达时刻**是存活证据；它的 window_* 是数据补传语义，不能当存活证据。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");

        // 批次在"到达时刻" -3 天入库，但它宣称覆盖了 20 天前的历史窗口。
        db.Set<MobileSyncBatchEntity>().Add(new MobileSyncBatchEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = PhoneId,
            BatchId = "batch-1",
            WindowStartUtc = Now.AddDays(-20),
            WindowEndUtc = Now.AddDays(-20).AddHours(2),
            CreatedAt = Now.AddDays(-3),
        });
        await db.SaveChangesAsync();

        var recent = await LivenessService(db).GetDeviceLivenessAsync(
            PhoneId,
            Now.AddDays(-7),
            Now,
            CancellationToken.None);

        Assert.True(recent!.HasData);
        Assert.Equal(0, recent.ObservedHeartbeats);

        // 只覆盖"回补窗口"那段时间的查询不应因为窗口本身而有存活证据（那时批次还没到达）。
        var backfillOnly = await LivenessService(db).GetDeviceLivenessAsync(
            PhoneId,
            Now.AddDays(-21),
            Now.AddDays(-19),
            CancellationToken.None);

        Assert.False(backfillOnly!.HasData);
    }

    [Fact]
    public async Task DeviceLiveness_ReturnsRESTCompatibleSummaryFields()
    {
        // AC-11.1：一句话结论、两个覆盖率、最长静默、死因汇总、最近事件时间、≥1 小时静默标记。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");
        var service = IngestService(db);

        await service.IngestAsync(
            Upload(PhoneId, new[]
            {
                Heartbeat("hb-1", Now.AddHours(-6)),
                new MobileForensicEventUploadItem(
                    "exit-1",
                    ForensicEventTypes.ProcessExit,
                    Now.AddHours(-3),
                    "{\"reason\":\"REASON_LOW_MEMORY\",\"importance\":100,\"pssKb\":12345,\"rssKb\":23456}"),
            }),
            CancellationToken.None);

        var block = await LivenessService(db).GetDeviceLivenessAsync(
            PhoneId,
            Now.AddDays(-7),
            Now,
            CancellationToken.None);

        Assert.NotNull(block);
        Assert.True(block!.HasData);
        Assert.True(block.HasSilenceOverOneHour);
        Assert.Equal("内存不足被系统回收", Assert.Single(block.Causes).Label);
        Assert.NotNull(block.LastEventAtUtc);
        Assert.NotNull(block.CoverageByHourDefinition);
        Assert.NotNull(block.CoverageByExpectedHeartbeatDefinition);
    }

    [Fact]
    public async Task DeviceLiveness_UnknownDevice_ReturnsNullSoEndpointCanAnswer404()
    {
        // AC-11.4：设备存在但无数据 → 明确空态；设备不存在 → null（端点回 404）。两者必须可区分。
        await using var db = MobileTestHelpers.CreateDb();
        var block = await LivenessService(db).GetDeviceLivenessAsync(
            "android-missing",
            Now.AddDays(-7),
            Now,
            CancellationToken.None);

        Assert.Null(block);
    }

    // ===== REQ-6 / AC-6.1 / AC-1.3 / AC-7.2 =====

    [Fact]
    public async Task Events_AreRetainedBeyondThirtyDaysOnTheServer()
    {
        // AC-6.1：服务端长期保留原始事件，30 天前的事件仍能查询返回。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");
        await IngestService(db).IngestAsync(
            Upload(PhoneId, new[] { Heartbeat("hb-old", Now.AddDays(-45)) }),
            CancellationToken.None);

        var page = await LivenessService(db).GetEventsAsync(
            PhoneId,
            Now.AddDays(-60),
            Now,
            page: 1,
            pageSize: 50,
            CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("hb-old", page.Items.Single().PayloadJson.Contains("bootElapsedMs")
            ? "hb-old"
            : "unexpected");
    }

    [Fact]
    public async Task Events_ExposeTypeReasonContextAndRawJson()
    {
        // AC-7.2：逐条事件可见时刻、类型、原因、上下文，并可查看原始 JSON。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");
        await IngestService(db).IngestAsync(
            Upload(PhoneId, new[]
            {
                new MobileForensicEventUploadItem(
                    "exit-1",
                    ForensicEventTypes.ProcessExit,
                    Now.AddHours(-1),
                    "{\"reason\":\"REASON_ANR\",\"importance\":100,\"screenOn\":false,\"batteryPercent\":42}"),
            }),
            CancellationToken.None);

        var page = await LivenessService(db).GetEventsAsync(
            PhoneId, Now.AddDays(-1), Now, 1, 50, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("process-exit", item.EventType);
        Assert.Equal("进程退出", item.EventTypeLabel);
        Assert.Equal("应用无响应（ANR）", item.ReasonLabel);
        Assert.Contains("电量 42%", item.Description);
        Assert.Contains("REASON_ANR", item.PayloadJson);
    }

    [Fact]
    public async Task Events_UnrecognisedExitReason_ReportsUnknownWithInference()
    {
        // AC-1.3：系统未提供/无法识别的退出记录显示"未知"并给出可推断线索，不给"无异常"。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");
        await IngestService(db).IngestAsync(
            Upload(PhoneId, new[]
            {
                new MobileForensicEventUploadItem(
                    "exit-x",
                    ForensicEventTypes.ProcessExit,
                    Now.AddHours(-1),
                    "{}"),
                new MobileForensicEventUploadItem(
                    "exit-y",
                    ForensicEventTypes.ProcessExit,
                    Now.AddHours(-2),
                    "{\"reason\":\"REASON_FROM_THE_FUTURE\"}"),
            }),
            CancellationToken.None);

        var page = await LivenessService(db).GetEventsAsync(
            PhoneId, Now.AddDays(-1), Now, 1, 50, CancellationToken.None);

        var noReason = page.Items.Single(item => item.PayloadJson == "{}");
        Assert.Equal(MobileLivenessCauseClassifier.NoRecordCause, noReason.Reason);
        Assert.False(string.IsNullOrWhiteSpace(noReason.Inference));

        var unknown = page.Items.Single(item => item.PayloadJson.Contains("THE_FUTURE"));
        Assert.Equal(MobileLivenessCauseClassifier.UnknownCause, unknown.Reason);
        Assert.Contains("REASON_FROM_THE_FUTURE", unknown.Inference);
    }

    [Fact]
    public async Task Events_ForceStopKindsAreDistinguished()
    {
        // AC-2.1 / AC-2.2 / AC-2.3：强停、重启、哨兵被清空（权限变更）必须可区分。
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db, PhoneId, "OPPO PLG110", "{\"deviceKind\":\"phone\"}");
        await IngestService(db).IngestAsync(
            Upload(PhoneId, new[]
            {
                new MobileForensicEventUploadItem("fs-1", ForensicEventTypes.ForceStop, Now.AddHours(-1),
                    "{\"kind\":\"force-stop\",\"evidence\":\"sentinel-missing\"}"),
                new MobileForensicEventUploadItem("fs-2", ForensicEventTypes.ForceStop, Now.AddHours(-2),
                    "{\"kind\":\"reboot\",\"evidence\":\"boot-elapsed-decreased\"}"),
                new MobileForensicEventUploadItem("fs-3", ForensicEventTypes.ForceStop, Now.AddHours(-3),
                    "{\"kind\":\"sentinel-cleared-permission\",\"evidence\":\"permission-change\"}"),
            }),
            CancellationToken.None);

        var block = await LivenessService(db).GetDeviceLivenessAsync(
            PhoneId, Now.AddDays(-1), Now, CancellationToken.None);

        var labels = block!.Causes.Select(cause => cause.Label).ToHashSet();
        Assert.Contains("疑似强停", labels);
        Assert.Contains("设备重启", labels);
        Assert.Contains("哨兵被清空（权限变更）", labels);
    }

    [Fact]
    public async Task ResolveRange_DefaultsToLastSevenDaysAndClampsFuture()
    {
        // AC-7.1：首屏默认最近 7 天。
        await using var db = MobileTestHelpers.CreateDb();
        var service = LivenessService(db);

        var (start, end) = service.ResolveRange(null, null);
        Assert.Equal(Now, end);
        Assert.Equal(Now - TimeSpan.FromDays(7), start);

        var (clampedStart, clampedEnd) = service.ResolveRange(null, Now.AddDays(3));
        Assert.Equal(Now, clampedEnd);
        Assert.Equal(Now - TimeSpan.FromDays(7), clampedStart);
    }

    [Fact]
    public void ResolveDeviceKind_FallsBackToScreenWidthThenUnknown()
    {
        Assert.Equal("phone", MobileLivenessService.ResolveDeviceKind("{\"deviceKind\":\"PHONE\"}"));
        Assert.Equal("tablet", MobileLivenessService.ResolveDeviceKind("{\"deviceKind\":\"tablet\"}"));
        Assert.Equal("tablet", MobileLivenessService.ResolveDeviceKind("{\"smallestScreenWidthDp\":600}"));
        Assert.Equal("phone", MobileLivenessService.ResolveDeviceKind("{\"smallestScreenWidthDp\":411}"));
        Assert.Equal("unknown", MobileLivenessService.ResolveDeviceKind("{}"));
        Assert.Equal("unknown", MobileLivenessService.ResolveDeviceKind(null));
        Assert.Equal("unknown", MobileLivenessService.ResolveDeviceKind("{not json"));
    }
}
