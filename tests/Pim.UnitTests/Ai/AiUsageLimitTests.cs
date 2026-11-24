using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Ai;

/// <summary>
/// J09 connection-scoped usage limits. The tests deliberately use only persisted
/// <see cref="AiRequestLogEntity.TotalTokens"/>/<see cref="AiRequestLogEntity.EstimatedCost"/>
/// as the usage facts: no rate card, provider estimate or zero-filled unknown is allowed.
/// </summary>
public sealed class AiUsageLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task DayAndMonthWindows_UseConnectionTimeZone_StableInitiationAndLegacyFallback()
    {
        await using var db = Db();
        var admin = new AdminUser();
        var service = Service(db, admin);
        var connection = await service.CreateConnectionAsync("主连接", default);
        await service.SaveRuleAsync(connection.Id, new(connection.Limit.Revision, true,
            AiUsageLimitPeriods.Day, 80, 1_000, null, null, "Asia/Shanghai"), default);

        // 2026-11-24T15:30Z is 2026-11-24 23:30 in Shanghai.
        db.AiRequestLogs.AddRange(
            Log(connection.Id, new(2026, 11, 24, 15, 30, 0, TimeSpan.Zero), 40, null),
            // Outside the 11-24 Shanghai day, even though its StartedAt is the same UTC date.
            Log(connection.Id, new(2026, 11, 24, 16, 30, 0, TimeSpan.Zero), 60, null),
            // Stable initiation belongs to 11-24 although execution started after local midnight.
            Log(connection.Id, new(2026, 11, 24, 16, 45, 0, TimeSpan.Zero), 20, null,
                initiatedAt: new(2026, 11, 24, 15, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var day = await service.GetUsageAsync(connection.Id, AiUsageLimitPeriods.Day, "2026-11-24", default);
        Assert.Equal(60, day.TotalTokens);
        Assert.Equal(2, day.RequestCount);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 16, 0, 0, TimeSpan.Zero), day.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 16, 0, 0, TimeSpan.Zero), day.EndsAt);

        db.AiRequestLogs.AddRange(
            Log(connection.Id, new(2026, 10, 31, 15, 59, 59, TimeSpan.Zero), 5, null),
            Log(connection.Id, new(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), 7, null));
        await db.SaveChangesAsync();
        var month = await service.GetUsageAsync(connection.Id, AiUsageLimitPeriods.Month, "2026-11", default);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), month.StartsAt);
        Assert.Equal(127, month.TotalTokens);
    }

    [Fact]
    public async Task Usage_KeepsUnknownUnknown_AndNeverMixesCurrencies()
    {
        await using var db = Db();
        var service = Service(db, new AdminUser());
        var connection = await service.CreateConnectionAsync("费用连接", default);
        await service.SaveRuleAsync(connection.Id, new(connection.Limit.Revision, true,
            AiUsageLimitPeriods.Month, 80, 100, 10m, "CNY", "Asia/Shanghai"), default);
        var at = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        db.AiRequestLogs.AddRange(
            Log(connection.Id, at, 30, 2m, "CNY"),
            Log(connection.Id, at.AddMinutes(1), null, null),
            Log(connection.Id, at.AddMinutes(2), 5, 100m, "USD"),
            Log(connection.Id, at.AddMinutes(3), 0, 0m, "CNY"));
        await db.SaveChangesAsync();

        var usage = await service.GetUsageAsync(connection.Id, AiUsageLimitPeriods.Month, "2026-11", default);
        Assert.Equal(35, usage.TotalTokens);
        Assert.Equal(3, usage.TokenReportedRequestCount);
        Assert.Equal(1, usage.TokenUnknownRequestCount);
        Assert.Equal(2m, usage.EstimatedCost);
        Assert.Equal(2, usage.CostKnownRequestCount);
        Assert.Equal(2, usage.CostUnknownRequestCount);
        Assert.Equal(AiUsageLimitStatuses.Unknown, usage.TokenStatus);
        Assert.Equal(AiUsageLimitStatuses.Unknown, usage.CostStatus);
        Assert.Contains(usage.CoverageNotes, note => note.Contains("Token", StringComparison.Ordinal));
        Assert.Contains(usage.CoverageNotes, note => note.Contains("金额", StringComparison.Ordinal) ||
            note.Contains("费用", StringComparison.Ordinal));

        var blocked = await service.TryBeginAsync(connection.Id, "unknown-history", "test",
            TimeSpan.FromMinutes(1), default);
        Assert.False(blocked.Allowed);
        Assert.Equal("usage_limit_unknown", blocked.ErrorCode);

        var unknown = await service.CreateConnectionAsync("全未知连接", default);
        await service.SaveRuleAsync(unknown.Id, new(unknown.Limit.Revision, false,
            AiUsageLimitPeriods.Month, 80, 100, 10m, "CNY", "Asia/Shanghai"), default);
        db.AiRequestLogs.Add(Log(unknown.Id, at, null, null));
        await db.SaveChangesAsync();
        var unknownUsage = await service.GetUsageAsync(unknown.Id, AiUsageLimitPeriods.Month, "2026-11", default);
        Assert.Null(unknownUsage.TotalTokens);
        Assert.Null(unknownUsage.EstimatedCost);
        Assert.Equal(AiUsageLimitStatuses.Unknown, unknownUsage.TokenStatus);
        Assert.Equal(AiUsageLimitStatuses.Unknown, unknownUsage.CostStatus);
    }

    [Fact]
    public async Task ReminderEvents_MergeMetrics_DirectReachedSkipsWarning_AndReadsArePerAccount()
    {
        await using var db = Db();
        var firstAdmin = new AdminUser();
        var secondAdmin = new AdminUser();
        var service = Service(db, firstAdmin);
        var connection = await service.CreateConnectionAsync("提醒连接", default);
        await service.SaveRuleAsync(connection.Id, new(connection.Limit.Revision, true,
            AiUsageLimitPeriods.Month, 80, 100, 10m, "CNY", "Asia/Shanghai"), default);
        var at = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        db.AiRequestLogs.Add(Log(connection.Id, at, 85, 8m, "CNY"));
        await db.SaveChangesAsync();

        await service.EvaluateAsync(connection.Id, default);
        await service.EvaluateAsync(connection.Id, default);
        var warnings = await service.ListRemindersAsync(connection.Id, unreadOnly: false, 50, default);
        var warning = Assert.Single(warnings);
        Assert.Equal(AiUsageLimitLevels.Warning, warning.Level);
        Assert.Contains("tokens", warning.Metrics);
        Assert.Contains("cost-CNY", warning.Metrics);
        Assert.False(warning.Read);

        Assert.True(await service.MarkReminderReadAsync(warning.Id, default));
        Assert.True(await service.MarkReminderReadAsync(warning.Id, default));
        var firstView = Assert.Single(await service.ListRemindersAsync(connection.Id, false, 50, default));
        Assert.True(firstView.Read);
        var secondView = Assert.Single(await Service(db, secondAdmin)
            .ListRemindersAsync(connection.Id, false, 50, default));
        Assert.False(secondView.Read);

        var direct = await service.CreateConnectionAsync("直接到线连接", default);
        await service.SaveRuleAsync(direct.Id, new(direct.Limit.Revision, true,
            AiUsageLimitPeriods.Month, 80, 100, null, null, "Asia/Shanghai"), default);
        db.AiRequestLogs.Add(Log(direct.Id, at, 150, null));
        await db.SaveChangesAsync();
        await service.EvaluateAsync(direct.Id, default);
        var directEvents = await service.ListRemindersAsync(direct.Id, false, 50, default);
        var reached = Assert.Single(directEvents);
        Assert.Equal(AiUsageLimitLevels.Reached, reached.Level);
    }

    [Fact]
    public async Task DisabledRule_StopsNewEvents_ButKeepsHistoryReadable()
    {
        await using var db = Db();
        var service = Service(db, new AdminUser());
        var connection = await service.CreateConnectionAsync("关闭提醒连接", default);
        var enabled = await service.SaveRuleAsync(connection.Id, new(connection.Limit.Revision, true,
            AiUsageLimitPeriods.Month, 80, 100, null, null, "Asia/Shanghai"), default);
        var at = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        db.AiRequestLogs.Add(Log(connection.Id, at, 150, null));
        await db.SaveChangesAsync();
        await service.EvaluateAsync(connection.Id, default);
        Assert.Single(await service.ListRemindersAsync(connection.Id, false, 50, default));

        await service.SaveRuleAsync(connection.Id, new(enabled.Revision, false,
            AiUsageLimitPeriods.Month, 80, 100, null, null, "Asia/Shanghai"), default);
        db.AiRequestLogs.Add(Log(connection.Id, at.AddMinutes(1), 50, null));
        await db.SaveChangesAsync();
        await service.EvaluateAsync(connection.Id, default);
        Assert.Single(await service.ListRemindersAsync(connection.Id, false, 50, default));
    }

    [SkippableFact]
    public async Task ConcurrentReminderEvaluation_ConvergesToOneUniqueEvent()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_j09_event");
        await database.MigrateAsync();
        var connectionId = Guid.NewGuid();
        await using (var seed = Open(database.ConnectionString))
        {
            seed.AiProviderConnections.Add(new AiProviderConnectionEntity
            {
                Id = connectionId, Name = "并发连接", NormalizedName = "并发连接"
            });
            seed.AiUsageLimitRules.Add(new AiUsageLimitRuleEntity
            {
                ConnectionId = connectionId, Enabled = true, Period = AiUsageLimitPeriods.Month,
                WarningPercent = 80, TokenLimit = 100, TimeZoneId = "Asia/Shanghai"
            });
            seed.AiRequestLogs.Add(Log(connectionId, new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), 85, null));
            await seed.SaveChangesAsync();
        }

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = Open(database.ConnectionString);
            var service = Service(db, new AdminUser(), new FixedTimeProvider(Now));
            await service.EvaluateAsync(connectionId, default);
        });
        await Task.WhenAll(tasks);

        await using var check = Open(database.ConnectionString);
        var rows = await check.AiUsageReminderEvents.AsNoTracking()
            .Where(x => x.ConnectionId == connectionId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(AiUsageLimitLevels.Warning, row.Level);
        Assert.Contains("tokens", row.MetricsJson);
    }

    [SkippableFact]
    public async Task ConcurrentLimitGuard_AllowsOnlyOneInFlightCall_AndRejectsAtRecordedLimit()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_j09_guard");
        await database.MigrateAsync();
        var connectionId = Guid.NewGuid();
        await using (var seed = Open(database.ConnectionString))
        {
            seed.AiProviderConnections.Add(new AiProviderConnectionEntity
            {
                Id = connectionId, Name = "并发限额连接", NormalizedName = "并发限额连接"
            });
            seed.AiUsageLimitRules.Add(new AiUsageLimitRuleEntity
            {
                ConnectionId = connectionId, Enabled = true, Period = AiUsageLimitPeriods.Month,
                WarningPercent = 80, TokenLimit = 1_000, TimeZoneId = "Asia/Shanghai"
            });
            await seed.SaveChangesAsync();
        }

        async Task<AiUsageLimitDecision> Try(string key)
        {
            await using var db = Open(database.ConnectionString);
            return await Service(db, new AdminUser(), new FixedTimeProvider(Now))
                .TryBeginAsync(connectionId, key, "test", TimeSpan.FromMinutes(1), default);
        }

        var decisions = await Task.WhenAll(Try("request-a"), Try("request-b"));
        var winner = Assert.Single(decisions, x => x.Allowed);
        var loser = Assert.Single(decisions, x => !x.Allowed);
        Assert.Equal("usage_limit_busy", loser.ErrorCode);
        Assert.NotNull(winner.Lease);

        await using (var release = Open(database.ConnectionString))
        {
            await Service(release, new AdminUser(), new FixedTimeProvider(Now))
                .EndAsync(winner.Lease, default);
            release.AiRequestLogs.Add(Log(connectionId,
                new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), 1_000, null));
            await release.SaveChangesAsync();
        }

        var reached = await Try("request-c");
        Assert.False(reached.Allowed);
        Assert.Equal("usage_limit_reached", reached.ErrorCode);
    }

    [SkippableFact]
    public async Task IsolatedLeaseContext_PreservesCallerPendingChanges()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("test_j09_isolated");
        await database.MigrateAsync();
        var connectionId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;

        await using var db = new PimDbContext(options);
        db.AiProviderConnections.Add(new AiProviderConnectionEntity
        {
            Id = connectionId, Name = "隔离连接", NormalizedName = "隔离连接"
        });
        db.AiUsageLimitRules.Add(new AiUsageLimitRuleEntity
        {
            ConnectionId = connectionId, Enabled = true, Period = AiUsageLimitPeriods.Month,
            WarningPercent = 80, TokenLimit = 1_000, TimeZoneId = "Asia/Shanghai"
        });
        await db.SaveChangesAsync();

        var pending = Log(connectionId, Now, 10, null);
        db.AiRequestLogs.Add(pending);
        var service = Service(db, new AdminUser(), new FixedTimeProvider(Now), options);

        var decision = await service.TryBeginAsync(connectionId, "pending-save", "test",
            TimeSpan.FromMinutes(1), default);
        Assert.True(decision.Allowed);

        await db.SaveChangesAsync();

        await using var check = new PimDbContext(options);
        Assert.True(await check.AiRequestLogs.AsNoTracking().AnyAsync(x => x.Id == pending.Id));
        await service.EndAsync(decision.Lease, default);
    }

    private static PimDbContext Db() => new(new DbContextOptionsBuilder<PimDbContext>()
        .UseInMemoryDatabase("j09-" + Guid.NewGuid().ToString("N")).Options);

    private static PimDbContext Open(string connectionString) => new(
        new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(connectionString).Options);

    private static AiUsageLimitService Service(PimDbContext db, ICurrentUserService user,
        TimeProvider? clock = null, DbContextOptions<PimDbContext>? isolatedOptions = null) =>
        new(db, user, clock ?? new FixedTimeProvider(Now), isolatedOptions);

    private static AiRequestLogEntity Log(Guid connectionId, DateTimeOffset startedAt, int? tokens,
        decimal? cost, string? currency = null, DateTimeOffset? initiatedAt = default) => new()
        {
            Id = Guid.NewGuid(),
            Module = "system",
            Purpose = "ai.text-call",
            SourceObjectType = "ai-model",
            SourceObjectId = "synthetic",
            Provider = "openai-compatible",
            Model = "fixture",
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "succeeded",
            StartedAt = startedAt,
            InitiatedAt = initiatedAt,
            FinishedAt = startedAt.AddSeconds(1),
            TotalTokens = tokens,
            EstimatedCost = cost,
            Currency = cost is null ? null : currency,
            ProviderConnectionId = connectionId
        };

    private sealed class AdminUser : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Role => "admin";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
