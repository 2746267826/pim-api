using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class NativeTrackerTimelineTests
{
    [Fact]
    public void SessionToEvents_BrowserSessionWithPageVisits_SegmentsTimelineWithoutOverlap()
    {
        var config = new TrackerConfig();
        var api = new ApiClient();
        var tracker = new NativeTrackerService(api, config);

        var start = new DateTimeOffset(2026, 3, 24, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(5); // 300 seconds

        var session = new TrackerSession
        {
            Id = 1,
            AppName = "chrome",
            ExePath = "chrome.exe",
            WindowTitle = "Google Chrome",
            StartedAt = start,
            EndedAt = end,
            DurationSecs = 300,
            IsIdle = false,
            PageVisits = new List<TrackerPageVisit>
            {
                new()
                {
                    StartedAt = start.AddSeconds(30),
                    EndedAt = start.AddSeconds(120),
                    DurationSecs = 90,
                    Url = "https://github.com/issues",
                    Domain = "github.com",
                    WindowTitle = "GitHub"
                },
                new()
                {
                    StartedAt = start.AddSeconds(150),
                    EndedAt = start.AddSeconds(270),
                    DurationSecs = 120,
                    Url = "https://google.com/search",
                    Domain = "google.com",
                    WindowTitle = "Google Search"
                }
            }
        };

        var events = tracker.SessionToEvents(session, end);

        // Expected 5 disjoint intervals:
        // 1. [0, 30): window (msedge/chrome)
        // 2. [30, 120): web-page (github.com)
        // 3. [120, 150): window
        // 4. [150, 270): web-page (google.com)
        // 5. [270, 300): window
        Assert.Equal(5, events.Count);

        var totalDuration = events.Sum(e => e.Duration);
        Assert.Equal(300.0, totalDuration, precision: 2);

        for (int i = 0; i < events.Count - 1; i++)
        {
            var e1Start = DateTimeOffset.Parse(events[i].Timestamp);
            var e1End = e1Start.AddSeconds(events[i].Duration);
            var e2Start = DateTimeOffset.Parse(events[i + 1].Timestamp);

            // No two intervals may overlap! e1End must <= e2Start
            Assert.True(e1End <= e2Start.AddMilliseconds(50), $"Overlap detected between event {i} and {i + 1}");
        }
    }

    [Fact]
    public void HandleIdleStarted_ClampsIdleStart_ToLastEventEndTime()
    {
        var config = new TrackerConfig { IdleThresholdSeconds = 300 };
        var sessionMgr = new TrackerSessionManager(config);

        var t0 = new DateTimeOffset(2026, 3, 24, 10, 0, 0, TimeSpan.Zero);
        var tGapEnd = t0.AddMinutes(10); // 10:10:00 gap finished

        // Simulate a gap from 10:00 to 10:10
        sessionMgr.HandleGap(t0, tGapEnd);

        // At 10:12:00, user is detected idle for 300s (since 10:07:00).
        // But 10:07:00 was inside the gap!
        // HandleIdleStarted must clamp idleStart to tGapEnd (10:10:00), NOT 10:07:00!
        var tCheck = tGapEnd.AddMinutes(2); // 10:12:00
        sessionMgr.HandleIdleStarted(tCheck, TimeSpan.FromSeconds(300));

        Assert.NotNull(sessionMgr.Current);
        Assert.True(sessionMgr.Current.IsIdle);
        Assert.Equal(tGapEnd, sessionMgr.Current.StartedAt);
    }

    [Fact]
    public void CheckpointIfNeeded_SplitsContinuousSession_ExceedingMaxDuration()
    {
        var config = new TrackerConfig();
        var sessionMgr = new TrackerSessionManager(config);

        var t0 = new DateTimeOffset(2026, 3, 24, 10, 0, 0, TimeSpan.Zero);
        var win = new TrackerWindowInfo { AppName = "code", ExePath = "code.exe", WindowTitle = "VS Code" };

        sessionMgr.HandleWindowChange(win, t0);
        Assert.NotNull(sessionMgr.Current);

        // Advance 31 minutes (1860s > 1800s max session duration)
        var t1 = t0.AddMinutes(31);
        TrackerSession? closed = null;
        sessionMgr.SessionClosed += s => closed = s;

        var checkpointResult = sessionMgr.CheckpointIfNeeded(t1, win);

        Assert.NotNull(checkpointResult);
        Assert.NotNull(closed);
        Assert.Equal(1860.0, closed.DurationSecs);
        Assert.Equal(t1, sessionMgr.Current!.StartedAt);
    }

    [Fact]
    public void CheckpointIfNeeded_SplitsSession_CrossingBusinessDayBoundary()
    {
        var config = new TrackerConfig();
        var sessionMgr = new TrackerSessionManager(config);

        // 2026-03-24 03:55:00 CST (+08:00) belongs to Business Day 2026-03-23
        var t0 = new DateTimeOffset(2026, 3, 24, 3, 55, 0, TimeSpan.FromHours(8));
        var win = new TrackerWindowInfo { AppName = "code", ExePath = "code.exe", WindowTitle = "VS Code" };

        sessionMgr.HandleWindowChange(win, t0);

        // 2026-03-24 04:05:00 CST (+08:00) belongs to Business Day 2026-03-24
        var t1 = new DateTimeOffset(2026, 3, 24, 4, 5, 0, TimeSpan.FromHours(8));
        TrackerSession? closed = null;
        sessionMgr.SessionClosed += s => closed = s;

        var checkpointResult = sessionMgr.CheckpointIfNeeded(t1, win);

        Assert.NotNull(checkpointResult);
        Assert.NotNull(closed);
        Assert.Equal("2026-03-23", closed.Date);
        Assert.Equal("2026-03-24", sessionMgr.Current!.Date);
    }

    [Fact]
    public void HandleScreenOff_TransitionsToIdleImmediately_WithoutBacktracking()
    {
        var config = new TrackerConfig { IdleThresholdSeconds = 300 };
        var sessionMgr = new TrackerSessionManager(config);

        var t0 = new DateTimeOffset(2026, 3, 24, 10, 0, 0, TimeSpan.Zero);
        var win = new TrackerWindowInfo { AppName = "notepad", ExePath = "notepad.exe", WindowTitle = "Notes" };
        sessionMgr.HandleWindowChange(win, t0);

        var tScreenOff = t0.AddMinutes(5); // 10:05:00
        sessionMgr.HandleScreenOff(tScreenOff);

        Assert.NotNull(sessionMgr.Current);
        Assert.True(sessionMgr.Current.IsIdle);
        Assert.Equal(tScreenOff, sessionMgr.Current.StartedAt);
    }
}
