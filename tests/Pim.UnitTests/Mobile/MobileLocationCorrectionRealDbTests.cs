using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Pim.Core.Caching;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// P05 分段合并的 23505 并发边界。
///
/// <para>
/// 预置一行再走顺序重试只能覆盖“读到了已有行”的分支，无法证明并发插入撞唯一索引后
/// <c>MergeAdjacentSegmentsAsync</c> 的 catch 真的生效。这里在真 PostgreSQL 上使用独立连接：
/// 服务通过预检查、准备插入时，测试上下文先用另一条连接提交同边界赢家，再让本次保存以
/// 23505 失败。赢家行带独立 ID/actor，因此“只剩赢家一行”也同时证明输家的合并行与审计已回滚。
/// </para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class MobileLocationCorrectionRealDbTests
{
    private const string DeviceId = "pgsql-location-merge-conflict";
    private static readonly Guid UserId = Guid.Parse("62050000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-26T04:00:00Z");
    private static readonly DateTimeOffset CompetitorCreatedAt = Now.AddMinutes(-1);

    private static readonly string[] RequiredTables =
    [
        "mobile_location_points",
        "mobile_location_segment_merges",
        "audit_logs"
    ];

    [SkippableFact]
    public async Task MergeAdjacentSegmentsAsync_ConcurrentSameKindWinner_EntersUniqueViolationCatchWithoutDuplicateWrite()
    {
        await using var scenario = await MergeConflictScenario.CreateAsync("move");
        var service = scenario.CreateService(scenario.Db);

        var result = await service.MergeAdjacentSegmentsAsync(
            scenario.Request,
            CancellationToken.None);

        // 预检查发生在赢家插入之前；若 catch 未生效，这里会是 DbUpdateException/500。
        Assert.Equal("move", result.Kind);
        Assert.Equal(scenario.BoundaryPointId, result.BoundaryPointId);
        Assert.Equal(scenario.Request.RangeStartUtc, result.RangeStartUtc);
        Assert.Equal(scenario.Request.RangeEndUtc, result.RangeEndUtc);
        Assert.Equal(CompetitorCreatedAt, result.RecomputedAtUtc);
        Assert.Equal(1, scenario.Db.SaveAttemptCount);
        Assert.Equal(1, scenario.Db.CompetitorInsertCount);

        scenario.Db.ChangeTracker.Clear();
        var merge = Assert.Single(await scenario.Db.Set<MobileLocationSegmentMergeEntity>()
            .AsNoTracking()
            .Where(row => row.UserId == UserId && row.DeviceId == DeviceId)
            .ToListAsync());
        Assert.Equal(scenario.Db.CompetitorId, merge.Id);
        Assert.Equal(scenario.Db.CompetitorActorUserId, merge.ActorUserId);
        Assert.Equal("move", merge.Kind);
        Assert.Equal(scenario.BoundaryPointId, merge.BeforePointId);
        Assert.Equal(scenario.BoundaryPointId, merge.AfterPointId);

        // 输家的业务行与审计必须一起回滚：不能留下第二条覆盖或孤儿审计。
        Assert.Empty(await scenario.Db.AuditLogs
            .Where(log => log.UserId == UserId && log.Action == "mobile.location.segment.merge")
            .ToListAsync());
    }

    [SkippableFact]
    public async Task MergeAdjacentSegmentsAsync_ConcurrentDifferentKindWinner_Returns6205WithoutWritingOverride()
    {
        await using var scenario = await MergeConflictScenario.CreateAsync("stay");
        var service = scenario.CreateService(scenario.Db);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.MergeAdjacentSegmentsAsync(scenario.Request, CancellationToken.None));

        Assert.Equal(6205, error.ErrorCode);
        Assert.Equal(1, scenario.Db.SaveAttemptCount);
        Assert.Equal(1, scenario.Db.CompetitorInsertCount);

        scenario.Db.ChangeTracker.Clear();
        var merge = Assert.Single(await scenario.Db.Set<MobileLocationSegmentMergeEntity>()
            .AsNoTracking()
            .Where(row => row.UserId == UserId && row.DeviceId == DeviceId)
            .ToListAsync());
        Assert.Equal(scenario.Db.CompetitorId, merge.Id);
        Assert.Equal(scenario.Db.CompetitorActorUserId, merge.ActorUserId);
        Assert.Equal("stay", merge.Kind);
        Assert.Empty(await scenario.Db.AuditLogs
            .Where(log => log.UserId == UserId && log.Action == "mobile.location.segment.merge")
            .ToListAsync());
    }

    private sealed class MergeConflictScenario : IAsyncDisposable
    {
        private readonly NpgsqlConnection _admin;
        private readonly string _schema;

        private MergeConflictScenario(
            NpgsqlConnection admin,
            string schema,
            CompetingMergeDbContext db,
            MobileLocationSegmentMergeRequest request,
            Guid boundaryPointId)
        {
            _admin = admin;
            _schema = schema;
            Db = db;
            Request = request;
            BoundaryPointId = boundaryPointId;
        }

        public CompetingMergeDbContext Db { get; }

        public MobileLocationSegmentMergeRequest Request { get; }

        public Guid BoundaryPointId { get; }

        public static async Task<MergeConflictScenario> CreateAsync(string competitorKind)
        {
            var connectionString = RealDbTestConnection.Require();
            MobileTestHelpers.RegisterMobileModule();

            var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            var schema = $"test_location_merge_conflict_{Guid.NewGuid():N}";
            try
            {
                await using (var probe = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                    "WHERE n.nspname = 'public' AND c.relname = ANY(@tables)", admin))
                {
                    probe.Parameters.AddWithValue("tables", RequiredTables);
                    var found = Convert.ToInt64(await probe.ExecuteScalarAsync() ?? 0L);
                    Skip.If(found < RequiredTables.Length,
                        "RealDb 缺少 P05 表结构（未指向已迁移的库），跳过测试。");
                }

                await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
                    await create.ExecuteNonQueryAsync();

                foreach (var table in RequiredTables)
                {
                    await using var copy = new NpgsqlCommand(
                        $"CREATE TABLE \"{schema}\".\"{table}\" " +
                        $"(LIKE public.\"{table}\" INCLUDING ALL)",
                        admin);
                    await copy.ExecuteNonQueryAsync();
                }

                var searchPathConnection = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    SearchPath = schema
                }.ConnectionString;
                var options = new DbContextOptionsBuilder<PimDbContext>()
                    .UseNpgsql(searchPathConnection, npgsql => npgsql.EnableRetryOnFailure(3))
                    .Options;
                var db = new CompetingMergeDbContext(options, searchPathConnection, schema);

                var points = new[]
                {
                    Point(UserId, DeviceId, Now, 31.230416m, 121.473701m),
                    Point(UserId, DeviceId, Now.AddMinutes(12), 31.230800m, 121.473701m),
                    Point(UserId, DeviceId, Now.AddMinutes(25), 31.260000m, 121.510000m),
                    Point(UserId, DeviceId, Now.AddMinutes(40), 31.260500m, 121.510500m)
                };
                db.Set<MobileLocationPointEntity>().AddRange(points);
                await db.SaveChangesAsync();

                var query = new MobileLocationQueryRequest(
                    RangeStartUtc: Now.AddHours(-1),
                    RangeEndUtc: Now.AddHours(1),
                    Timezone: "Asia/Shanghai",
                    DeviceId: DeviceId);
                var user = MobileTestHelpers.CurrentUser(UserId);
                var time = MobileTestHelpers.Time(Now);
                var queryService = new MobileLocationQueryService(time);
                var aggregation = new MobileLocationAggregationService(db, user, queryService, time);
                var track = Assert.Single(
                    await aggregation.GetTracksAsync(query, CancellationToken.None));
                Assert.Equal(3, track.SegmentCount);
                var boundaryPointId = Guid.Parse(track.Segments[0].Path[^1].Id);
                Assert.Equal(boundaryPointId, Guid.Parse(track.Segments[1].Path[0].Id));

                var request = new MobileLocationSegmentMergeRequest(
                    [track.Segments[0].Id, track.Segments[1].Id],
                    Kind: "move",
                    RangeStartUtc: query.RangeStartUtc!.Value,
                    RangeEndUtc: query.RangeEndUtc!.Value,
                    Timezone: query.Timezone,
                    DeviceId: DeviceId);
                db.ArmCompetingInsert(boundaryPointId, competitorKind);

                return new MergeConflictScenario(
                    admin,
                    schema,
                    db,
                    request,
                    boundaryPointId);
            }
            catch
            {
                await using (var drop = new NpgsqlCommand(
                    $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin))
                {
                    await drop.ExecuteNonQueryAsync();
                }

                await admin.DisposeAsync();
                throw;
            }
        }

        public MobileLocationCorrectionService CreateService(PimDbContext db)
        {
            var user = MobileTestHelpers.CurrentUser(UserId);
            var time = MobileTestHelpers.Time(Now);
            var query = new MobileLocationQueryService(time);
            var aggregation = new MobileLocationAggregationService(db, user, query, time);
            return new MobileLocationCorrectionService(
                db,
                user,
                query,
                aggregation,
                new NoopRecalculationService(),
                new AuditLogService(db),
                new MemoryAggregateResultCache(new MemoryCache(new MemoryCacheOptions()), time),
                time);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await using (var drop = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", _admin))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await _admin.DisposeAsync();
        }
    }

    /// <summary>
    /// 在首次业务保存前用独立连接提交同边界赢家。独立连接立即提交，因此随后 EF 的插入
    /// 必定命中唯一索引；不依赖线程调度或 sleep，重复运行语义一致。
    /// </summary>
    private sealed class CompetingMergeDbContext(
        DbContextOptions<PimDbContext> options,
        string connectionString,
        string schema) : PimDbContext(options)
    {
        private int _armed;
        private int _saveAttempts;
        private int _competitorInsertCount;
        private Guid _boundaryPointId;
        private string _competitorKind = "move";

        public Guid CompetitorId { get; } = Guid.NewGuid();

        public Guid CompetitorActorUserId { get; } = Guid.Parse("62050000-0000-4000-8000-000000000002");

        public int SaveAttemptCount => Volatile.Read(ref _saveAttempts);

        public int CompetitorInsertCount => Volatile.Read(ref _competitorInsertCount);

        public void ArmCompetingInsert(Guid boundaryPointId, string competitorKind)
        {
            _boundaryPointId = boundaryPointId;
            _competitorKind = competitorKind;
            Interlocked.Exchange(ref _armed, 1);
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveAttempts);
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                await InsertCompetitorAsync(cancellationToken);
                Interlocked.Increment(ref _competitorInsertCount);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }

        private async Task InsertCompetitorAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var insert = new NpgsqlCommand(
                $"""
                INSERT INTO "{schema}"."mobile_location_segment_merges"
                    (id, user_id, device_id, before_point_id, after_point_id, kind, actor_user_id, created_at)
                VALUES
                    (@id, @userId, @deviceId, @boundaryPointId, @boundaryPointId, @kind, @actorUserId, @createdAt)
                """,
                connection);
            insert.Parameters.AddWithValue("id", CompetitorId);
            insert.Parameters.AddWithValue("userId", UserId);
            insert.Parameters.AddWithValue("deviceId", DeviceId);
            insert.Parameters.AddWithValue("boundaryPointId", _boundaryPointId);
            insert.Parameters.AddWithValue("kind", _competitorKind);
            insert.Parameters.AddWithValue("actorUserId", CompetitorActorUserId);
            insert.Parameters.AddWithValue("createdAt", CompetitorCreatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class NoopRecalculationService : IMobileLocationRecalculationService
    {
        public Task<DateTimeOffset> RecalculateAsync(
            IReadOnlyCollection<MobileLocationRecalculationScope> scopes,
            CancellationToken ct = default)
            => Task.FromResult(Now);
    }

    private static MobileLocationPointEntity Point(
        Guid userId,
        string deviceId,
        DateTimeOffset recordedAt,
        decimal latitude,
        decimal longitude)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeviceId = deviceId,
            RecordedAtUtc = recordedAt,
            Latitude = latitude,
            Longitude = longitude,
            HorizontalAccuracyMeters = 12,
            Provider = "gps",
            Source = "auto",
            Quality = "usable",
            RawJson = "{}",
            CreatedAt = recordedAt
        };
}
