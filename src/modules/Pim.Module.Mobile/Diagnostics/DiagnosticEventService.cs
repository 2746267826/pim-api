using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;
using Pim.Module.Mobile.Services;

namespace Pim.Module.Mobile.Diagnostics;

public sealed class DiagnosticQuotaExceededException : Exception
{
    public DiagnosticQuotaExceededException(int retryAfterSeconds)
        : base("诊断事件已达到本账号的每日上限。")
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int RetryAfterSeconds { get; }
}

public sealed class DiagnosticEventService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public DiagnosticEventService(
        PimDbContext db,
        ICurrentUserService currentUser,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public async Task<DiagnosticUploadResult> UploadAsync(
        DiagnosticEventUploadRequest? request,
        CancellationToken ct = default)
    {
        if (request is null)
        {
            throw new DomainException(40001, "至少需要上报一条诊断事件。");
        }

        var userId = MobileUserContext.RequireUserId(_currentUser);
        var now = _timeProvider.GetUtcNow();
        var deviceId = NormalizeDeviceId(request.DeviceId);
        var platform = NormalizePlatform(request.Platform);
        var incoming = request.Events ?? Array.Empty<DiagnosticEventUploadItem>();

        if (incoming.Count == 0)
        {
            throw new DomainException(40001, "至少需要上报一条诊断事件。");
        }

        if (incoming.Count > DiagnosticLimits.MaxEventsPerBatch)
        {
            throw new DomainException(
                40002,
                $"单批诊断事件不能超过 {DiagnosticLimits.MaxEventsPerBatch} 条。");
        }

        var clientBatchId = Truncate(request.ClientBatchId?.Trim() ?? string.Empty, 128);
        var results = new DiagnosticUploadItemResult[incoming.Count];
        var candidates = new List<PendingEvent>(incoming.Count);
        var seenClientIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < incoming.Count; index++)
        {
            var item = incoming[index];
            if (item is null)
            {
                results[index] = Rejected(string.Empty, "null-event");
                continue;
            }

            var clientEventId = item.ClientEventId?.Trim() ?? string.Empty;
            if (clientEventId.Length == 0 || clientEventId.Length > 128)
            {
                results[index] = Rejected(clientEventId, "invalid-client-event-id");
                continue;
            }

            if (!seenClientIds.Add(clientEventId))
            {
                results[index] = Skipped(clientEventId, "duplicate-in-batch");
                continue;
            }

            var kind = NormalizeKind(item.Kind);
            if (kind is null)
            {
                results[index] = Rejected(clientEventId, "invalid-kind");
                continue;
            }

            var severity = NormalizeSeverity(item.Severity);
            if (severity is null)
            {
                results[index] = Rejected(clientEventId, "invalid-severity");
                continue;
            }

            if (item.Attributes is { Count: > DiagnosticLimits.MaxAttributes })
            {
                results[index] = Rejected(clientEventId, "too-many-attributes");
                continue;
            }

            var message = DiagnosticRedaction.RedactText(
                item.Message,
                DiagnosticLimits.MaxMessageLength);
            var stackTrace = DiagnosticRedaction.RedactText(
                item.StackTrace,
                DiagnosticLimits.MaxStackTraceLength);
            if (message.Length == 0 && stackTrace.Length == 0)
            {
                results[index] = Rejected(clientEventId, "empty-message");
                continue;
            }

            var attributes = DiagnosticRedaction.RedactAttributes(item.Attributes);
            candidates.Add(new PendingEvent(
                index,
                clientEventId,
                new DiagnosticEventEntity
                {
                    UserId = userId,
                    DeviceId = deviceId,
                    Platform = platform,
                    ClientEventId = clientEventId,
                    ClientBatchId = clientBatchId,
                    Kind = kind,
                    Severity = severity,
                    OccurredAtUtc = item.OccurredAtUtc.ToUniversalTime(),
                    ReceivedAtUtc = now,
                    AppVersion = DiagnosticRedaction.RedactText(item.AppVersion, 64),
                    OsVersion = DiagnosticRedaction.RedactText(item.OsVersion, 128),
                    Message = message,
                    StackTrace = stackTrace.Length == 0 ? null : stackTrace,
                    AttributesJson = JsonSerializer.Serialize(attributes, JsonOptions),
                    CreatedAt = now
                }));
        }

        if (candidates.Count > 0)
        {
            var candidateIds = candidates.Select(candidate => candidate.Entity.ClientEventId).ToList();
            var existingIds = await _db.Set<DiagnosticEventEntity>()
                .AsNoTracking()
                .Where(entity => entity.UserId == userId
                    && entity.DeviceId == deviceId
                    && candidateIds.Contains(entity.ClientEventId))
                .Select(entity => entity.ClientEventId)
                .ToListAsync(ct);
            var existing = existingIds.ToHashSet(StringComparer.Ordinal);

            var fresh = new List<PendingEvent>();
            foreach (var candidate in candidates)
            {
                if (existing.Contains(candidate.Entity.ClientEventId))
                {
                    results[candidate.Index] = Skipped(candidate.Entity.ClientEventId, "duplicate");
                    continue;
                }

                fresh.Add(candidate);
            }

            if (fresh.Count > 0)
            {
                var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
                var usedToday = await _db.Set<DiagnosticEventEntity>()
                    .CountAsync(entity => entity.UserId == userId
                        && entity.ReceivedAtUtc >= dayStart, ct);
                var remaining = DiagnosticLimits.MaxEventsPerUserPerUtcDay - usedToday;
                if (remaining <= 0 || fresh.Count > remaining)
                {
                    throw new DiagnosticQuotaExceededException(
                        SecondsUntilNextUtcDay(now));
                }

                _db.Set<DiagnosticEventEntity>().AddRange(fresh.Select(candidate => candidate.Entity));
                await SaveWithDuplicateRecoveryAsync(userId, deviceId, fresh, results, ct);
            }
        }

        var items = results
            .Select(result => result ?? Rejected(string.Empty, "not-processed"))
            .ToList();
        return new DiagnosticUploadResult(
            items.Count(item => item.Outcome == "accepted"),
            items.Count(item => item.Outcome == "skipped"),
            items.Count(item => item.Outcome == "rejected"),
            items);
    }

    public async Task<DiagnosticOverviewDto> GetOverviewAsync(
        string? deviceId,
        string? platform,
        int? limit,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var now = _timeProvider.GetUtcNow();
        var normalizedDeviceId = NormalizeOptionalDeviceId(deviceId);
        var normalizedPlatform = NormalizeOptionalPlatform(platform);
        var take = limit ?? 100;
        if (take is < 1 or > DiagnosticLimits.MaxEventsInOverview)
        {
            throw new DomainException(
                40003,
                $"limit 必须在 1-{DiagnosticLimits.MaxEventsInOverview} 之间。");
        }

        var scoped = _db.Set<DiagnosticEventEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId);
        if (normalizedDeviceId is not null)
        {
            scoped = scoped.Where(entity => entity.DeviceId == normalizedDeviceId);
        }

        if (normalizedPlatform is not null)
        {
            scoped = scoped.Where(entity => entity.Platform == normalizedPlatform);
        }

        var totalCount = await scoped.CountAsync(ct);
        var since = now.AddHours(-24);
        var last24HoursCount = await scoped.CountAsync(entity => entity.ReceivedAtUtc >= since, ct);

        var rawDevices = await _db.Set<DiagnosticEventEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId)
            .GroupBy(entity => new { entity.DeviceId, entity.Platform })
            .Select(group => new
            {
                group.Key.DeviceId,
                group.Key.Platform,
                EventCount = group.Count(),
                LastEventAtUtc = group.Max(entity => entity.ReceivedAtUtc)
            })
            .ToListAsync(ct);

        var devices = rawDevices
            .Select(row => new DiagnosticDeviceSummaryDto(
                RedactDeviceId(row.DeviceId),
                row.Platform,
                row.EventCount,
                row.LastEventAtUtc))
            .GroupBy(summary => new { summary.DeviceId, summary.Platform })
            .Select(group => new DiagnosticDeviceSummaryDto(
                group.Key.DeviceId,
                group.Key.Platform,
                group.Sum(summary => summary.EventCount),
                group.Max(summary => summary.LastEventAtUtc)))
            .OrderByDescending(summary => summary.LastEventAtUtc)
            .ToList();

        var rows = await scoped
            .OrderByDescending(entity => entity.ReceivedAtUtc)
            .ThenByDescending(entity => entity.Id)
            .Take(take)
            .ToListAsync(ct);

        var items = rows
            .Select(entity => new DiagnosticEventDto(
                entity.Id,
                RedactDeviceId(entity.DeviceId),
                entity.Platform,
                entity.Kind,
                entity.Severity,
                entity.OccurredAtUtc,
                entity.ReceivedAtUtc,
                DiagnosticRedaction.RedactText(entity.AppVersion, 64),
                DiagnosticRedaction.RedactText(entity.OsVersion, 128),
                DiagnosticRedaction.RedactText(entity.Message, DiagnosticLimits.MaxMessageLength)))
            .ToList();

        return new DiagnosticOverviewDto(totalCount, last24HoursCount, devices, items);
    }

    public async Task<IReadOnlyList<DiagnosticExportEventDto>> GetExportEventsAsync(
        string? deviceId,
        string? platform,
        int days,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var normalizedDeviceId = NormalizeOptionalDeviceId(deviceId);
        var normalizedPlatform = NormalizeOptionalPlatform(platform);
        if (days is < 1 or > DiagnosticLimits.MaxExportDays)
        {
            throw new DomainException(
                40004,
                $"days 必须在 1-{DiagnosticLimits.MaxExportDays} 之间。");
        }

        var since = _timeProvider.GetUtcNow().AddDays(-days);
        var query = _db.Set<DiagnosticEventEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId && entity.ReceivedAtUtc >= since);
        if (normalizedDeviceId is not null)
        {
            query = query.Where(entity => entity.DeviceId == normalizedDeviceId);
        }

        if (normalizedPlatform is not null)
        {
            query = query.Where(entity => entity.Platform == normalizedPlatform);
        }

        var rows = await query
            .OrderByDescending(entity => entity.ReceivedAtUtc)
            .ThenByDescending(entity => entity.Id)
            .Take(DiagnosticLimits.MaxEventsInExport + 1)
            .ToListAsync(ct);

        if (rows.Count > DiagnosticLimits.MaxEventsInExport)
        {
            throw new DomainException(
                41302,
                $"单次最多导出 {DiagnosticLimits.MaxEventsInExport} 条诊断事件，请缩小时间范围或按设备导出。");
        }

        return rows.Select(entity => new DiagnosticExportEventDto(
            entity.Id,
            RedactDeviceId(entity.DeviceId),
            entity.Platform,
            entity.Kind,
            entity.Severity,
            entity.OccurredAtUtc,
            entity.ReceivedAtUtc,
            DiagnosticRedaction.RedactText(entity.AppVersion, 64),
            DiagnosticRedaction.RedactText(entity.OsVersion, 128),
            DiagnosticRedaction.RedactText(entity.Message, DiagnosticLimits.MaxMessageLength),
            NullIfEmpty(DiagnosticRedaction.RedactText(
                entity.StackTrace,
                DiagnosticLimits.MaxStackTraceLength)),
            DiagnosticRedaction.RedactAttributes(ParseAttributes(entity.AttributesJson))))
            .ToList();
    }

    private async Task SaveWithDuplicateRecoveryAsync(
        Guid userId,
        string deviceId,
        List<PendingEvent> fresh,
        DiagnosticUploadItemResult[] results,
        CancellationToken ct)
    {
        while (fresh.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _db.SaveChangesAsync(ct);
                foreach (var candidate in fresh)
                {
                    results[candidate.Index] = Accepted(candidate.Entity.ClientEventId);
                }

                return;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // 一条 INSERT 语句是多行原子的：只要有一行撞唯一索引，这一批就一行都没写进去。
                // EF/Npgsql 的 DbUpdateException.Entries 可能包含整条命令的所有条目，而不是只
                // 包含冲突行；直接把它们全当重复会静默丢掉同批次里的新事件。因此先全部脱离，
                // 再回查真正已经存在的 clientEventId，只跳过这些行并重试剩余的新事件。
                foreach (var candidate in fresh)
                {
                    _db.Entry(candidate.Entity).State = EntityState.Detached;
                }

                var candidateIds = fresh
                    .Select(candidate => candidate.Entity.ClientEventId)
                    .ToList();
                var existingIds = await _db.Set<DiagnosticEventEntity>()
                    .AsNoTracking()
                    .Where(entity => entity.UserId == userId
                        && entity.DeviceId == deviceId
                        && candidateIds.Contains(entity.ClientEventId))
                    .Select(entity => entity.ClientEventId)
                    .ToListAsync(ct);
                var existing = existingIds.ToHashSet(StringComparer.Ordinal);
                if (existing.Count == 0)
                {
                    throw;
                }

                fresh.RemoveAll(candidate =>
                {
                    if (!existing.Contains(candidate.Entity.ClientEventId))
                    {
                        return false;
                    }

                    results[candidate.Index] = Skipped(candidate.Entity.ClientEventId, "duplicate");
                    return true;
                });

                foreach (var candidate in fresh)
                {
                    _db.Set<DiagnosticEventEntity>().Add(candidate.Entity);
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseAttributes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }

            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string NormalizeDeviceId(string? value)
    {
        var raw = value?.Trim() ?? string.Empty;
        if (raw.Length is < 1 or > 128)
        {
            throw new DomainException(40005, "deviceId 长度必须在 1-128 之间。");
        }

        // 设备标识由客户端提供。正常客户端传 UUID/机型名，但畸形或恶意客户端可能把
        // token、ChangeKey 或临时路径塞进这个字段；它同样会进入查询、页面和 ZIP。
        return RedactDeviceId(raw);
    }

    private static string RedactDeviceId(string? value)
    {
        var redacted = DiagnosticRedaction.RedactText(value, 128);
        return redacted.Length == 0 ? DiagnosticRedaction.Marker : redacted;
    }

    private static string? NormalizeOptionalDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeDeviceId(value);
    }

    private static string NormalizePlatform(string? value)
    {
        var platform = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!DiagnosticPlatforms.All.Contains(platform))
        {
            throw new DomainException(40006, "platform 必须是 android、windows 或 web。");
        }

        return platform;
    }

    private static string? NormalizeOptionalPlatform(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizePlatform(value);

    private static string? NormalizeKind(string? value)
    {
        var kind = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return DiagnosticEventKinds.All.Contains(kind) ? kind : null;
    }

    private static string? NormalizeSeverity(string? value)
    {
        var severity = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return DiagnosticSeverities.All.Contains(severity) ? severity : null;
    }

    private static int SecondsUntilNextUtcDay(DateTimeOffset now)
    {
        var next = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        return Math.Max(1, (int)Math.Ceiling((next - now).TotalSeconds));
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string? NullIfEmpty(string value)
        => value.Length == 0 ? null : value;

    private static DiagnosticUploadItemResult Accepted(string clientEventId)
        => new(clientEventId, "accepted", "0");

    private static DiagnosticUploadItemResult Skipped(string clientEventId, string code)
        => new(clientEventId, "skipped", code);

    private static DiagnosticUploadItemResult Rejected(string clientEventId, string code)
        => new(clientEventId, "rejected", code);

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            if (sqlState == "23505")
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("23505", StringComparison.Ordinal)
                || message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record PendingEvent(
        int Index,
        string ClientEventId,
        DiagnosticEventEntity Entity);
}
