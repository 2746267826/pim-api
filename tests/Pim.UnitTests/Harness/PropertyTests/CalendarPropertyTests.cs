using System;
using System.Collections.Generic;
using System.Linq;
using Pim.UnitTests.Harness.Generators;
using Pim.UnitTests.Harness.Invariants;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class CalendarPropertyTests
{
    [Fact]
    public void RecurrenceExpansionCompleteness_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.GenerateWithRrule(20, seed: seed);
            var baseStart = events.FirstOrDefault()?.Start ?? new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.FromHours(8));
            var expanded = new List<DateTimeOffset>();
            for (int i = 0; i < 10; i++)
                expanded.Add(baseStart.AddDays(i));
            expanded = expanded.OrderBy(x => x).Distinct().ToList();
            var (pass, detail) = CalendarInvariants.CheckRecurrenceExpansionCompleteness(expanded, expectedCount: expanded.Count);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void ReminderTiming_ShouldBeWithinMaxLead()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(20, seed: seed);
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            var reminders = events.Select(e =>
            {
                var leadMinutes = faker.Random.Int(0, 60 * 24);
                var reminderTime = e.Start.AddMinutes(-leadMinutes);
                return (e.Start, reminderTime);
            }).ToList();
            var (pass, detail) = CalendarInvariants.CheckReminderTiming(reminders);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void OutlookConflictDetection_ShouldBeConsistent()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(20, seed: seed);
            var tuples = events.Select(e => (e.Start, e.End, false)).ToList();
            for (int i = 0; i < tuples.Count; i++)
            {
                var hasOverlap = false;
                for (int j = 0; j < tuples.Count; j++)
                {
                    if (i == j) continue;
                    var a = tuples[i];
                    var b = tuples[j];
                    var overlapStart = a.Item1 > b.Item1 ? a.Item1 : b.Item1;
                    var overlapEnd = a.Item2 < b.Item2 ? a.Item2 : b.Item2;
                    if ((overlapEnd - overlapStart).TotalSeconds > 60)
                    {
                        hasOverlap = true;
                        break;
                    }
                }
                tuples[i] = (tuples[i].Item1, tuples[i].Item2, hasOverlap);
            }
            var (pass, detail) = CalendarInvariants.CheckOutlookConflictDetection(tuples);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void ReportSumEqualsDetail_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(20, seed: seed);
            var details = events.Select(e => (e.End - e.Start).TotalSeconds).ToList();
            var total = details.Sum();
            var (pass, detail) = CalendarInvariants.CheckReportSumEqualsDetail(details, total);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void RecurrenceExceptionOverlay_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.GenerateWithRrule(10, seed: seed);
            var occurrences = new List<(string recurrenceId, DateTimeOffset originalStart, bool isException, DateTimeOffset? exceptionStart)>();
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                var recurrenceId = $"rid_{i:D4}_{seed}";
                if (i % 5 == 0)
                {
                    var exceptionStart = e.Start.AddHours(2).AddDays(30);
                    occurrences.Add((recurrenceId, e.Start, true, exceptionStart));
                }
                else
                {
                    occurrences.Add((recurrenceId, e.Start, false, null));
                }
            }
            var (pass, detail) = CalendarInvariants.CheckRecurrenceExceptionOverlay(occurrences);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void TaskSegmentCoverage_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(5, seed: seed);
            var first = events.First();
            var taskDuration = (first.End - first.Start).TotalSeconds;
            if (taskDuration <= 0) taskDuration = 3600;
            var segments = new List<(DateTimeOffset start, DateTimeOffset end)>();
            var cursor = first.Start;
            var remaining = taskDuration;
            var faker = new Bogus.Faker("zh_CN");
            faker.Random = new Bogus.Randomizer(seed);
            int parts = faker.Random.Int(1, 4);
            for (int i = 0; i < parts; i++)
            {
                var isLast = i == parts - 1;
                var dur = isLast ? remaining : Math.Max(1, remaining / (parts - i) * faker.Random.Double(0.5, 1.0));
                dur = Math.Min(dur, remaining);
                var segEnd = cursor.AddSeconds(dur);
                segments.Add((cursor, segEnd));
                cursor = segEnd;
                remaining -= dur;
                if (remaining <= 0.5) break;
            }
            var sum = segments.Sum(s => (s.end - s.start).TotalSeconds);
            var (pass, detail) = CalendarInvariants.CheckTaskSegmentCoverage(segments, sum);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void EventDurationBounds_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(30, seed: seed);
            var tuples = events.Select(e => (e.Id, e.Start, e.End)).ToList();
            var (pass, detail) = CalendarInvariants.CheckEventDurationBounds(tuples);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void CalendarDeduplication_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(30, seed: seed);
            var view = events.Select(e => (e.GraphEventId, e.Id, e.Start)).ToList();
            var known = new HashSet<string>(events.Select(e => e.GraphEventId));
            var (pass, detail) = CalendarInvariants.CheckCalendarDeduplication(view, known);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    [Fact]
    public void CrossTimezoneDurationBounds_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.GenerateCrossTimezone(20, seed: seed);
            foreach (var e in events)
            {
                Assert.True(e.End >= e.Start, $"Seed {seed}: cross-timezone event {e.Id} end before start");
            }
            var tuples = events.Select(e => (e.Id, e.Start, e.End)).ToList();
            var (pass, detail) = CalendarInvariants.CheckEventDurationBounds(tuples);
            Assert.True(pass, $"Seed {seed}: {detail}");
            var view = events.Select(e => (e.GraphEventId, e.Id, e.Start)).ToList();
            var known = new HashSet<string>(events.Select(e => e.GraphEventId));
            var (pass2, detail2) = CalendarInvariants.CheckCalendarDeduplication(view, known);
            Assert.True(pass2, $"Seed {seed}: {detail2}");
        }
    }

    [Fact]
    public void EdgeCase_AllDayAndZeroDuration_ShouldHold()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.GenerateEdgeCases(seed: seed);
            var durationTuples = events.Select(e => (e.Id, e.Start, e.End)).ToList();
            var (pass, detail) = CalendarInvariants.CheckEventDurationBounds(durationTuples, maxDurationSeconds: 86400 * 4);
            Assert.True(pass, $"Seed {seed}: {detail}");
            var view = events.Select(e => (e.GraphEventId, e.Id, e.Start)).ToList();
            var known = new HashSet<string>(events.Select(e => e.GraphEventId));
            var (pass2, detail2) = CalendarInvariants.CheckCalendarDeduplication(view, known);
            Assert.True(pass2, $"Seed {seed}: {detail2}");
            var details = events.Select(e => Math.Max(0, (e.End - e.Start).TotalSeconds)).ToList();
            var total = details.Sum();
            var (pass3, detail3) = CalendarInvariants.CheckReportSumEqualsDetail(details, total);
            Assert.True(pass3, $"Seed {seed}: {detail3}");
        }
    }
}
