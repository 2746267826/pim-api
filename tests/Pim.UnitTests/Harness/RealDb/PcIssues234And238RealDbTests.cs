using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Calendar;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

public sealed class PcIssues234And238RealDbTests
{
    private static PimDbContext TryCreateDbContext()
    {
        var connStr = Environment.GetEnvironmentVariable("PIM_TEST_CONN");
        Skip.If(string.IsNullOrWhiteSpace(connStr), "RealDb unavailable (PIM_TEST_CONN not set), skipping test.");

        try
        {
            using var conn = new NpgsqlConnection(connStr);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.ExecuteScalar();
        }
        catch (Exception ex)
        {
            throw new Xunit.SkipException($"RealDb connection failed: {ex.Message}");
        }

        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(connStr)
            .Options;

        return new PimDbContext(options);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Issue238_PostAwDate_QualityCheck_UsesNativeEvents_AndDoesNotRequireAwBuckets()
    {
        await using var db = TryCreateDbContext();

        var fixedNow = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var service = new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = fixedNow });

        // Query date 2026-09-10 (post-AW era, native events exist)
        var result = await service.GetQualityAsync(new DateTime(2026, 9, 10), null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Components, c => c.Key == "aw-buckets");
        Assert.DoesNotContain(result.Components, c => c.Key == "aw-events");
        Assert.Contains(result.Components, c => c.Key == "tracker-events");
        Assert.Contains(result.Components, c => c.Key == "interpreted-timeline");
        Assert.DoesNotContain(result.Issues, i => i.Code.StartsWith("missing-aw-", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task RealDb_Issue234_PostAwDate_ActivityClassification_UsesNativeEvents()
    {
        await using var db = TryCreateDbContext();

        var recomputeService = new ActivityClassificationRecomputeService(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationRuleService(db),
            new StubCurrentUserService(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            NullLogger<ActivityClassificationRecomputeService>.Instance);

        // Preview classification for rule across 2026-09-10 (where 200+ native msedge events exist)
        var rule = new SaveActivityClassificationRuleRequest(
            RuleName: "Microsoft Edge",
            Scope: "app",
            CategoryName: "浏览",
            ProjectTag: null,
            Color: "#0078d4",
            Priority: 100,
            ConditionsJson: "{\"all\":[{\"field\":\"appName\",\"op\":\"equals\",\"value\":\"msedge\"}]}",
            Confidence: 1.0,
            Explanation: null);

        var range = new ActivityClassificationApplyRangeRequest("range", "2026-09-10", "2026-09-10");

        var preview = await recomputeService.PreviewRuleAsync(rule, range, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.True(preview.AffectedRecordCount > 0, $"Expected affected native events on 2026-09-10, got {preview.AffectedRecordCount}");
        Assert.Contains(preview.Samples, s => s.AppName != null && s.AppName.Equals("msedge", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubCurrentUserService : ICurrentUserService
    {
        public StubCurrentUserService(Guid userId)
        {
            UserId = userId;
        }

        public Guid? UserId { get; }
        public string? Email => "test@test.local";
        public string? Role => "User";
        public bool IsAuthenticated => true;
    }
}
