using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// 本仓默认单测使用 InMemory/SQLite，不能证明 LINQ 在真实 PostgreSQL 上可翻译。
/// ToQueryString 只需要 provider 翻译器，不需要建立连接；这里固定 P05 分页/分段查询的形状。
/// </summary>
public sealed class MobileLocationQueryTranslationTests
{
    private static PimDbContext CreateNpgsqlContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(MobileLocationPointEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            // 仅用于翻译，不打开连接。
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=pim_translation_smoke;Username=smoke;Password=smoke")
            .Options;
        return new PimDbContext(options);
    }

    [Fact]
    public void CursorAndSegmentBoundaryQueries_TranslateToPostgreSqlWithoutAConnection()
    {
        using var db = CreateNpgsqlContext();
        var cursor = new MobileLocationCursorValue(
            MobileLocationCursorKind.Encoded,
            DateTimeOffset.Parse("2026-11-25T12:00:00Z"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var boundary = new MobileLocationSegmentBoundary(
            "pixel-8",
            DateTimeOffset.Parse("2026-11-25T11:00:00Z"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            DateTimeOffset.Parse("2026-11-25T13:00:00Z"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"));

        var cursorSql = MobileLocationCursor.Apply(
                db.Set<MobileLocationPointEntity>().AsNoTracking(),
                cursor)
            .OrderBy(point => point.RecordedAtUtc)
            .ThenBy(point => point.Id)
            .Take(51)
            .ToQueryString();
        var segmentSql = MobileLocationSegmentBoundaries.Apply(
                db.Set<MobileLocationPointEntity>().AsNoTracking(),
                boundary,
                50,
                includeRejected: false)
            .ToQueryString();

        Assert.Contains("mobile_location_points", cursorSql);
        Assert.Contains("LIMIT", cursorSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mobile_location_points", segmentSql);
        Assert.Contains("recorded_at_utc", segmentSql, StringComparison.OrdinalIgnoreCase);
    }
}
