using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Bogus;

namespace Pim.UnitTests.Harness.Generators;

/// <summary>
/// PC activity stream generator covering AFK jitter, classification rule conflict, RecordKey mapping failure.
/// Reproducibility via new Faker().Random = new Randomizer(seed).
/// </summary>
public static class PcActivityStreamGenerator
{
    public sealed record PcActivity(
        string DeviceId,
        DateTimeOffset Timestamp,
        double DurationMs,
        string AppName,
        string? WindowTitle,
        string AfkStatus,
        string RecordKey,
        string Classification,
        string EventType);

    private static readonly string[] Apps = { "chrome.exe", "code.exe", "explorer.exe", "devenv.exe", "teams.exe", "outlook.exe", "slack.exe", "notepad.exe", "figma.exe", "spotify.exe" };
    private static readonly string[] Classifications = { "work", "communication", "entertainment", "idle", "unknown", "learning" };
    private static readonly string[] AfkStatuses = { "active", "afk", "idle" };
    private static readonly string[] EventTypes = { "window", "afk", "heartbeat" };

    /// <summary>
    /// Generate random PC activity stream.
    /// </summary>
    public static List<PcActivity> Generate(int count = 50, int seed = 42)
    {
        new Faker().Random = new Randomizer(seed);
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        for (int i = 0; i < count; i++)
        {
            var app = faker.PickRandom(Apps);
            var afk = faker.PickRandom(AfkStatuses);
            var duration = faker.Random.Double(1000, 600000);
            var ts = baseTime.AddSeconds(i * 60 + faker.Random.Int(-10, 10));
            var recordKey = $"{faker.Random.Guid():N}_{ts.ToUnixTimeSeconds()}";
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 10):D3}",
                ts,
                duration,
                app,
                faker.Lorem.Sentence(2),
                afk,
                recordKey,
                faker.PickRandom(Classifications),
                faker.PickRandom(EventTypes)));
        }
        return list;
    }

    /// <summary>
    /// Generate stream with AFK jitter: rapid active/afk flapping within seconds.
    /// </summary>
    public static List<PcActivity> GenerateWithAfkJitter(int count = 30, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        var ts = baseTime;
        string lastAfk = "active";
        for (int i = 0; i < count; i++)
        {
            // jitter: 40% chance to flip AFK within 1-5 seconds
            var jitter = faker.Random.Bool(0.4f);
            if (jitter)
            {
                lastAfk = lastAfk == "active" ? "afk" : "active";
                ts = ts.AddSeconds(faker.Random.Int(1, 5));
            }
            else
            {
                lastAfk = faker.PickRandom(AfkStatuses);
                ts = ts.AddSeconds(faker.Random.Int(10, 120));
            }
            var duration = jitter
                ? faker.Random.Double(500, 5000)
                : faker.Random.Double(10000, 300000);
            var app = faker.PickRandom(Apps);
            var recordKey = $"jitter_{faker.Random.Hash(8)}_{ts.ToUnixTimeMilliseconds()}";
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 5):D3}",
                ts,
                duration,
                app,
                faker.Lorem.Words(2).Aggregate((a, b) => a + " " + b),
                lastAfk,
                recordKey,
                lastAfk == "afk" ? "idle" : faker.PickRandom(Classifications),
                "afk"));
        }
        return list;
    }

    /// <summary>
    /// Generate stream with classification rule conflicts: same app maps to multiple classifications.
    /// </summary>
    public static List<PcActivity> GenerateWithClassificationConflict(int count = 30, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        // pick 3 apps to be conflicting
        var conflictApps = faker.Random.ListItems(Apps, 3);
        var conflictRules = new Dictionary<string, string[]>
        {
            { conflictApps[0], new[] { "work", "entertainment" } },
            { conflictApps[1], new[] { "communication", "work" } },
            { conflictApps[2], new[] { "learning", "entertainment", "work" } },
        };
        for (int i = 0; i < count; i++)
        {
            var app = faker.PickRandom(Apps);
            string classification;
            if (conflictRules.TryGetValue(app, out var opts))
            {
                // alternate between conflicting classifications
                classification = opts[i % opts.Length];
            }
            else
            {
                classification = faker.PickRandom(Classifications);
            }
            var ts = baseTime.AddMinutes(i * 5 + faker.Random.Int(-2, 2));
            var recordKey = $"conflict_{faker.Random.Hash(6)}_{ts.ToUnixTimeSeconds()}";
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 5):D3}",
                ts,
                faker.Random.Double(5000, 600000),
                app,
                faker.Lorem.Sentence(2),
                "active",
                recordKey,
                classification,
                "window"));
        }
        return list;
    }

    /// <summary>
    /// Generate stream with RecordKey mapping failures: duplicate, null-like, malformed keys.
    /// </summary>
    public static List<PcActivity> GenerateWithRecordKeyFailures(int count = 30, int seed = 42)
    {
        var faker = new Faker("zh_CN");
        faker.Random = new Randomizer(seed);
        var baseTime = new DateTimeOffset(2026, 7, 6, 11, 0, 0, TimeSpan.FromHours(8));
        var list = new List<PcActivity>(count);
        var sharedKey = $"shared_{faker.Random.Hash(12)}";
        for (int i = 0; i < count; i++)
        {
            var failureType = faker.Random.Int(0, 4);
            string recordKey = failureType switch
            {
                0 => sharedKey, // duplicate key
                1 => string.Empty, // empty key
                2 => $"malformed key with spaces {i} / {faker.Lorem.Word()}", // malformed
                3 => $"{faker.Random.Guid():N}", // missing timestamp suffix
                _ => $"ok_{faker.Random.Hash(8)}_{baseTime.AddMinutes(i).ToUnixTimeSeconds()}"
            };
            // inject null-like string for some
            if (failureType == 1 && faker.Random.Bool(0.5f))
                recordKey = "null";
            if (failureType == 2 && faker.Random.Bool(0.3f))
                recordKey = $"key,with,commas,{i}";
            var ts = baseTime.AddMinutes(i * 3);
            list.Add(new PcActivity(
                $"device_{faker.Random.Int(1, 5):D3}",
                ts,
                faker.Random.Double(1000, 300000),
                faker.PickRandom(Apps),
                faker.Lorem.Sentence(1),
                faker.PickRandom(AfkStatuses),
                recordKey,
                faker.PickRandom(Classifications),
                faker.PickRandom(EventTypes)));
        }
        return list;
    }

    /// <summary>
    /// Try to sample PC activity stream from DB; fallback to synthetic <see cref="Generate"/> on failure.
    /// </summary>
    public static List<PcActivity> FromDb(int seed = 42)
    {
        try
        {
            var sampled = TrySampleFromDb(50);
            if (sampled != null && sampled.Count > 0)
                return sampled;
        }
        catch
        {
            // fallback
        }
        return Generate(50, seed);
    }

    private static List<PcActivity>? TrySampleFromDb(int count)
    {
        try
        {
            var psi = new ProcessStartInfo("docker",
                $"exec 1Panel-postgresql-rIyE psql -U pim -d pim_prod -t -A -F\",\" -c \"SELECT device_id, timestamp, duration, app_name, window_title, afk_status, record_key, classification, event_type FROM pc_aw_events ORDER BY random() LIMIT {count}\"")
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
            var list = new List<PcActivity>();
            foreach (var line in lines)
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                var device = parts[0];
                if (!DateTimeOffset.TryParse(parts[1], out var ts)) continue;
                if (!double.TryParse(parts[2], out var dur)) dur = 0;
                var app = parts.Length > 3 ? parts[3] : faker.PickRandom(Apps);
                var title = parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4] : null;
                var afk = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : "active";
                var key = parts.Length > 6 && !string.IsNullOrWhiteSpace(parts[6]) ? parts[6] : $"{faker.Random.Hash(8)}_{ts.ToUnixTimeSeconds()}";
                var cls = parts.Length > 7 && !string.IsNullOrWhiteSpace(parts[7]) ? parts[7] : "unknown";
                var evt = parts.Length > 8 && !string.IsNullOrWhiteSpace(parts[8]) ? parts[8] : "window";
                list.Add(new PcActivity(device, ts, dur, app, title, afk, key, cls, evt));
            }
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }
}
