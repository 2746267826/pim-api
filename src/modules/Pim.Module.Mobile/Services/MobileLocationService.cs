using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileLocationService
{
    public const string LocationPointEntityType = "location-point";

    private const double MaxUsableAccuracyMeters = 50;

    /// <summary>单次批量补传的定位点上限，避免一次请求把写入放大成不可控的长事务。</summary>
    private const int MaxBatchPoints = 1000;

    private const string UsableQuality = "usable";
    private const string RejectedQuality = "rejected";

    /// <summary>
    /// 坐标按表定义的小数位归一化后再参与匹配/写入：<c>latitude numeric(10,7)</c>、
    /// <c>longitude numeric(10,7)</c>。不归一化的话，同一个 double 值经 Postgres 四舍五入落库后
    /// 与内存中的参数值不再相等，幂等查询会查不到刚写入的行（#246）。
    /// </summary>
    private const int CoordinateScale = 7;

    private const int AccuracyScale = 2;

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileLocationService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<MobileLocationPointDto> SubmitAsync(MobileLocationPointRequest request, CancellationToken ct = default)
    {
        if (ValidateCoordinates(request.Latitude, request.Longitude) is { } validationError)
            throw new DomainException(6201, validationError);

        var userId = MobileUserContext.RequireUserId(_currentUser);
        var usable = request.HorizontalAccuracyMeters < MaxUsableAccuracyMeters;

        var upsert = await SavePointAsync(
            userId,
            request,
            usable ? UsableQuality : RejectedQuality,
            ct);

        if (!usable)
            throw new DomainException(6202, "Mobile location accuracy is not usable.");

        return Map(upsert.Entity);
    }

    /// <summary>
    /// 批量上传定位点（#246）：客户端积压时可一次补传，逐条返回结果。
    /// 与单点接口共用同一套幂等语义：同一 (设备, 时刻, 经纬度) 只保留一行。
    /// </summary>
    public async Task<MobileLocationPointsUploadResult> SubmitBatchAsync(
        MobileLocationPointsUploadRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        if (request.Points.Count > MaxBatchPoints)
            throw new DomainException(
                6203,
                $"A location batch must not contain more than {MaxBatchPoints} points.");

        if (request.Points.Count == 0)
            return new MobileLocationPointsUploadResult(0, 0, 0, []);

        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = 0;
        while (true)
        {
            try
            {
                return await strategy.ExecuteAsync(
                    token => SubmitBatchAttemptAsync(userId, request, token),
                    ct);
            }
            catch (DbUpdateException) when (!ct.IsCancellationRequested && attempt == 0)
            {
                // 并发写入抢在前面插入了同一个点（唯一索引兜底）：清掉待写入的重读一次，
                // 第二次读取时这些点已经存在，会作为 duplicate 返回，而不是把整批判失败。
                attempt++;
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<MobileLocationPointsUploadResult> SubmitBatchAttemptAsync(
        Guid userId,
        MobileLocationPointsUploadRequest request,
        CancellationToken ct)
    {
        var knownPoints = await LoadKnownPointsAsync(userId, request.Points, ct);
        var itemResults = new List<MobileIngestItemResult>(request.Points.Count);
        var now = _timeProvider.GetUtcNow();

        foreach (var point in request.Points)
        {
            var clientItemKey = LocationClientItemKey(point);
            if (string.IsNullOrWhiteSpace(point.DeviceId))
            {
                itemResults.Add(Item(
                    clientItemKey,
                    "rejected",
                    "invalid-device-id",
                    "Device id is required."));
                continue;
            }

            if (ValidateCoordinates(point.Latitude, point.Longitude) is { } validationError)
            {
                itemResults.Add(Item(clientItemKey, "rejected", "invalid-coordinates", validationError));
                continue;
            }

            var usable = point.HorizontalAccuracyMeters < MaxUsableAccuracyMeters;
            var quality = usable ? UsableQuality : RejectedQuality;
            var key = LocationKey.From(userId, point);

            if (knownPoints.TryGetValue(key, out var existing))
            {
                // 已存在同一自然键：先落库的坏精度点可以被后来的好精度点升级，其余按重复处理。
                if (UpsertRejectedToUsable(existing, point, usable))
                    itemResults.Add(Item(clientItemKey, "accepted", "accepted", "Accepted."));
                else if (usable)
                    itemResults.Add(Item(clientItemKey, "skipped", "duplicate", "Duplicate item."));
                else
                    itemResults.Add(Item(
                        clientItemKey,
                        "rejected",
                        "unusable-accuracy",
                        "Mobile location accuracy is not usable."));
                continue;
            }

            var entity = BuildEntity(userId, point, quality, now);
            _db.Set<MobileLocationPointEntity>().Add(entity);
            knownPoints[key] = entity;
            itemResults.Add(usable
                ? Item(clientItemKey, "accepted", "accepted", "Accepted.")
                : Item(clientItemKey, "rejected", "unusable-accuracy", "Mobile location accuracy is not usable."));
        }

        await _db.SaveChangesAsync(ct);

        return new MobileLocationPointsUploadResult(
            itemResults.Count(item => item.Outcome == "accepted"),
            itemResults.Count(item => item.Outcome == "skipped"),
            itemResults.Count(item => item.Outcome == "rejected"),
            itemResults);
    }

    /// <summary>
    /// 一次性把该批可能重复的既有行读出来（同一用户 + 同一批设备 + 时间区间），
    /// 用自然键建字典，避免逐点查库。
    /// </summary>
    private async Task<Dictionary<LocationKey, MobileLocationPointEntity>> LoadKnownPointsAsync(
        Guid userId,
        IReadOnlyList<MobileLocationPointRequest> points,
        CancellationToken ct)
    {
        var known = new Dictionary<LocationKey, MobileLocationPointEntity>();
        var candidates = points
            .Where(point => !string.IsNullOrWhiteSpace(point.DeviceId))
            .ToList();
        if (candidates.Count == 0)
            return known;

        var deviceIds = candidates
            .Select(point => point.DeviceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var minRecordedAt = candidates.Min(point => point.RecordedAtUtc);
        var maxRecordedAt = candidates.Max(point => point.RecordedAtUtc);

        var existing = await _db.Set<MobileLocationPointEntity>()
            .Where(point => point.UserId == userId
                && deviceIds.Contains(point.DeviceId)
                && point.RecordedAtUtc >= minRecordedAt
                && point.RecordedAtUtc <= maxRecordedAt)
            .ToListAsync(ct);

        foreach (var point in existing)
            known[LocationKey.From(userId, point.DeviceId, point.RecordedAtUtc, point.Latitude, point.Longitude)] = point;

        return known;
    }

    /// <summary>
    /// 返回既有实体（可能是被升级后的行）。重复上传同一自然键时不再新增行（#246）。
    /// </summary>
    private async Task<LocationUpsert> SavePointAsync(
        Guid userId,
        MobileLocationPointRequest request,
        string quality,
        CancellationToken ct)
    {
        var key = LocationKey.From(userId, request);
        var existing = await FindExistingAsync(key, ct);
        if (existing is not null)
        {
            if (UpsertRejectedToUsable(existing, request, string.Equals(quality, UsableQuality, StringComparison.Ordinal)))
            {
                await _db.SaveChangesAsync(ct);
                return new LocationUpsert(existing, "accepted");
            }

            return new LocationUpsert(existing, "duplicate");
        }

        var entity = BuildEntity(userId, request, quality, _timeProvider.GetUtcNow());
        _db.Set<MobileLocationPointEntity>().Add(entity);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            // 并发重投：唯一索引拦下了重复行，回放已经落库的那一行。
            _db.ChangeTracker.Clear();
            var winner = await FindExistingAsync(key, ct);
            if (winner is null)
                throw;

            // 并发"胜者"也可能是坏精度点：本次既然是好精度样本，仍要把它升级，
            // 否则同一点永远停在 rejected（与串行路径的语义不一致，评审 #3）。
            if (UpsertRejectedToUsable(existing: winner, request, usable: string.Equals(quality, UsableQuality, StringComparison.Ordinal)))
                await _db.SaveChangesAsync(ct);

            return new LocationUpsert(winner, "duplicate");
        }

        return new LocationUpsert(entity, "accepted");
    }

    private async Task<MobileLocationPointEntity?> FindExistingAsync(LocationKey key, CancellationToken ct)
    {
        // 不加 AsNoTracking：命中"被拒 → 可用"的升级路径时要能更新这一行。
        // 排序放在内存里做（SQLite 不支持对 DateTimeOffset 排序），唯一索引下最多只有一行。
        var candidates = await _db.Set<MobileLocationPointEntity>()
            .Where(point => point.UserId == key.UserId
                && point.DeviceId == key.DeviceId
                && point.RecordedAtUtc == key.RecordedAtUtc
                && point.Latitude == key.Latitude
                && point.Longitude == key.Longitude)
            .Take(2)
            .ToListAsync(ct);

        return candidates.OrderBy(point => point.CreatedAt).FirstOrDefault();
    }

    /// <summary>
    /// 先落库的坏精度点被后来的好精度点取代：点位本身没变，只是精度样本变好了（#246）。
    /// </summary>
    private static bool UpsertRejectedToUsable(
        MobileLocationPointEntity existing,
        MobileLocationPointRequest request,
        bool usable)
    {
        if (!usable
            || !string.Equals(existing.Quality, RejectedQuality, StringComparison.Ordinal))
            return false;

        existing.HorizontalAccuracyMeters = Accuracy(request.HorizontalAccuracyMeters);
        existing.Provider = request.Provider;
        existing.Source = request.Source;
        existing.AltitudeMeters = DecimalOrNull(request.AltitudeMeters);
        existing.VerticalAccuracyMeters = DecimalOrNull(request.VerticalAccuracyMeters);
        existing.SpeedMetersPerSecond = DecimalOrNull(request.SpeedMetersPerSecond);
        existing.SpeedAccuracyMetersPerSecond = DecimalOrNull(request.SpeedAccuracyMetersPerSecond);
        existing.BearingDegrees = DecimalOrNull(request.BearingDegrees);
        existing.BearingAccuracyDegrees = DecimalOrNull(request.BearingAccuracyDegrees);
        existing.IsMock = request.IsMock;
        existing.RawJson = JsonOrDefault(request.RawJson);
        existing.Quality = UsableQuality;
        return true;
    }

    private MobileLocationPointEntity BuildEntity(
        Guid userId,
        MobileLocationPointRequest request,
        string quality,
        DateTimeOffset now)
        => new()
        {
            UserId = userId,
            DeviceId = request.DeviceId,
            RecordedAtUtc = request.RecordedAtUtc,
            Latitude = Coordinate(request.Latitude),
            Longitude = Coordinate(request.Longitude),
            HorizontalAccuracyMeters = Accuracy(request.HorizontalAccuracyMeters),
            Provider = request.Provider,
            Source = request.Source,
            AltitudeMeters = DecimalOrNull(request.AltitudeMeters),
            VerticalAccuracyMeters = DecimalOrNull(request.VerticalAccuracyMeters),
            SpeedMetersPerSecond = DecimalOrNull(request.SpeedMetersPerSecond),
            SpeedAccuracyMetersPerSecond = DecimalOrNull(request.SpeedAccuracyMetersPerSecond),
            BearingDegrees = DecimalOrNull(request.BearingDegrees),
            BearingAccuracyDegrees = DecimalOrNull(request.BearingAccuracyDegrees),
            IsMock = request.IsMock,
            RawJson = JsonOrDefault(request.RawJson),
            Quality = quality,
            CreatedAt = now
        };

    public async Task<IReadOnlyList<MobileLocationPointDto>> GetHistoryAsync(
        string? deviceId,
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        double maxAccuracyMeters = MaxUsableAccuracyMeters,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var query = _db.Set<MobileLocationPointEntity>()
            .AsNoTracking()
            .Where(p => p.UserId == userId);

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(p => p.DeviceId == deviceId);
        if (rangeStartUtc is not null)
            query = query.Where(p => p.RecordedAtUtc >= rangeStartUtc);
        if (rangeEndUtc is not null)
            query = query.Where(p => p.RecordedAtUtc < rangeEndUtc);
        query = query.Where(p =>
            p.HorizontalAccuracyMeters < Accuracy(maxAccuracyMeters)
            && p.Quality != "rejected");

        return await query
            .OrderByDescending(p => p.RecordedAtUtc)
            .Take(500)
            .Select(p => Map(p))
            .ToListAsync(ct);
    }

    private static MobileLocationPointDto Map(MobileLocationPointEntity entity)
        => new(
            entity.Id,
            entity.DeviceId,
            entity.RecordedAtUtc,
            entity.CreatedAt,
            (double)entity.Latitude,
            (double)entity.Longitude,
            (double)entity.HorizontalAccuracyMeters,
            entity.Provider,
            entity.Source,
            DecimalToDouble(entity.AltitudeMeters),
            DecimalToDouble(entity.VerticalAccuracyMeters),
            DecimalToDouble(entity.SpeedMetersPerSecond),
            DecimalToDouble(entity.SpeedAccuracyMetersPerSecond),
            DecimalToDouble(entity.BearingDegrees),
            DecimalToDouble(entity.BearingAccuracyDegrees),
            string.Equals(entity.Source, "auto", StringComparison.OrdinalIgnoreCase),
            entity.Quality,
            entity.RawJson);

    private static string? ValidateCoordinates(double latitude, double longitude)
        => latitude is < -90 or > 90 || longitude is < -180 or > 180
            ? "Invalid mobile location coordinates."
            : null;

    private static decimal Coordinate(double value)
        => decimal.Round(Convert.ToDecimal(value), CoordinateScale, MidpointRounding.AwayFromZero);

    private static decimal Accuracy(double value)
        => decimal.Round(Convert.ToDecimal(value), AccuracyScale, MidpointRounding.AwayFromZero);

    private static decimal? DecimalOrNull(double? value)
        => value is null ? null : Convert.ToDecimal(value.Value);

    private static double? DecimalToDouble(decimal? value)
        => value is null ? null : Convert.ToDouble(value.Value);

    private static string JsonOrDefault(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;

    private static MobileIngestItemResult Item(
        string clientItemKey,
        string outcome,
        string code,
        string message)
        => new(clientItemKey, LocationPointEntityType, outcome, code, message);

    private static string LocationClientItemKey(MobileLocationPointRequest point)
        => $"location:{NaturalKeyHash(
            point.DeviceId,
            UtcKey(point.RecordedAtUtc),
            point.Latitude.ToString("R", CultureInfo.InvariantCulture),
            point.Longitude.ToString("R", CultureInfo.InvariantCulture))}";

    private static string UtcKey(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string NaturalKeyHash(params string[] parts)
    {
        var canonical = new StringBuilder();
        foreach (var part in parts)
            canonical.Append(part.Length).Append(':').Append(part);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private sealed record LocationUpsert(MobileLocationPointEntity Entity, string Outcome);

    /// <summary>定位点的天然键：(用户, 设备, 记录时刻, 经纬度)。与唯一索引保持一致。</summary>
    private sealed record LocationKey(
        Guid UserId,
        string DeviceId,
        DateTimeOffset RecordedAtUtc,
        decimal Latitude,
        decimal Longitude)
    {
        public static LocationKey From(Guid userId, MobileLocationPointRequest request)
            => new(userId, request.DeviceId, request.RecordedAtUtc, Coordinate(request.Latitude), Coordinate(request.Longitude));

        public static LocationKey From(
            Guid userId,
            string deviceId,
            DateTimeOffset recordedAtUtc,
            decimal latitude,
            decimal longitude)
            => new(userId, deviceId, recordedAtUtc, latitude, longitude);
    }
}
