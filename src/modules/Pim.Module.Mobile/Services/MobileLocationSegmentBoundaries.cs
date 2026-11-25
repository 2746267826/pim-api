using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

internal sealed record MobileLocationSegmentBoundary(
    string DeviceId,
    DateTimeOffset StartRecordedAtUtc,
    Guid StartPointId,
    DateTimeOffset EndRecordedAtUtc,
    Guid EndPointId);

/// <summary>
/// 从稳定分段 ID（segment_{first:N}_{last:N}）还原查询边界。
/// 轨迹 ID 本来就由首尾点组成，因此分页不需要先重算整段轨迹。
/// </summary>
internal static class MobileLocationSegmentBoundaries
{
    public static async Task<MobileLocationSegmentBoundary?> ResolveAsync(
        PimDbContext db,
        Guid userId,
        string segmentId,
        MobileLocationQueryContext context,
        CancellationToken ct)
    {
        if (!TryParseSegmentId(segmentId, out var firstPointId, out var lastPointId))
            return null;

        var points = await db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(point => point.UserId == userId
                && (point.Id == firstPointId || point.Id == lastPointId))
            .Select(point => new
            {
                point.Id,
                point.DeviceId,
                point.RecordedAtUtc
            })
            .ToListAsync(ct);
        var first = points.SingleOrDefault(point => point.Id == firstPointId);
        var last = points.SingleOrDefault(point => point.Id == lastPointId);
        if (first is null || last is null
            || !string.Equals(first.DeviceId, last.DeviceId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(context.DeviceId)
                && !string.Equals(first.DeviceId, context.DeviceId, StringComparison.Ordinal))
            || first.RecordedAtUtc < context.Range.RangeStartUtc
            || last.RecordedAtUtc >= context.Range.RangeEndUtc
            || first.RecordedAtUtc > last.RecordedAtUtc)
        {
            return null;
        }

        return new MobileLocationSegmentBoundary(
            first.DeviceId,
            first.RecordedAtUtc,
            first.Id,
            last.RecordedAtUtc,
            last.Id);
    }

    public static IQueryable<MobileLocationPointEntity> Apply(
        IQueryable<MobileLocationPointEntity> query,
        MobileLocationSegmentBoundary boundary,
        double maxAccuracyMeters,
        bool includeRejected)
    {
        query = query
            .Where(point => point.DeviceId == boundary.DeviceId)
            .Where(point => point.RecordedAtUtc > boundary.StartRecordedAtUtc
                || (point.RecordedAtUtc == boundary.StartRecordedAtUtc
                    && point.Id.CompareTo(boundary.StartPointId) >= 0))
            .Where(point => point.RecordedAtUtc < boundary.EndRecordedAtUtc
                || (point.RecordedAtUtc == boundary.EndRecordedAtUtc
                    && point.Id.CompareTo(boundary.EndPointId) <= 0));
        if (!includeRejected)
        {
            // 与 GetTracksAsync 的可用点口径一致；显式 includeRejected 时，
            // 分段 Path 本身包含低精度/拒绝点，这里不能再次过滤掉。
            query = query
                .Where(point => point.Quality.ToLower() != "rejected")
                .Where(point => point.HorizontalAccuracyMeters < (decimal)maxAccuracyMeters);
        }

        return query;
    }

    private static bool TryParseSegmentId(string segmentId, out Guid firstPointId, out Guid lastPointId)
    {
        firstPointId = Guid.Empty;
        lastPointId = Guid.Empty;
        if (!segmentId.StartsWith("segment_", StringComparison.Ordinal))
            return false;

        var parts = segmentId.Split('_');
        return parts.Length == 3
            && Guid.TryParseExact(parts[1], "N", out firstPointId)
            && Guid.TryParseExact(parts[2], "N", out lastPointId);
    }
}
