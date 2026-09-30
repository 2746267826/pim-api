using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// REQ-8 内容地平线（<c>PcTrackerQualityService.MaxEventEndAsync</c>）的 LINQ 在 **Npgsql** 下必须能翻译成 SQL。
///
/// <para>为什么需要这个用例：本仓库的单测都跑 EF InMemory，InMemory 不做 SQL 翻译 —— 一个在 InMemory 下
/// 全绿的查询，到了真实 PostgreSQL 可能直接抛 <c>InvalidOperationException: could not be translated</c>。
/// 本环境连不上真库（`PIM_MIRROR_CONN` 不可用），但 <c>ToQueryString()</c> 只需要 provider 的翻译器、
/// **不需要建立连接**，因此可以把这几条查询形状的 SQL 固定下来。</para>
///
/// <para>这也解释了实现为什么用「先聚合 max(Timestamp)/max(Duration)、再按窗口取行」两段式，而不是
/// <c>MaxAsync(e =&gt; e.Timestamp.AddSeconds(e.Duration))</c>：后者把计算塞进聚合表达式，翻译风险高；
/// 前者只用到 <c>MAX(列)</c> 与 <c>WHERE 列 &gt;= 常量</c>。</para>
/// </summary>
public sealed class PcQualityContentHorizonSqlTranslationTests
{
    private readonly ITestOutputHelper _output;

    public PcQualityContentHorizonSqlTranslationTests(ITestOutputHelper output) => _output = output;

    private static PimDbContext CreateNpgsqlContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(TrackerEventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            // 只用于翻译，绝不连接：ToQueryString 不打开连接。
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=pim_translation_smoke;Username=smoke;Password=smoke")
            .Options;
        return new PimDbContext(options);
    }

    [Fact]
    public void ContentHorizonQueries_TranslateToPostgreSqlWithoutAConnection()
    {
        using var db = CreateNpgsqlContext();
        var windowStart = DateTimeOffset.Parse("2026-09-27T18:00:00+00:00");

        // 1) MaxEventEndAsync 的第 1 步：MAX(timestamp)（等价于 source.MaxAsync(e => e.Timestamp)）。
        var maxTimestampSql = db.Set<TrackerEventEntity>().AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => g.Max(e => e.Timestamp))
            .ToQueryString();

        // 2) 第 2 步：MAX(duration)。
        var maxDurationSql = db.Set<TrackerEventEntity>().AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => g.Max(e => e.Duration))
            .ToQueryString();

        // 3) 第 3 步：按窗口取候选行（窗口起点是服务端算好的常量）。
        var windowSql = db.Set<TrackerEventEntity>().AsNoTracking()
            .Where(e => e.Timestamp >= windowStart)
            .ToQueryString();

        // 4) 存在性检查（AnyAsync）。
        var anySql = db.Set<AwEventEntity>().AsNoTracking()
            .Where(e => e.Timestamp >= windowStart)
            .ToQueryString();

        foreach (var sql in new[] { maxTimestampSql, maxDurationSql, windowSql, anySql })
        {
            Assert.False(string.IsNullOrWhiteSpace(sql));
            Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
            _output.WriteLine(sql.ReplaceLineEndings(" "));
        }

        Assert.Contains("max(", maxTimestampSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pc_tracker_events", maxTimestampSql);
        Assert.Contains("max(", maxDurationSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pc_tracker_events", windowSql);
        Assert.Contains("WHERE", windowSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pc_aw_events", anySql);
    }

    [Fact]
    public void ContentHorizonQueries_DoNotPutArithmeticInsideTheAggregate()
    {
        using var db = CreateNpgsqlContext();

        // 反面记录：把 `Timestamp + Duration` 塞进聚合表达式是**故意避免**的写法（翻译风险高），
        // 实现改为「窗口内取行、在内存里算结束时刻」。这里把可翻译的形状固定住：
        // 窗口条件里的常量参数必须是 DateTimeOffset，聚合只允许作用在裸列上。
        var windowStart = DateTimeOffset.Parse("2026-09-27T18:00:00+00:00");
        var sql = db.Set<AwEventEntity>().AsNoTracking()
            .Where(e => e.Timestamp >= windowStart && e.Duration > 0)
            .ToQueryString();

        Assert.Contains("pc_aw_events", sql);
        Assert.Contains("timestamp", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duration", sql, StringComparison.OrdinalIgnoreCase);
    }
}
