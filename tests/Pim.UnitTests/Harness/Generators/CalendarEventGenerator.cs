using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Bogus;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// Calendar event generator covering cross-timezone, RRULE, all-day and 0-duration edge cases.
/// Reproducibility via new Faker().Random = new Randomizer(seed).
/// </summary>
public static class CalendarEventGenerator
{
    public sealed record CalendarEvent(
        string Id,
        string GraphEventId,
        string Title,
        DateTimeOffset Start,
        DateTimeOffset End,
        string TimeZoneId,
        string? RRule,
        bool IsAllDay,
        string CalendarId,
        string OrganizerEmail);

    private static readonly string[] TimeZones =
    {
        "UTC", "Asia/Shanghai", "Asia/Tokyo", "America/New_York",
        "America/Los_Angeles", "Europe/London", "Europe/Berlin", "Australia/Sydney"
    };

    private static readonly string[] RRules =
    {
        "FREQ=DAILY;COUNT=10",
        "FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=20",
        "FREQ=MONTHLY;BYMONTHDAY=15;COUNT=12",
        "FREQ=YEARLY;BYMONTH=1;BYMONTHDAY=1;COUNT=5",
        "FREQ=WEEKLY;INTERVAL=2;UNTIL=20261231T000000Z",
        "FREQ=DAILY;INTERVAL=3;COUNT=30",
    };

    /// <summary>
    /// Generate random calendar events (mixed).
    /// </summary>
    public static List<CalendarEvent> Generate(int count = 50, int seed = 42)
    {
        new Faker().Random = new Randomizer(seed);
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var list = new List<CalendarEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var tz = faker.PickRandom(TimeZones);
            var offset = TimeZoneOffset(tz, faker);
            var start = baseDate.AddDays(faker.Random.Int(0, 364))
                .AddHours(faker.Random.Int(0, 23))
                .AddMinutes(faker.Random.Int(0, 59))
                .ToOffset(offset);
            var durationMinutes = faker.Random.Int(15, 240);
            var end = start.AddMinutes(durationMinutes);
            var hasRrule = faker.Random.Bool(0.2f);
            var rrule = hasRrule ? faker.PickRandom(RRules) : null;
            list.Add(new CalendarEvent(
                Guid.NewGuid().ToString("N"),
                $"graph_{faker.Random.Hash(16)}",
                faker.Lorem.Sentence(3),
                start,
                end,
                tz,
                rrule,
                false,
                faker.Random.Guid().ToString("N"),
                faker.Internet.Email()));
        }
        return list;
    }

    /// <summary>
    /// Generate cross-timezone events where start/end are expressed in different zone offsets.
    /// </summary>
    public static List<CalendarEvent> GenerateCrossTimezone(int count = 20, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseDate = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var list = new List<CalendarEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var tzStart = faker.PickRandom(TimeZones);
            var tzEnd = faker.PickRandom(TimeZones);
            // ensure cross-timezone pair differs
            if (tzEnd == tzStart) tzEnd = TimeZones[(Array.IndexOf(TimeZones, tzStart) + 1) % TimeZones.Length];
            var startOffset = TimeZoneOffset(tzStart, faker);
            var endOffset = TimeZoneOffset(tzEnd, faker);
            var start = baseDate.AddDays(faker.Random.Int(0, 30)).ToOffset(startOffset);
            // end is 1-4 hours later but expressed in different offset
            var end = start.AddHours(faker.Random.Int(1, 4)).ToOffset(endOffset);
            list.Add(new CalendarEvent(
                Guid.NewGuid().ToString("N"),
                $"graph_ct_{faker.Random.Hash(8)}",
                $"CT-{faker.Lorem.Word()}",
                start,
                end,
                $"{tzStart}->{tzEnd}",
                null,
                false,
                faker.Random.Guid().ToString("N"),
                faker.Internet.Email()));
        }
        return list;
    }

    /// <summary>
    /// Generate RRULE recurring events.
    /// </summary>
    public static List<CalendarEvent> GenerateWithRrule(int count = 20, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseDate = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.FromHours(8));
        var list = new List<CalendarEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var rrule = faker.PickRandom(RRules);
            var tz = faker.PickRandom(TimeZones);
            var offset = TimeZoneOffset(tz, faker);
            var start = baseDate.AddDays(faker.Random.Int(0, 60)).ToOffset(offset);
            var end = start.AddMinutes(faker.Random.Int(30, 120));
            list.Add(new CalendarEvent(
                Guid.NewGuid().ToString("N"),
                $"graph_rrule_{faker.Random.Hash(8)}",
                $"RRULE-{faker.Lorem.Word()}",
                start,
                end,
                tz,
                rrule,
                false,
                faker.Random.Guid().ToString("N"),
                faker.Internet.Email()));
        }
        return list;
    }

    /// <summary>
    /// Generate edge cases: all-day events and 0-duration (instant) events.
    /// </summary>
    public static List<CalendarEvent> GenerateEdgeCases(int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var list = new List<CalendarEvent>();
        var baseDay = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.FromHours(8));

        // all-day events (00:00 -> 00:00 next day, IsAllDay=true)
        for (int i = 0; i < 10; i++)
        {
            var day = baseDay.AddDays(faker.Random.Int(0, 30));
            var start = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, day.Offset);
            var end = start.AddDays(1);
            // randomly multi-day all-day
            if (faker.Random.Bool(0.3f)) end = end.AddDays(faker.Random.Int(1, 3));
            list.Add(new CalendarEvent(
                Guid.NewGuid().ToString("N"),
                $"graph_allday_{i:D3}",
                $"AllDay-{faker.Lorem.Word()}",
                start,
                end,
                "Asia/Shanghai",
                null,
                true,
                faker.Random.Guid().ToString("N"),
                faker.Internet.Email()));
        }

        // 0-duration events
        for (int i = 0; i < 10; i++)
        {
            var dt = baseDay.AddDays(faker.Random.Int(0, 30)).AddHours(faker.Random.Int(0, 23)).AddMinutes(faker.Random.Int(0, 59));
            list.Add(new CalendarEvent(
                Guid.NewGuid().ToString("N"),
                $"graph_zerodur_{i:D3}",
                $"ZeroDur-{faker.Lorem.Word()}",
                dt,
                dt,
                faker.PickRandom(TimeZones),
                null,
                false,
                faker.Random.Guid().ToString("N"),
                faker.Internet.Email()));
        }

        // mix: shuffled deterministically
        return list.OrderBy(_ => faker.Random.Int(0, 10000)).ToList();
    }

    /// <summary>
    /// Try to sample calendar events from DB; fallback to synthetic <see cref="Generate"/> on failure.
    /// Keeps reproducibility via seed for fallback path.
    /// </summary>
    public static List<CalendarEvent> FromDb(int seed = 42)
    {
        try
        {
            var sampled = TrySampleFromDb(50);
            if (sampled != null && sampled.Count > 0)
                return sampled;
        }
        catch
        {
            // ignore and fallback
        }

        // fallback to deterministic synthetic
        return Generate(50, seed);
    }

    private static List<CalendarEvent>? TrySampleFromDb(int count)
    {
        try
        {
            var psi = new ProcessStartInfo("docker",
                $"exec 1Panel-postgresql-rIyE psql -U pim -d pim_prod -t -A -F\",\" -c \"SELECT id, graph_event_id, title, start_utc, end_utc, timezone, rrule, is_all_day, calendar_id, organizer_email FROM calendar_events ORDER BY random() LIMIT {count}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var faker = new Faker("zh_CN");
            faker.Random = new Randomizer(42);
            var list = new List<CalendarEvent>();
            foreach (var line in lines)
            {
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                if (!DateTimeOffset.TryParse(parts[3], out var start)) continue;
                if (!DateTimeOffset.TryParse(parts[4], out var end)) end = start;
                var tz = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : "UTC";
                var rrule = parts.Length > 6 && !string.IsNullOrWhiteSpace(parts[6]) ? parts[6] : null;
                var isAllDay = parts.Length > 7 && bool.TryParse(parts[7], out var ad) && ad;
                list.Add(new CalendarEvent(
                    parts[0],
                    parts.Length > 1 ? parts[1] : Guid.NewGuid().ToString("N"),
                    parts.Length > 2 ? parts[2] : faker.Lorem.Word(),
                    start,
                    end,
                    tz,
                    rrule,
                    isAllDay,
                    parts.Length > 8 ? parts[8] : Guid.NewGuid().ToString("N"),
                    parts.Length > 9 ? parts[9] : faker.Internet.Email()));
            }
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan TimeZoneOffset(string tz, Faker faker)
    {
        return tz switch
        {
            "UTC" => TimeSpan.Zero,
            "Asia/Shanghai" => TimeSpan.FromHours(8),
            "Asia/Tokyo" => TimeSpan.FromHours(9),
            "America/New_York" => TimeSpan.FromHours(-5 + faker.Random.Int(0, 1)),
            "America/Los_Angeles" => TimeSpan.FromHours(-8 + faker.Random.Int(0, 1)),
            "Europe/London" => TimeSpan.FromHours(faker.Random.Int(0, 1)),
            "Europe/Berlin" => TimeSpan.FromHours(1 + faker.Random.Int(0, 1)),
            "Australia/Sydney" => TimeSpan.FromHours(10 + faker.Random.Int(0, 1)),
            _ => TimeSpan.Zero
        };
    }
}
