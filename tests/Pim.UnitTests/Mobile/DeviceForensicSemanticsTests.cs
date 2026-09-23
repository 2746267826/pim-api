using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// AC-6.3（反面）：设备合并 / 删除后，取证事件与丢弃原因统计必须按现有设备语义处理，
/// **不产生孤儿记录**。
///
/// 走真实 PostgreSQL（<see cref="TempMigrationDatabase"/> 建一次性库 + 完整迁移链）而不是
/// EF InMemory：合并/删除路径用的是 <c>ExecuteDelete</c> / <c>ExecuteUpdate</c>，
/// InMemory provider 根本无法翻译这些查询，在那里"通过"说明不了任何问题。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class DeviceForensicSemanticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private const string Source = "android-source";
    private const string Target = "android-target";

    private static DeviceManagementService Service(PimDbContext db) =>
        new(db, MobileTestHelpers.CurrentUser(), MobileTestHelpers.Time(Now));

    private static async Task<TempMigrationDatabase> NewDatabaseAsync(string prefix)
    {
        MobileTestHelpers.RegisterMobileModule();
        var database = await TempMigrationDatabase.CreateAsync(prefix);
        await database.MigrateAsync();
        return database;
    }

    private static async Task SeedDeviceAsync(PimDbContext db, string deviceId)
    {
        db.Set<MobileDeviceEntity>().Add(new MobileDeviceEntity
        {
            UserId = MobileTestHelpers.UserId,
            DeviceId = deviceId,
            DisplayName = deviceId,
        });
        await db.SaveChangesAsync();
    }

    private static MobileForensicEventEntity ForensicEvent(string deviceId, string key) => new()
    {
        UserId = MobileTestHelpers.UserId,
        DeviceId = deviceId,
        EventType = "heartbeat",
        ClientItemKey = key,
        OccurredAtUtc = Now.AddHours(-1),
        PayloadJson = "{}",
    };

    private static MobileDroppedReasonDailyEntity Dropped(string deviceId, string date, string reason, int count) => new()
    {
        UserId = MobileTestHelpers.UserId,
        DeviceId = deviceId,
        LocalDate = date,
        Reason = reason,
        Count = count,
    };

    [SkippableFact]
    public async Task Merge_MovesForensicEventsAndDoesNotLeaveOrphans()
    {
        await using var database = await NewDatabaseAsync("device_forensic_merge");
        var db = database.Db;
        await SeedDeviceAsync(db, Source);
        await SeedDeviceAsync(db, Target);
        db.Set<MobileForensicEventEntity>().AddRange(
            ForensicEvent(Source, "hb-1"),
            ForensicEvent(Source, "hb-2"),
            ForensicEvent(Target, "hb-2"), // 重装后重传造成的同键冲突
            ForensicEvent(Target, "hb-3"));
        await db.SaveChangesAsync();

        await Service(db).MergeAsync([Source], Target, CancellationToken.None);

        var rows = await db.Set<MobileForensicEventEntity>().ToListAsync();
        Assert.All(rows, row => Assert.Equal(Target, row.DeviceId));
        // 唯一键 (user, device, clientItemKey) 不允许重复：同键只保留一份。
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(row => row.ClientItemKey).Distinct().Count());
        Assert.Empty(await db.Set<MobileDeviceEntity>().Where(d => d.DeviceId == Source).ToListAsync());
    }

    [SkippableFact]
    public async Task Merge_SumsDroppedReasonCountsForTheSameDayAndReason()
    {
        await using var database = await NewDatabaseAsync("device_forensic_sum");
        var db = database.Db;
        await SeedDeviceAsync(db, Source);
        await SeedDeviceAsync(db, Target);
        db.Set<MobileDroppedReasonDailyEntity>().AddRange(
            Dropped(Source, "2026-09-22", "horizontal-accuracy-too-low", 5),
            Dropped(Source, "2026-09-22", "missing-horizontal-accuracy", 2),
            Dropped(Target, "2026-09-22", "horizontal-accuracy-too-low", 7));
        await db.SaveChangesAsync();

        await Service(db).MergeAsync([Source], Target, CancellationToken.None);

        var rows = await db.Set<MobileDroppedReasonDailyEntity>()
            .Where(row => row.UserId == MobileTestHelpers.UserId)
            .ToListAsync();

        Assert.All(rows, row => Assert.Equal(Target, row.DeviceId));
        Assert.Equal(2, rows.Count);
        // 两台设备同一天同一原因的条数语义上相加；不同原因保持独立。
        Assert.Equal(12, rows.Single(r => r.Reason == "horizontal-accuracy-too-low").Count);
        Assert.Equal(2, rows.Single(r => r.Reason == "missing-horizontal-accuracy").Count);
    }

    [SkippableFact]
    public async Task Delete_RemovesForensicEventsAndDroppedReasonStats()
    {
        await using var database = await NewDatabaseAsync("device_forensic_delete");
        var db = database.Db;
        await SeedDeviceAsync(db, Target);
        db.Set<MobileForensicEventEntity>().AddRange(
            ForensicEvent(Target, "hb-1"),
            ForensicEvent(Target, "hb-2"));
        db.Set<MobileDroppedReasonDailyEntity>().Add(
            Dropped(Target, "2026-09-22", "horizontal-accuracy-too-low", 3));
        await db.SaveChangesAsync();

        await Service(db).DeleteAsync(Target, CancellationToken.None);

        // AC-6.3：删除设备后不允许留下指向已删除 device_id 的孤儿取证记录。
        Assert.Empty(await db.Set<MobileForensicEventEntity>().ToListAsync());
        Assert.Empty(await db.Set<MobileDroppedReasonDailyEntity>().ToListAsync());
    }

    [SkippableFact]
    public async Task Delete_PreviewStillSucceedsWithForensicRowsPresent()
    {
        // 回归：新表存在不应让删除预览（统计路径）报错。
        await using var database = await NewDatabaseAsync("device_forensic_preview");
        var db = database.Db;
        await SeedDeviceAsync(db, Target);
        db.Set<MobileForensicEventEntity>().Add(ForensicEvent(Target, "hb-1"));
        await db.SaveChangesAsync();

        var preview = await Service(db).PreviewDeleteAsync(Target, CancellationToken.None);

        Assert.Equal(Target, preview.DeviceId);
    }
}
