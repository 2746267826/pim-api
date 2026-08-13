using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileLocationAggregationService
{
    private static readonly TimeSpan TrackGapThreshold = TimeSpan.FromHours(2);

    private const double NoiseDisplacementFloorMeters = 30;
    private const double StaySpeedThresholdMetersPerSecond = 0.3;
    private const double MoveSpeedThresholdMetersPerSecond = 1.0;
    private const double JumpSpeedThresholdMetersPerSecond = 30;
    private const double EarthRadiusMeters = 6371000;

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileLocationQueryService _queryService;
    private readonly TimeProvider _timeProvider;

    public MobileLocationAggregationService(
        PimDbContext db,
        ICurrentUserService currentUser,
        MobileLocationQueryService queryService,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _queryService = queryService;
        _timeProvider = timeProvider;
    }

    public async Task<MobileLocationAnalyticsOverviewResponse> GetOverviewAsync(
        MobileLocationQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var rawPoints = await LoadRawPointsAsync(context, ct);
        var usablePoints = rawPoints.Where(point => IsUsable(point, context)).ToList();
        var gaps = CountLargeGaps(usablePoints);
        var qualityFlags = QualityFlags(rawPoints, usablePoints, context, gaps);
        var rejectedCount = rawPoints.Count - usablePoints.Count;
        var activeSpanSeconds = usablePoints.Count <= 1
            ? 0
            : Convert.ToInt64((usablePoints[^1].RecordedAtUtc - usablePoints[0].RecordedAtUtc).TotalSeconds);

        return new MobileLocationAnalyticsOverviewResponse(
            context.Range,
            _timeProvider.GetUtcNow(),
            rawPoints.Count,
            usablePoints.Count,
            rejectedCount,
            Math.Max(0, activeSpanSeconds),
            Math.Round(TotalDistanceMetersByDevice(usablePoints), 1),
            CountStaySegments(usablePoints),
            LongestStaySeconds(usablePoints),
            Math.Round(AverageAccuracyMeters(usablePoints), 1),
            rejectedCount + gaps,
            qualityFlags);
    }

    public async Task<IReadOnlyList<MobileLocationTrackDto>> GetTracksAsync(
        MobileLocationQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var points = await LoadTrackPointsAsync(context, ct);
        return BuildTracks(points, context);
    }

    public async Task<MobileLocationSegmentDto?> GetSegmentAsync(
        string segmentId,
        MobileLocationQueryRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            return null;

        var tracks = await GetTracksAsync(request, ct);
        return tracks
            .SelectMany(track => track.Segments)
            .FirstOrDefault(segment => string.Equals(segment.Id, segmentId, StringComparison.Ordinal));
    }

    public async Task<MobileLocationSegmentPointPageDto> GetSegmentPointsAsync(
        string segmentId,
        MobileLocationQueryRequest request,
        CancellationToken ct = default)
    {
        var context = _queryService.Normalize(request);
        var segment = await GetSegmentAsync(segmentId, request, ct);
        if (segment is null)
            return new MobileLocationSegmentPointPageDto([], null, false);

        var segmentPointIds = segment.Path
            .Select(point => Guid.Parse(point.Id))
            .ToHashSet();
        var points = await LoadRawPointsAsync(context, ct);
        var segmentPoints = points
            .Where(point => segmentPointIds.Contains(point.Id))
            .OrderBy(point => point.RecordedAtUtc)
            .ThenBy(point => point.Id)
            .ToList();

        var startIndex = 0;
        if (Guid.TryParse(context.Cursor, out var cursorId))
        {
            var cursorIndex = segmentPoints.FindIndex(point => point.Id == cursorId);
            if (cursorIndex >= 0)
                startIndex = cursorIndex + 1;
        }

        var page = segmentPoints
            .Skip(startIndex)
            .Take(context.PageSize)
            .ToList();
        var hasMore = startIndex + page.Count < segmentPoints.Count;
        var nextCursor = hasMore && page.Count > 0
            ? page[^1].Id.ToString()
            : null;

        return new MobileLocationSegmentPointPageDto(page.Select(MapPoint).ToList(), nextCursor, hasMore);
    }

    private async Task<List<MobileLocationPointEntity>> LoadRawPointsAsync(
        MobileLocationQueryContext context,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var query = _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(point => point.UserId == userId
                && point.RecordedAtUtc >= context.Range.RangeStartUtc
                && point.RecordedAtUtc < context.Range.RangeEndUtc);

        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            query = query.Where(point => point.DeviceId == context.DeviceId);

        return await query
            .OrderBy(point => point.RecordedAtUtc)
            .ThenBy(point => point.Id)
            .ToListAsync(ct);
    }

    private async Task<List<MobileLocationPointEntity>> LoadTrackPointsAsync(
        MobileLocationQueryContext context,
        CancellationToken ct)
    {
        var points = await LoadRawPointsAsync(context, ct);
        return points
            .Where(point => context.IncludeRejected || IsUsable(point, context))
            .ToList();
    }

    private static IReadOnlyList<MobileLocationTrackDto> BuildTracks(
        List<MobileLocationPointEntity> points,
        MobileLocationQueryContext context)
    {
        if (points.Count == 0)
            return [];

        var tracks = new List<MobileLocationTrackDto>();
        foreach (var devicePoints in points.GroupBy(point => point.DeviceId).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var current = new List<MobileLocationPointEntity>();
            foreach (var point in devicePoints.OrderBy(point => point.RecordedAtUtc).ThenBy(point => point.Id))
            {
                if (current.Count > 0 && point.RecordedAtUtc - current[^1].RecordedAtUtc > TrackGapThreshold)
                {
                    tracks.Add(BuildTrack(current, context));
                    current = [];
                }

                current.Add(point);
            }

            if (current.Count > 0)
                tracks.Add(BuildTrack(current, context));
        }

        return tracks;
    }

    private static MobileLocationTrackDto BuildTrack(
        List<MobileLocationPointEntity> points,
        MobileLocationQueryContext context)
    {
        var first = points[0];
        var last = points[^1];
        var trackId = StableId("track", first.Id, last.Id);
        var segments = BuildSegments(points, context, trackId);
        var bounds = Bounds(points);
        var flags = segments.SelectMany(segment => segment.QualityFlags).Distinct(StringComparer.Ordinal).ToList();

        return new MobileLocationTrackDto(
            trackId,
            first.DeviceId,
            first.RecordedAtUtc,
            last.RecordedAtUtc,
            Math.Round(segments.Sum(segment => segment.DistanceMeters), 1),
            DurationSeconds(first.RecordedAtUtc, last.RecordedAtUtc),
            points.Count,
            segments.Count,
            bounds,
            flags,
            segments);
    }

    private static IReadOnlyList<MobileLocationSegmentDto> BuildSegments(
        List<MobileLocationPointEntity> points,
        MobileLocationQueryContext context,
        string trackId)
    {
        if (points.Count <= 1)
            return [BuildSegment(points, "stay", new HashSet<Guid>(), context, trackId)];

        var jumpPointIds = MarkJumpPoints(points);
        var segments = new List<MobileLocationSegmentDto>();
        var current = new List<MobileLocationPointEntity> { points[0] };
        var currentEvidence = PairEvidence.None;
        for (var index = 1; index < points.Count; index++)
        {
            var classification = ClassifyPair(points[index - 1], points[index], currentEvidence);
            if (classification.Evidence != PairEvidence.None)
            {
                if (currentEvidence == PairEvidence.None)
                {
                    currentEvidence = classification.Evidence;
                }
                else if (classification.Evidence != currentEvidence)
                {
                    segments.Add(BuildSegment(current, KindOf(currentEvidence), jumpPointIds, context, trackId));
                    current = [points[index - 1], points[index]];
                    currentEvidence = classification.Evidence;
                }
            }

            current.Add(points[index]);
        }

        segments.Add(BuildSegment(current, KindOf(currentEvidence), jumpPointIds, context, trackId));
        return segments;
    }

    private static MobileLocationSegmentDto BuildSegment(
        List<MobileLocationPointEntity> points,
        string kind,
        IReadOnlySet<Guid> jumpPointIds,
        MobileLocationQueryContext context,
        string trackId)
    {
        var first = points[0];
        var last = points[^1];
        var distanceMeters = kind == "move" ? MoveDistanceMeters(points, jumpPointIds) : 0;
        var durationSeconds = DurationSeconds(first.RecordedAtUtc, last.RecordedAtUtc);
        var qualityFlags = SegmentQualityFlags(points, context, jumpPointIds);
        var quality = qualityFlags.Count == 0 ? "usable" : "review";

        return new MobileLocationSegmentDto(
            StableId("segment", first.Id, last.Id),
            trackId,
            first.DeviceId,
            kind,
            first.RecordedAtUtc,
            last.RecordedAtUtc,
            LocalLabel(first.RecordedAtUtc, context.Range.Timezone),
            LocalLabel(last.RecordedAtUtc, context.Range.Timezone),
            durationSeconds,
            Math.Round(distanceMeters, 1),
            points.Count,
            durationSeconds <= 0 ? 0 : Math.Round(distanceMeters / durationSeconds, 3),
            Math.Round(AverageAccuracyMeters(points), 1),
            Math.Round(points.Count == 0 ? 0 : points.Max(AccuracyMeters), 1),
            quality,
            qualityFlags,
            Bounds(points),
            points.Select(point => MapPathPoint(point, jumpPointIds.Contains(point.Id))).ToList());
    }

    private static IReadOnlySet<Guid> MarkJumpPoints(IReadOnlyList<MobileLocationPointEntity> points)
    {
        var jumpPairCount = new int[points.Count];
        for (var index = 0; index < points.Count - 1; index++)
        {
            if (IsJumpPair(points[index], points[index + 1]))
            {
                jumpPairCount[index]++;
                jumpPairCount[index + 1]++;
            }
        }

        var jumpIds = new HashSet<Guid>();
        for (var index = 0; index < points.Count; index++)
        {
            if (jumpPairCount[index] == 0)
                continue;

            if (jumpPairCount[index] >= 2)
            {
                jumpIds.Add(points[index].Id);
                continue;
            }

            var partnerIndex = index > 0 && IsJumpPair(points[index - 1], points[index])
                ? index - 1
                : index + 1;
            if (jumpPairCount[partnerIndex] == 1)
                jumpIds.Add(points[index].Id);
        }

        return jumpIds;
    }

    private static bool IsJumpPair(MobileLocationPointEntity from, MobileLocationPointEntity to)
    {
        var elapsedSeconds = DurationSeconds(from.RecordedAtUtc, to.RecordedAtUtc);
        if (elapsedSeconds <= 0)
            return false;

        var distanceMeters = DistanceMeters(from, to);
        return distanceMeters > 0
            && distanceMeters / elapsedSeconds > JumpSpeedThresholdMetersPerSecond;
    }

    private static PairClassification ClassifyPair(
        MobileLocationPointEntity from,
        MobileLocationPointEntity to,
        PairEvidence carry)
    {
        var elapsedSeconds = DurationSeconds(from.RecordedAtUtc, to.RecordedAtUtc);
        var distanceMeters = DistanceMeters(from, to);
        if (elapsedSeconds <= 0 || distanceMeters <= 0)
            return new PairClassification(PairEvidence.None, false);

        var noiseFloorMeters = Math.Max(
            NoiseDisplacementFloorMeters,
            2 * ((AccuracyMeters(from) + AccuracyMeters(to)) / 2));
        if (distanceMeters < noiseFloorMeters)
            return new PairClassification(PairEvidence.None, false);

        var speedMetersPerSecond = distanceMeters / elapsedSeconds;
        if (speedMetersPerSecond > JumpSpeedThresholdMetersPerSecond)
            return new PairClassification(PairEvidence.None, true);

        if (speedMetersPerSecond < StaySpeedThresholdMetersPerSecond)
            return new PairClassification(PairEvidence.Stay, false);

        if (speedMetersPerSecond > MoveSpeedThresholdMetersPerSecond)
            return new PairClassification(PairEvidence.Move, false);

        return new PairClassification(MotionSignalEvidence(from, to) ?? carry, false);
    }

    private static PairEvidence? MotionSignalEvidence(MobileLocationPointEntity from, MobileLocationPointEntity to)
    {
        var fromSignal = MotionSignalFromRawJson(from.RawJson);
        var toSignal = MotionSignalFromRawJson(to.RawJson);
        foreach (var signal in new[] { fromSignal, toSignal })
        {
            if (string.Equals(signal, "Still", StringComparison.OrdinalIgnoreCase))
                return PairEvidence.Stay;
            if (IsMovingSignal(signal))
                return PairEvidence.Move;
        }

        return null;
    }

    private static bool IsMovingSignal(string signal)
        => signal.Trim().ToLowerInvariant() is "walking" or "running" or "onbicycle" or "invehicle" or "moving";

    private static string MotionSignalFromRawJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return "Unknown";

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("motionSignal", out var signal)
                || signal.ValueKind != JsonValueKind.String)
            {
                return "Unknown";
            }

            return signal.GetString() ?? "Unknown";
        }
        catch (JsonException)
        {
            return "Unknown";
        }
    }

    private static double MoveDistanceMeters(
        IReadOnlyList<MobileLocationPointEntity> points,
        IReadOnlySet<Guid> jumpPointIds)
    {
        var distance = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            if (jumpPointIds.Contains(points[index - 1].Id) || jumpPointIds.Contains(points[index].Id))
                continue;

            distance += DistanceMeters(points[index - 1], points[index]);
        }

        return distance;
    }

    private static string KindOf(PairEvidence evidence)
        => evidence == PairEvidence.Move ? "move" : "stay";

    private static IReadOnlyList<string> SegmentQualityFlags(
        IReadOnlyList<MobileLocationPointEntity> points,
        MobileLocationQueryContext context,
        IReadOnlySet<Guid> jumpPointIds)
    {
        var flags = new List<string>();
        if (points.Count <= 1)
            flags.Add("single-point");
        if (points.Any(point => !IsUsable(point, context)))
            flags.Add("low-accuracy");
        if (points.Any(point => string.Equals(point.Quality, "rejected", StringComparison.OrdinalIgnoreCase)))
            flags.Add("rejected-points");
        if (points.Any(point => jumpPointIds.Contains(point.Id)))
            flags.Add("jump-point");
        return flags.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<string> QualityFlags(
        IReadOnlyList<MobileLocationPointEntity> rawPoints,
        IReadOnlyList<MobileLocationPointEntity> usablePoints,
        MobileLocationQueryContext context,
        int largeGapCount)
    {
        var flags = new List<string>();
        if (rawPoints.Any(point => AccuracyMeters(point) > context.MaxAccuracyMeters))
            flags.Add("low-accuracy-cluster");
        if (rawPoints.Any(point => string.Equals(point.Quality, "rejected", StringComparison.OrdinalIgnoreCase)))
            flags.Add("rejected-points");
        if (largeGapCount > 0)
            flags.Add("large-gap");
        if (rawPoints.Count > 0 && usablePoints.Count == 0)
            flags.Add("no-usable-points");
        return flags;
    }

    private static int CountLargeGaps(IReadOnlyList<MobileLocationPointEntity> points)
    {
        var gaps = 0;
        foreach (var devicePoints in points.GroupBy(point => point.DeviceId))
        {
            var ordered = devicePoints.OrderBy(point => point.RecordedAtUtc).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                if (ordered[index].RecordedAtUtc - ordered[index - 1].RecordedAtUtc > TrackGapThreshold)
                    gaps++;
            }
        }

        return gaps;
    }

    private static int CountStaySegments(List<MobileLocationPointEntity> points)
        => BuildTracks(points, DefaultSegmentContext())
            .SelectMany(track => track.Segments)
            .Count(segment => segment.Kind == "stay");

    private static long LongestStaySeconds(List<MobileLocationPointEntity> points)
        => BuildTracks(points, DefaultSegmentContext())
            .SelectMany(track => track.Segments)
            .Where(segment => segment.Kind == "stay")
            .Select(segment => segment.DurationSeconds)
            .DefaultIfEmpty(0)
            .Max();

    private static double TotalDistanceMetersByDevice(List<MobileLocationPointEntity> points)
        => BuildTracks(points, DefaultSegmentContext())
            .Sum(track => track.DistanceMeters);

    private static MobileLocationQueryContext DefaultSegmentContext()
        => new(
            new MobileAnalyticsRangeDto(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, MobileAnalyticsDefaults.DefaultTimezone, "0001-01-01", "9999-12-31"),
            null,
            MobileLocationQueryService.DefaultMaxAccuracyMeters,
            false,
            null,
            MobileLocationQueryService.DefaultPageSize);

    private static MobileGeoBoundsDto? Bounds(IReadOnlyList<MobileLocationPointEntity> points)
    {
        if (points.Count == 0)
            return null;

        return new MobileGeoBoundsDto(
            points.Min(Latitude),
            points.Min(Longitude),
            points.Max(Latitude),
            points.Max(Longitude));
    }

    private static MobileLocationPathPointDto MapPathPoint(MobileLocationPointEntity point, bool isJump)
        => new(
            point.Id.ToString(),
            point.RecordedAtUtc,
            Latitude(point),
            Longitude(point),
            AccuracyMeters(point),
            point.Quality,
            isJump ? ["jump-point"] : []);

    private static MobileLocationPointDto MapPoint(MobileLocationPointEntity point)
        => new(
            point.Id,
            point.DeviceId,
            point.RecordedAtUtc,
            point.CreatedAt,
            Latitude(point),
            Longitude(point),
            AccuracyMeters(point),
            point.Provider,
            point.Source,
            DecimalToDouble(point.AltitudeMeters),
            DecimalToDouble(point.VerticalAccuracyMeters),
            DecimalToDouble(point.SpeedMetersPerSecond),
            DecimalToDouble(point.SpeedAccuracyMetersPerSecond),
            DecimalToDouble(point.BearingDegrees),
            DecimalToDouble(point.BearingAccuracyDegrees),
            string.Equals(point.Source, "auto", StringComparison.OrdinalIgnoreCase),
            point.Quality,
            point.RawJson);

    private static bool IsUsable(MobileLocationPointEntity point, MobileLocationQueryContext context)
        => !string.Equals(point.Quality, "rejected", StringComparison.OrdinalIgnoreCase)
            && AccuracyMeters(point) <= context.MaxAccuracyMeters;

    private static long DurationSeconds(DateTimeOffset start, DateTimeOffset end)
        => Math.Max(0, Convert.ToInt64((end - start).TotalSeconds));

    private static string StableId(string prefix, Guid firstPointId, Guid lastPointId)
        => $"{prefix}_{firstPointId:N}_{lastPointId:N}";

    private static double DistanceMeters(MobileLocationPointEntity from, MobileLocationPointEntity to)
    {
        var lat1 = DegreesToRadians(Latitude(from));
        var lat2 = DegreesToRadians(Latitude(to));
        var deltaLat = DegreesToRadians(Latitude(to) - Latitude(from));
        var deltaLon = DegreesToRadians(Longitude(to) - Longitude(from));
        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)
            + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180;

    private static double AverageAccuracyMeters(IReadOnlyList<MobileLocationPointEntity> points)
        => points.Count == 0 ? 0 : points.Average(AccuracyMeters);

    private static string LocalLabel(DateTimeOffset value, string timezone)
        => TimeZoneInfo.ConvertTime(value, ResolveTimezone(timezone)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveTimezone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException) when (timezone == MobileAnalyticsDefaults.DefaultTimezone)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (InvalidTimeZoneException) when (timezone == MobileAnalyticsDefaults.DefaultTimezone)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }

    private static double Latitude(MobileLocationPointEntity point)
        => Convert.ToDouble(point.Latitude);

    private static double Longitude(MobileLocationPointEntity point)
        => Convert.ToDouble(point.Longitude);

    private static double AccuracyMeters(MobileLocationPointEntity point)
        => Convert.ToDouble(point.HorizontalAccuracyMeters);

    private static double? DecimalToDouble(decimal? value)
        => value is null ? null : Convert.ToDouble(value.Value);

    private enum PairEvidence
    {
        None,
        Stay,
        Move
    }

    private readonly record struct PairClassification(PairEvidence Evidence, bool IsJump);
}
