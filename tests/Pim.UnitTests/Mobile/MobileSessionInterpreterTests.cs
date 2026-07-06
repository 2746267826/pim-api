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
