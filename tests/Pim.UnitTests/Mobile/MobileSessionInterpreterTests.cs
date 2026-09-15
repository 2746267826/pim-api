using Microsoft.EntityFrameworkCore;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class MobileSessionInterpreterTests
{
    [Fact]
    public async Task RebuildSessionsAsync_ClosesPreviousForegroundAppOnAppSwitchAndFlagsIt()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var userId = MobileTestHelpers.UserId;
        var start = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        db.Set<MobileUsageEventEntity>().AddRange(
            Event(userId, "com.example.mail", "MOVE_TO_FOREGROUND", start),
            Event(userId, "com.example.chat", "MOVE_TO_FOREGROUND", start.AddMinutes(5)),
            Event(userId, "com.example.chat", "MOVE_TO_BACKGROUND", start.AddMinutes(10)));
        await db.SaveChangesAsync();

        var interpreter = new MobileSessionInterpreter(db);
        await interpreter.RebuildSessionsAsync(userId, "android-main", start, start.AddMinutes(30), CancellationToken.None);

        var sessions = await db.Set<MobileUsageSessionEntity>()
            .OrderBy(session => session.StartUtc)
            .ToListAsync();

        Assert.Equal(2, sessions.Count);
        Assert.Equal("com.example.mail", sessions[0].PackageName);
        Assert.Equal(start, sessions[0].StartUtc);
        Assert.Equal(start.AddMinutes(5), sessions[0].EndUtc);
        Assert.Contains("closed-by-app-switch", sessions[0].QualityFlagsJson);
        Assert.Equal("com.example.chat", sessions[1].PackageName);
        Assert.Equal(start.AddMinutes(5), sessions[1].StartUtc);
        Assert.Equal(start.AddMinutes(10), sessions[1].EndUtc);
    }

    [Fact]
    public async Task RebuildSessionsAsync_KeepsSessionsWhoseStartEventPredatesTheWindow()
    {
        // #248：删除按"与窗口重叠"，重建只读"窗口内"的事件 —— 起点在窗口之外、
        // 但与窗口重叠的会话会被删掉却建不回来。
        await using var db = MobileTestHelpers.CreateDb();
        var userId = MobileTestHelpers.UserId;
        var start = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        db.Set<MobileUsageEventEntity>().AddRange(
            Event(userId, "com.example.mail", "MOVE_TO_FOREGROUND", start),
            Event(userId, "com.example.mail", "MOVE_TO_BACKGROUND", start.AddMinutes(30)));
        await db.SaveChangesAsync();

        var interpreter = new MobileSessionInterpreter(db);
        // 第一批只覆盖到 10:15，会话按窗口末端封口。
        await interpreter.RebuildSessionsAsync(userId, "android-main", start, start.AddMinutes(15), CancellationToken.None);
        var firstBuild = await db.Set<MobileUsageSessionEntity>().SingleAsync();
        Assert.Equal(start, firstBuild.StartUtc);
        Assert.Equal(start.AddMinutes(15), firstBuild.EndUtc);

        // 第二批窗口从 10:10 开始：删除口径命中该会话，重建必须能覆盖它的起点事件。
        await interpreter.RebuildSessionsAsync(userId, "android-main", start.AddMinutes(10), start.AddMinutes(45), CancellationToken.None);

        var sessions = await db.Set<MobileUsageSessionEntity>().OrderBy(s => s.StartUtc).ToListAsync();
        var session = Assert.Single(sessions);
        Assert.Equal(start, session.StartUtc);
        Assert.Equal(start.AddMinutes(30), session.EndUtc);
        Assert.DoesNotContain("open-ended", session.QualityFlagsJson);
    }

    [Fact]
    public async Task RebuildSessionsAsync_IsIdempotentForTheSameWindow()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var userId = MobileTestHelpers.UserId;
        var start = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        db.Set<MobileUsageEventEntity>().AddRange(
            Event(userId, "com.example.mail", "MOVE_TO_FOREGROUND", start),
            Event(userId, "com.example.mail", "MOVE_TO_BACKGROUND", start.AddMinutes(30)),
            Event(userId, "com.example.chat", "MOVE_TO_FOREGROUND", start.AddMinutes(40)),
            Event(userId, "com.example.chat", "MOVE_TO_BACKGROUND", start.AddMinutes(50)));
        await db.SaveChangesAsync();

        var interpreter = new MobileSessionInterpreter(db);
        var windowEnd = start.AddHours(1);
        await interpreter.RebuildSessionsAsync(userId, "android-main", start, windowEnd, CancellationToken.None);
        var first = await db.Set<MobileUsageSessionEntity>()
            .OrderBy(s => s.StartUtc)
            .Select(s => new { s.StartUtc, s.EndUtc, s.PackageName })
            .ToListAsync();

        await interpreter.RebuildSessionsAsync(userId, "android-main", start, windowEnd, CancellationToken.None);
        var second = await db.Set<MobileUsageSessionEntity>()
            .OrderBy(s => s.StartUtc)
            .Select(s => new { s.StartUtc, s.EndUtc, s.PackageName })
            .ToListAsync();

        // 重复重建同一窗口：既不能丢行，也不能叠加重复行（删除口径与重建口径一致）。
        Assert.Equal(2, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task RebuildSessionsAsync_KeepsSessionEndingExactlyAtWindowStart()
    {
        await using var db = MobileTestHelpers.CreateDb();
        var userId = MobileTestHelpers.UserId;
        var start = DateTimeOffset.Parse("2026-07-06T09:00:00Z");
        db.Set<MobileUsageEventEntity>().AddRange(
            Event(userId, "com.example.mail", "MOVE_TO_FOREGROUND", start),
            Event(userId, "com.example.mail", "MOVE_TO_BACKGROUND", start.AddHours(1)));
        await db.SaveChangesAsync();

        var interpreter = new MobileSessionInterpreter(db);
        await interpreter.RebuildSessionsAsync(userId, "android-main", start, start.AddHours(1), CancellationToken.None);
        await interpreter.RebuildSessionsAsync(
            userId,
            "android-main",
            start.AddHours(1),
            start.AddHours(2),
            CancellationToken.None);

        var session = Assert.Single(await db.Set<MobileUsageSessionEntity>().ToListAsync());
        Assert.Equal(start, session.StartUtc);
        Assert.Equal(start.AddHours(1), session.EndUtc);
    }

    private static MobileUsageEventEntity Event(
        Guid userId,
        string packageName,
        string eventType,
        DateTimeOffset timestamp) => new()
        {
            UserId = userId,
            DeviceId = "android-main",
            PackageName = packageName,
            EventType = eventType,
            EventTimestampUtc = timestamp,
            ClassName = "MainActivity",
            SourceWindowStartUtc = timestamp.Date,
            SourceWindowEndUtc = timestamp.Date.AddDays(1),
            CollectedAtUtc = timestamp.AddSeconds(1),
            RawJson = "{}",
            QualityFlagsJson = "[]",
            CreatedAt = timestamp.AddSeconds(2)
        };
}
