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
/// WO-ANDROID-GATE-20260926 AC-8.4：**新增取证事件类型的三处登记必须一致**。
///
/// 工单原文：「设备端登记处 + `Pim.Core` 的 `ForensicEventTypes` +
/// **`MobileForensicIngestService.KnownEventTypes`（漏了会被服务端拒绝）**」。
/// 漏登记的失败表现是「设备上记了、服务端永远收不到」——最难从日志上看出来的一种，
/// 因此用契约测试钉死：**设备端字面量必须与服务端登记逐字相等**，
/// 并且真的能通过 ingest 校验。
///
/// 这里的字符串是**设备端 `LocationSprintEventTypes` / `PassiveLocationEventTypes`
/// 的逐字副本**（Kotlin 侧无法被 C# 直接引用）。任何一端改名而另一端没跟上，本用例会红。
/// </summary>
public sealed class MobileForensicEventTypeContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private const string PhoneId = "android-sprint-contract";

    // ===== 设备端字面量（逐字副本；改一端必须同步改另一端）=====
    private const string DeviceLocationSprint = "location-sprint";
    private const string DevicePassiveLocationCounter = "passive-location-counter";

    private static MobileForensicIngestService IngestService(PimDbContext db) =>
        new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now),
            NullLogger<MobileForensicIngestService>.Instance);

    private static MobileForensicsUploadRequest Upload(
        IEnumerable<MobileForensicEventUploadItem> events) =>
        new(PhoneId, "batch-sprint-1", events.ToList(), null);

    /// <summary>AC-8.4：冲刺台账事件类型在三处逐字一致。Pim.Core 与设备端必须相同。</summary>
    [Fact]
    public void LocationSprintType_MatchesDeviceLiteral_AndCoreConstant()
    {
        Assert.Equal(DeviceLocationSprint, ForensicEventTypes.LocationSprint);
    }

    /// <summary>AC-8.4：被动计数事件类型在三处逐字一致。</summary>
    [Fact]
    public void PassiveCounterType_MatchesDeviceLiteral_AndCoreConstant()
    {
        Assert.Equal(DevicePassiveLocationCounter, ForensicEventTypes.PassiveLocationCounter);
    }

    /// <summary>
    /// AC-8.4：两种新事件必须真的被 ingest 接受（未登记会走 rejected 分支）。
    /// 这条是「漏登记」最直接的复现：断言 accepted，而不是只看常量相等。
    /// </summary>
    [Fact]
    public async Task Ingest_AcceptsSprintAndPassiveTypes()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);

        var result = await service.IngestAsync(
            Upload(new[]
            {
                new MobileForensicEventUploadItem(
                    "sprint-1", DeviceLocationSprint, Now,
                    "{\"outcome\":\"executed\",\"sprintSampleCount\":28,\"sprintAcceptedCount\":12}"),
                new MobileForensicEventUploadItem(
                    "passive-counter-1", DevicePassiveLocationCounter, Now,
                    "{\"passiveCallbackCount\":300,\"passiveAcceptedCount\":300}"),
            }),
            CancellationToken.None);

        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Empty(result.RejectedKeys);
    }

    /// <summary>
    /// AC-8.4（反面）：**未登记的类型必须被显式拒绝**，不是静默忽略。
    /// 这条同时说明上一条不是「什么都收」——登记表确实在起作用。
    /// </summary>
    [Fact]
    public async Task Ingest_RejectsUnregisteredSprintType()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);

        var result = await service.IngestAsync(
            Upload(new[]
            {
                new MobileForensicEventUploadItem("sprint-unknown", "location-sprint-v2", Now, "{}"),
            }),
            CancellationToken.None);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Contains("sprint-unknown", result.RejectedKeys);
    }

    /// <summary>AC-8.4：事件类型取值登记后，实例真的落库（不只是返回值声称接受）。</summary>
    [Fact]
    public async Task Ingest_PersistsBothNewEventTypes()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var service = IngestService(db);

        await service.IngestAsync(
            Upload(new[]
            {
                new MobileForensicEventUploadItem("sprint-1", DeviceLocationSprint, Now, "{}"),
                new MobileForensicEventUploadItem("passive-counter-1", DevicePassiveLocationCounter, Now, "{}"),
            }),
            CancellationToken.None);

        var stored = await db.Set<MobileForensicEventEntity>()
            .AsNoTracking()
            .Select(row => row.EventType)
            .ToListAsync();

        Assert.Contains(DeviceLocationSprint, stored);
        Assert.Contains(DevicePassiveLocationCounter, stored);
    }

    /// <summary>
    /// REQ-14 规则 4 / AC-14.4：被动点的丢弃原因用 `passive-` 前缀编码，
    /// 服务端按「日 + 原因」聚合后应出现 `passive-*` 行（不改库结构）。
    /// </summary>
    [Fact]
    public async Task Ingest_AggregatesPassiveDroppedReasonsByDay()
    {
        await using var db = MobileTestHelpers.CreateDb();
        await SeedDeviceAsync(db);
        var service = IngestService(db);

        var result = await service.IngestAsync(
            new MobileForensicsUploadRequest(
                PhoneId,
                "batch-passive-drops",
                Array.Empty<MobileForensicEventUploadItem>(),
                new[]
                {
                    new MobileDroppedReasonSummaryItem("2026-09-26", "passive-horizontal-accuracy-too-low", 42),
                    new MobileDroppedReasonSummaryItem("2026-09-26", "passive-duplicate-fix", 7),
                    new MobileDroppedReasonSummaryItem("2026-09-26", "passive-enqueue-failed", 1),
                    new MobileDroppedReasonSummaryItem("2026-09-26", "horizontal-accuracy-too-low", 100),
                }),
            CancellationToken.None);

        var rows = await db.Set<MobileDroppedReasonDailyEntity>()
            .AsNoTracking()
            .Where(row => row.DeviceId == PhoneId)
            .ToListAsync();

        Assert.Equal(42, rows.Single(r => r.Reason == "passive-horizontal-accuracy-too-low").Count);
        Assert.Equal(7, rows.Single(r => r.Reason == "passive-duplicate-fix").Count);
        Assert.Equal(1, rows.Single(r => r.Reason == "passive-enqueue-failed").Count);
        // 主动流的原因保持不变（REQ-10：范围外不改）。
        Assert.Equal(100, rows.Single(r => r.Reason == "horizontal-accuracy-too-low").Count);
        Assert.True(result.AcceptedDroppedReasonCount >= 4);
    }

    private static async Task SeedDeviceAsync(PimDbContext db)
    {
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = PhoneId,
            DisplayName = PhoneId,
        });
        await db.SaveChangesAsync();
    }
}
