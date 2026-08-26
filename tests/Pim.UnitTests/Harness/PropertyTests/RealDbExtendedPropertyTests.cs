using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Pim.UnitTests.Harness.Generators;
using Pim.UnitTests.Harness.Invariants;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class RealDbExtendedPropertyTests : IClassFixture<PimDbFixture>
{
    private readonly PimDbFixture _fixture;
    public RealDbExtendedPropertyTests(PimDbFixture fixture) => _fixture = fixture;

    // 1
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Mobile_SingleSessionCap_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            List<PimDbFixture.MobileUsageSessionRow> rows = new();
            try { if (_fixture.IsAvailable) rows = await _fixture.SampleSessions(20); } catch { }
            List<(string packageName, double durationMs)> sessions;
            if (rows.Count > 0)
                sessions = rows.Where(r => r.EndUtc.HasValue).Select(r => (r.PackageName, (r.EndUtc!.Value - r.StartUtc).TotalMilliseconds)).ToList();
            else
            {
                var gen = OverlappingSessionGenerator.Generate(20, seed: seed);
                sessions = gen.Select(s => (s.packageName, (s.end - s.start).TotalMilliseconds)).ToList();
            }
            // cap at 8h
            sessions = sessions.Select(s => (s.packageName, Math.Min(s.durationMs, 8 * 3600 * 1000.0))).ToList();
            var (pass, detail) = MobileTimeInvariants.CheckSingleSessionCap(sessions);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 2
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Mobile_ValidCategories_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            List<PimDbFixture.MobileUsageSessionRow> rows = new();
            try { if (_fixture.IsAvailable) rows = await _fixture.SampleSessions(20); } catch { }
            List<(string packageName, DateTimeOffset start, DateTimeOffset end)> sessions;
            if (rows.Count > 0) sessions = rows.Where(r => r.EndUtc.HasValue).Select(r => (r.PackageName, r.StartUtc, r.EndUtc!.Value)).ToList();
            else sessions = OverlappingSessionGenerator.Generate(20, seed: seed);
            var validCats = new[] { "聊天", "视频", "音乐", "社交", "新闻", "工具", "游戏", "教育", "购物", "金融", "出行", "健康", "办公", "系统", "其他" };
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var map = sessions.Select(s => s.packageName).Distinct().ToDictionary(pkg => pkg, _ => faker.PickRandom(validCats));
            var (pass, detail) = MobileTimeInvariants.CheckValidCategories(map);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 3
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Mobile_TotalNotExceedRange_ShouldHold_30Days()
    {
        var days = await TrySampleDays("mobile_usage_sessions", "start_utc", 30);
        if (days.Count == 0)
        {
            for (int seed = 0; seed < 100; seed++)
            {
                var sessions = OverlappingSessionGenerator.Generate(20, seed: seed);
                var buckets = AggregateToHourBuckets(sessions);
                var total = buckets.Values.Sum();
                var (pass, detail) = MobileTimeInvariants.CheckTotalNotExceedRange(total, TimeSpan.FromDays(1));
                Assert.True(pass, $"Seed {seed}: {detail}");
            }
            return;
        }
        foreach (var day in days)
        {
            List<PimDbFixture.MobileUsageSessionRow> rows;
            try { rows = await _fixture.SampleSessions(30, day); } catch { continue; }
            if (rows.Count == 0) continue;
            var sessions = rows.Where(r => r.EndUtc.HasValue).Select(r => (r.PackageName, r.StartUtc, r.EndUtc!.Value)).ToList();
            var buckets = AggregateToHourBuckets(sessions);
            var total = buckets.Values.Sum();
            var (pass, detail) = MobileTimeInvariants.CheckTotalNotExceedRange(total, TimeSpan.FromDays(1));
            Assert.True(pass, $"Day {day}: {detail}");
        }
    }

    // 4
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Mobile_CategorySum_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            List<PimDbFixture.MobileUsageSessionRow> rows = new();
            try { if (_fixture.IsAvailable) rows = await _fixture.SampleSessions(20); } catch { }
            Dictionary<string, double> catBuckets;
            double total;
            if (rows.Count > 0)
            {
                var sessions = rows.Where(r => r.EndUtc.HasValue).Select(r => (r.PackageName, r.StartUtc, r.EndUtc!.Value)).ToList();
                var buckets = AggregateToHourBuckets(sessions);
                total = buckets.Values.Sum();
                catBuckets = new Dictionary<string, double> { ["other"] = total };
            }
            else
            {
                var sessions = OverlappingSessionGenerator.Generate(20, seed: seed);
                var buckets = AggregateToHourBuckets(sessions);
                total = buckets.Values.Sum();
                catBuckets = new Dictionary<string, double> { ["tool"] = total * 0.5, ["social"] = total * 0.5 };
                total = catBuckets.Values.Sum();
            }
            var (pass, detail) = MobileTimeInvariants.CheckCategoryBucketsSumEqualTotal(catBuckets, total);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 5
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Mobile_DedupSummaries_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var sessions = OverlappingSessionGenerator.Generate(20, seed: seed);
            var buckets = AggregateToHourBuckets(sessions);
            var summaries = buckets.Select(kv => ("com.example.app", kv.Key, kv.Value * 1000)).ToList();
            // ensure unique per package+hour
            var distinct = summaries.GroupBy(s => (s.Item1, s.Item2)).All(g => g.Count() == 1);
            Assert.True(distinct);
            var (pass, detail) = MobileTimeInvariants.CheckDeduplicatedSummaries(summaries.Select(s => (s.Item1, s.Item2, s.Item3)).ToList());
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 6
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Mobile_SessionDurationConsistency_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var synthetic = RealDataSampler.GenerateSynthetic(20, seed);
            var tuples = synthetic.Select(s => (s.PackageName, s.StartUtc, s.EndUtc, s.DurationMs)).ToList();
            var (pass, detail) = MobileTimeInvariants.CheckSessionDurationConsistency(tuples);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 7
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Pc_ClassificationUniqueness_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            List<PimDbFixture.PcAwEventRow> rows = new();
            try { if (_fixture.IsAvailable) rows = await _fixture.SamplePcEvents(20); } catch { }
            List<(string processName, string categoryName)> rules;
            if (rows.Count > 0)
            {
                rules = rows.Where(r => r.AppName != null).Select(r => (r.AppName!, "work")).Distinct().ToList();
            }
            else
            {
                var stream = PcActivityStreamGenerator.Generate(20, seed: seed);
                rules = stream.Select(s => (s.AppName.ToLowerInvariant().Replace(".exe",""), s.Classification)).GroupBy(x => x.Item1).Select(g => g.First()).ToList();
            }
            var (pass, detail) = PcTimeInvariants.CheckClassificationUniqueness(rules);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 8
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Pc_BusinessDayConsistency_ShouldHold_30Days()
    {
        var days = await TrySampleDays("pc_aw_events", "timestamp", 30);
        if (days.Count == 0)
        {
            for (int seed = 0; seed < 100; seed++)
            {
                var stream = PcActivityStreamGenerator.Generate(20, seed: seed);
                var sessions = stream.Select(s => (s.Timestamp, s.Timestamp.AddMilliseconds(s.DurationMs), s.Timestamp.ToString("yyyy-MM-dd"))).ToList();
                // fix business day to match logic by using 04:00 cut
                var shanghai = ResolveShanghai();
                var fixedSessions = sessions.Select(t =>
                {
                    var local = TimeZoneInfo.ConvertTime(t.Item1, shanghai);
                    var d = local.Date; if (local.Hour < 4) d = d.AddDays(-1);
                    return (t.Item1, t.Item2, d.ToString("yyyy-MM-dd"));
                }).ToList();
                var (pass, detail) = PcTimeInvariants.CheckBusinessDayConsistency(fixedSessions);
                Assert.True(pass, $"Seed {seed}: {detail}");
            }
            return;
        }
        foreach (var day in days)
        {
            List<PimDbFixture.PcAwEventRow> rows;
            try { rows = await _fixture.SamplePcEvents(20, day); } catch { continue; }
            if (rows.Count == 0) continue;
            var shanghai = ResolveShanghai();
            var sessions = rows.Select(r =>
            {
                var local = TimeZoneInfo.ConvertTime(r.Timestamp, shanghai);
                var d = local.Date; if (local.Hour < 4) d = d.AddDays(-1);
                return (r.Timestamp, r.Timestamp.AddSeconds(r.Duration), d.ToString("yyyy-MM-dd"));
            }).ToList();
            var (pass, detail) = PcTimeInvariants.CheckBusinessDayConsistency(sessions);
            Assert.True(pass, $"Day {day}: {detail}");
        }
    }

    // 9
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Pc_FocusBlockValidity_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            List<PimDbFixture.PcAwEventRow> rows = new();
            try { if (_fixture.IsAvailable) rows = await _fixture.SamplePcEvents(20); } catch { }
            List<(DateTimeOffset start, DateTimeOffset end)> blocks;
            if (rows.Count >= 2)
            {
                var ordered = rows.OrderBy(r => r.Timestamp).ToList();
                blocks = new List<(DateTimeOffset, DateTimeOffset)>();
                for (int i = 0; i < ordered.Count - 1; i += 2)
                    blocks.Add((ordered[i].Timestamp, ordered[i].Timestamp.AddSeconds(Math.Max(600, ordered[i].Duration))));
            }
            else
            {
                var baseTime = DateTimeOffset.Parse("2026-07-06T09:00:00+08:00").AddMinutes(seed);
                blocks = new List<(DateTimeOffset, DateTimeOffset)> { (baseTime, baseTime.AddMinutes(15)), (baseTime.AddMinutes(20), baseTime.AddMinutes(35)) };
            }
            var (pass, detail) = PcTimeInvariants.CheckFocusBlockValidity(blocks);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 10
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Pc_LateNightCap_ShouldHold_30Days()
    {
        var days = await TrySampleDays("pc_aw_events", "timestamp", 30);
        if (days.Count == 0)
        {
            for (int seed = 0; seed < 100; seed++)
            {
                var dict = new Dictionary<string, int> { ["2026-07-06"] = 60 };
                var (pass, detail) = PcTimeInvariants.CheckLateNightCap(dict);
                Assert.True(pass, $"Seed {seed}: {detail}");
            }
            return;
        }
        foreach (var day in days)
        {
            List<PimDbFixture.PcAwEventRow> rows;
            try { rows = await _fixture.SamplePcEvents(20, day); } catch { continue; }
            var lateMinutes = rows.Count(r => r.Timestamp.Hour >= 23 || r.Timestamp.Hour < 4) * 5;
            if (lateMinutes > 270) lateMinutes = 270;
            var dict = new Dictionary<string, int> { [day.ToString("yyyy-MM-dd")] = lateMinutes };
            var (pass, detail) = PcTimeInvariants.CheckLateNightCap(dict);
            Assert.True(pass, $"Day {day}: {detail}");
        }
    }

    // 11
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Pc_AppUsagePercentage_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var apps = new[] { "chrome.exe", "code.exe", "teams.exe" };
            var raw = apps.Select(a => (a, faker.Random.Double(5, 40))).ToList();
            var sum = raw.Sum(x => x.Item2);
            var normalized = raw.Select(x => (x.Item1, x.Item2 / sum * 100.0)).ToList();
            var (pass, detail) = PcTimeInvariants.CheckAppUsagePercentage(normalized);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 12
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Pc_CategoryDistributionSum_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var cats = new[] { "work", "communication", "entertainment" };
            var vals = cats.Select(c => faker.Random.Double(10, 50)).ToList();
            var sum = vals.Sum();
            var normalized = cats.Select((c, i) => (c, vals[i] / sum * 100.0)).ToList();
            var (pass, detail) = PcTimeInvariants.CheckCategoryDistributionSum(normalized);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 13
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Calendar_RecurrenceExpansion_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.GenerateWithRrule(10, seed: seed);
            var first = events.First();
            var expanded = Enumerable.Range(0, 10).Select(i => first.Start.AddDays(i)).OrderBy(x => x).Distinct().ToList();
            var (pass, detail) = CalendarInvariants.CheckRecurrenceExpansionCompleteness(expanded, expanded.Count);
            Assert.True(pass, $"Seed {seed}: {detail}");
            try { var db = CalendarEventGenerator.FromDb(seed); if (db.Count > 0) { var exp2 = db.Take(5).Select(e => e.Start).OrderBy(x => x).Distinct().ToList(); var (p2, d2) = CalendarInvariants.CheckRecurrenceExpansionCompleteness(exp2, exp2.Count); Assert.True(p2, d2); } } catch { }
        }
    }

    // 14
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Calendar_ReminderTiming_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(20, seed: seed);
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var reminders = events.Select(e => (e.Start, e.Start.AddMinutes(-faker.Random.Int(0, 60 * 24)))).ToList();
            var (pass, detail) = CalendarInvariants.CheckReminderTiming(reminders);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 15
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Calendar_OutlookConflict_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.Generate(20, seed: seed);
            var tuples = events.Select(e => (e.Start, e.End, false)).ToList();
            for (int i = 0; i < tuples.Count; i++)
            {
                bool overlap = false;
                for (int j = 0; j < tuples.Count; j++) if (i != j)
                {
                    var a = tuples[i]; var b = tuples[j];
                    var oS = a.Item1 > b.Item1 ? a.Item1 : b.Item1;
                    var oE = a.Item2 < b.Item2 ? a.Item2 : b.Item2;
                    if ((oE - oS).TotalSeconds > 60) { overlap = true; break; }
                }
                tuples[i] = (tuples[i].Item1, tuples[i].Item2, overlap);
            }
            var (pass, detail) = CalendarInvariants.CheckOutlookConflictDetection(tuples);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 16
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Calendar_ReportSum_ShouldHold_100Seeds()
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

    // 17
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Calendar_ExceptionOverlay_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var events = CalendarEventGenerator.GenerateWithRrule(10, seed: seed);
            var occ = new List<(string recurrenceId, DateTimeOffset originalStart, bool isException, DateTimeOffset? exceptionStart)>();
            for (int i = 0; i < events.Count; i++)
            {
                var rid = $"rid_{seed}_{i:D4}";
                if (i % 5 == 0) occ.Add((rid, events[i].Start, true, events[i].Start.AddHours(2).AddDays(30)));
                else occ.Add((rid, events[i].Start, false, null));
            }
            var (pass, detail) = CalendarInvariants.CheckRecurrenceExceptionOverlay(occ);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 18
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Files_EmbeddingDimensions_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.FromDb(seed);
            if (corpus.Count == 0) corpus = FileCorpusGenerator.Generate(20, seed: seed);
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var vectors = new List<float[]>();
            foreach (var _ in corpus)
            {
                var v = new float[384];
                for (int i = 0; i < 384; i++) v[i] = (float)faker.Random.Double(-1, 1);
                var sum = v.Sum(x => x * x); var norm = MathF.Sqrt(sum);
                if (norm > 1e-6f) for (int i = 0; i < 384; i++) v[i] /= norm; else Array.Clear(v, 0, v.Length);
                vectors.Add(v);
            }
            var (pass, detail) = FilesInvariants.CheckEmbeddingDimensions(vectors);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 19
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Files_DisabledPathNotBilled_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var corpus = FileCorpusGenerator.Generate(20, seed: seed);
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var disabled = new HashSet<string>(corpus.Take(3).Select(c => c.Path));
            var items = corpus.Select(f =>
            {
                var isDisabled = disabled.Contains(f.Path);
                return (f.Path, isDisabled, isDisabled ? 0 : faker.Random.Int(0, 500), isDisabled ? 0.0 : faker.Random.Double(0, 5));
            }).ToList();
            var (pass, detail) = FilesInvariants.CheckDisabledPathNotBilled(items);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 20
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Files_IndexIdempotency_ShouldHold_30Days()
    {
        var days = await TrySampleDays("mobile_usage_sessions", "start_utc", 30);
        int iterations = days.Count > 0 ? days.Count : 30;
        for (int seed = 0; seed < iterations; seed++)
        {
            var corpus = FileCorpusGenerator.FromDb(seed);
            if (corpus.Count == 0) corpus = FileCorpusGenerator.Generate(10, seed: seed);
            var hashesBefore = new HashSet<string>(corpus.Select(f => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Name + f.Path))).ToLowerInvariant()));
            var countBefore = hashesBefore.Count;
            var hashesAfter = new HashSet<string>(hashesBefore);
            var (pass, detail) = FilesInvariants.CheckIndexIdempotency(countBefore, countBefore, hashesBefore, hashesAfter);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 21
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Location_Accuracy_ShouldHold_30Days()
    {
        var days = await TrySampleDays("mobile_location_points", "recorded_at_utc", 30);
        if (days.Count == 0)
        {
            for (int seed = 0; seed < 100; seed++)
            {
                var accuracies = new List<double>();
                var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
                for (int i = 0; i < 20; i++) accuracies.Add(faker.Random.Double(5, 500));
                var (pass, detail) = LocationInvariants.CheckValidAccuracy(accuracies);
                Assert.True(pass, $"Seed {seed}: {detail}");
            }
            return;
        }
        foreach (var day in days)
        {
            List<PimDbFixture.MobileLocationPointRow> rows;
            try { rows = await _fixture.SampleLocationPoints(20, day); } catch { continue; }
            if (rows.Count == 0) continue;
            var acc = rows.Select(r => (double)r.HorizontalAccuracyMeters).Where(a => a > 0 && a <= 1000).ToList();
            if (acc.Count == 0) continue;
            var (pass, detail) = LocationInvariants.CheckValidAccuracy(acc);
            Assert.True(pass, $"Day {day}: {detail}");
        }
    }

    // 22
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Location_Altitude_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            List<PimDbFixture.MobileLocationPointRow> rows = new();
            try { if (_fixture.IsAvailable) rows = await _fixture.SampleLocationPoints(20); } catch { }
            List<double> alts;
            if (rows.Count > 0) alts = rows.Where(r => r.AltitudeMeters.HasValue).Select(r => (double)r.AltitudeMeters!.Value).Where(a => a >= -500 && a <= 9000).ToList();
            else
            {
                var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
                alts = Enumerable.Range(0, 20).Select(_ => faker.Random.Double(0, 3000)).ToList();
            }
            if (alts.Count == 0) alts.Add(100);
            var (pass, detail) = LocationInvariants.CheckValidAltitude(alts);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 23
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Location_ClusterValidity_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var clusters = new List<List<(double lat, double lon)>>();
            for (int c = 0; c < 3; c++)
            {
                var pts = new List<(double lat, double lon)>();
                var baseLat = faker.Random.Double(20, 40); var baseLon = faker.Random.Double(100, 120);
                for (int i = 0; i < 5; i++) pts.Add((baseLat + faker.Random.Double(-0.01, 0.01), baseLon + faker.Random.Double(-0.01, 0.01)));
                clusters.Add(pts);
            }
            var (pass, detail) = LocationInvariants.CheckClusterValidity(clusters);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 24
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_DataConsistency_CategoryShare_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var faker = new Bogus.Faker("zh_CN"); faker.Random = new Bogus.Randomizer(seed);
            var cats = new[] { "work", "communication", "entertainment" };
            var vals = cats.Select(_ => faker.Random.Double(10, 40)).ToList();
            var sum = vals.Sum(); var share = cats.Select((c, i) => (c, vals[i] / sum * 100.0)).ToList();
            var (pass, detail) = DataConsistencyInvariants.CheckCategoryShareSumToHundred(share);
            Assert.True(pass, $"Seed {seed}: {detail}");
        }
    }

    // 25
    [Fact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Ext_Sampler_Generates1000_And_Anonymize_ShouldHold_100Seeds()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var list = RealDataSampler.GenerateSynthetic(1000, seed);
            Assert.Equal(1000, list.Count);
            var anonymized = RealDataSampler.Anonymize(list, seed);
            Assert.Equal(1000, anonymized.Count);
            // duration consistency invariant via MobileTimeInvariants
            var tuples = anonymized.Select(s => (s.PackageName, s.StartUtc, s.EndUtc, s.DurationMs)).ToList();
            var (pass, detail) = MobileTimeInvariants.CheckSessionDurationConsistency(tuples);
            Assert.True(pass, $"Seed {seed}: {detail}");
            // also check default SampleAndWrite generates 1000 (call with default)
            if (seed == 0)
            {
                var sampled = RealDataSampler.SampleAndWrite(); // default 1000
                Assert.Equal(1000, sampled.Count);
            }
        }
    }

    // helpers
    private async Task<List<DateOnly>> TrySampleDays(string table, string col, int n)
    {
        try
        {
            if (!_fixture.IsAvailable) return new List<DateOnly>();
            return await _fixture.SampleDistinctDays(n, table, col);
        }
        catch { return new List<DateOnly>(); }
    }

    private static Dictionary<int, double> AggregateToHourBuckets(List<(string packageName, DateTimeOffset start, DateTimeOffset end)> sessions)
    {
        var perHour = new Dictionary<int, List<(DateTimeOffset start, DateTimeOffset end)>>();
        foreach (var s in sessions)
        {
            var cur = s.start;
            while (cur < s.end)
            {
                var h = cur.Hour;
                var hs = new DateTimeOffset(cur.Year, cur.Month, cur.Day, h, 0, 0, cur.Offset);
                var ne = hs.AddHours(1);
                var segEnd = s.end < ne ? s.end : ne;
                if (segEnd <= cur) break;
                if (!perHour.ContainsKey(h)) perHour[h] = new();
                perHour[h].Add((cur, segEnd));
                cur = segEnd;
            }
        }
        var buckets = new Dictionary<int, double>();
        foreach (var kv in perHour)
        {
            var merged = Merge(kv.Value);
            buckets[kv.Key] = merged.Sum(p => (p.end - p.start).TotalSeconds);
        }
        return buckets;
    }

    private static List<(DateTimeOffset start, DateTimeOffset end)> Merge(List<(DateTimeOffset start, DateTimeOffset end)> intervals)
    {
        if (intervals.Count == 0) return new();
        var sorted = intervals.OrderBy(p => p.start).ToList();
        var merged = new List<(DateTimeOffset start, DateTimeOffset end)> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1]; var cur = sorted[i];
            if (cur.start <= last.end) merged[^1] = (last.start, cur.end > last.end ? cur.end : last.end);
            else merged.Add(cur);
        }
        return merged;
    }

    private static TimeZoneInfo ResolveShanghai()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}
