using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Liveness;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 取证事件上报入口（REQ-5 / REQ-6 / REQ-9）。走**独立通道**，不改动也复用既有同步批次语义（工单 §7.3）。
/// <para>
/// 幂等（AC-5.2）：(user, device, clientItemKey) 已存在的事件直接计入 skipped，条数不变；
/// 丢弃原因按天统计用 (user, device, localDate, reason) 覆盖写，重复上报不会翻倍。
/// </para>
/// <para>
/// 只写这一条通道：任何单条事件的异常都被收敛成 rejected，不影响其余条目，也不影响采集与既有同步。
/// </para>
/// </summary>
public sealed class MobileForensicIngestService
{
    /// <summary>单批事件上限，防止一次请求把整张表拉进内存（沿用既有批量语义的保守上限）。</summary>
    public const int MaxEventsPerBatch = 500;

    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.Ordinal)
    {
        ForensicEventTypes.ProcessExit,
        ForensicEventTypes.ForceStop,
        ForensicEventTypes.Heartbeat,
        // 阶段二（REQ-14 / REQ-18 / REQ-21）：设备端新增的三种事件类型必须在这里登记，
        // 否则会被下面的契约校验按「未知类型」拒绝（REQ-28 的有意设计），
        // 表现为「闹钟兑现在设备上记了、却永远到不了服务端」。
        ForensicEventTypes.AlarmFulfillment,
        ForensicEventTypes.AlarmRegistered,
        ForensicEventTypes.KeepAliveHealth,
        // WO-ANDROID-GATE-20260926（定位精度门 + 30 秒冲刺 + 被动定位）：设备端新增的
        // 两种事件类型同样必须在这里登记，否则会被下面的契约校验按「未知类型」拒绝
        // （REQ-28 的有意设计），表现为「冲刺台账在设备上记了、却永远到不了服务端」。
        ForensicEventTypes.LocationSprint,
        ForensicEventTypes.PassiveLocationCounter,
    };

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MobileForensicIngestService> _logger;

    public MobileForensicIngestService(
        PimDbContext db,
        ICurrentUserService currentUser,
        TimeProvider timeProvider,
        ILogger<MobileForensicIngestService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<MobileForensicsIngestResult> IngestAsync(
        MobileForensicsUploadRequest request,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var now = _timeProvider.GetUtcNow();
        var deviceId = request.DeviceId?.Trim() ?? string.Empty;

        var accepted = new List<string>();
        var skipped = new List<string>();
        var rejected = new List<string>();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return new MobileForensicsIngestResult(0, 0, 0, 0, accepted, skipped, rejected, 0);
        }

        var incoming = request.Events ?? Array.Empty<MobileForensicEventUploadItem>();

        // 事件类型不在契约里的条目一律显式拒绝，不静默丢弃：旧客户端多带字段没关系（AC-30.2），
        // 但多带**类型**说明两端契约不一致，必须留下可见痕迹（REQ-28）。
        var candidates = new List<MobileForensicEventEntity>();        foreach (var item in incoming.Take(MaxEventsPerBatch))
        {
            if (string.IsNullOrWhiteSpace(item.ClientItemKey) ||
                !KnownEventTypes.Contains(item.EventType ?? string.Empty))
            {
                rejected.Add(item.ClientItemKey ?? string.Empty);
                continue;
            }

            candidates.Add(new MobileForensicEventEntity
            {
                UserId = userId,
                DeviceId = deviceId,
                EventType = item.EventType!,
                ClientItemKey = item.ClientItemKey,
                OccurredAtUtc = item.OccurredAtUtc,
                PayloadJson = NormalizePayload(item.PayloadJson),
                ReceivedAtUtc = now,
                CreatedAt = now,
            });
        }

        // 超出上限的部分显式记为 rejected，而不是假装全都收下了。
        for (var index = MaxEventsPerBatch; index < incoming.Count; index++)
        {
            rejected.Add(incoming[index].ClientItemKey ?? string.Empty);
        }

        if (candidates.Count > 0)
        {
            var keys = candidates.Select(candidate => candidate.ClientItemKey).ToList();
            var existing = await _db.Set<MobileForensicEventEntity>()
                .AsNoTracking()
                .Where(entity => entity.UserId == userId
                    && entity.DeviceId == deviceId
                    && keys.Contains(entity.ClientItemKey))
                .Select(entity => entity.ClientItemKey)
                .ToListAsync(ct);

            var existingKeys = existing.ToHashSet(StringComparer.Ordinal);
            var fresh = candidates.Where(candidate => !existingKeys.Contains(candidate.ClientItemKey)).ToList();

            foreach (var candidate in candidates)
            {
                if (existingKeys.Contains(candidate.ClientItemKey))
                {
                    skipped.Add(candidate.ClientItemKey);
                }
            }

            if (fresh.Count > 0)
            {
                _db.Set<MobileForensicEventEntity>().AddRange(fresh);
            }
        }

        var acceptedDropped = await UpsertDroppedReasonSummariesAsync(
            userId,
            deviceId,
            request.DroppedReasonSummaries,
            now,
            ct);

        // 事件与丢弃原因统计都走同一次 SaveChanges：只写事件却不落统计（或反过来）会让
        // "设备端条数 == 服务端条数" 的核对结果随提交路径不同而漂移（AC-5.1 / AC-9.2）。
        //
        // 并发重试（客户端同一批数据在两个连接上同时重发）可能让两个请求都通过上面的
        // "查已存在"，其中一个随后撞 (user, device, clientItemKey) 唯一索引。这**不是**失败：
        // 数据已经在了，语义就是 skipped，因此这里把唯一约束冲突翻译成 skipped（AC-5.2），
        // 而不是把 500 抛回客户端。
        var duplicatesOnInsert = new List<string>();
        while (true)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                foreach (var entry in ex.Entries)
                {
                    if (entry.Entity is MobileForensicEventEntity forensic)
                    {
                        duplicatesOnInsert.Add(forensic.ClientItemKey);
                    }

                    entry.State = EntityState.Detached;
                }

                if (duplicatesOnInsert.Count == 0)
                {
                    throw;
                }

                skipped.AddRange(duplicatesOnInsert);
                duplicatesOnInsert.Clear();
            }
        }

        accepted.AddRange(candidates
            .Select(candidate => candidate.ClientItemKey)
            .Where(key => !skipped.Contains(key, StringComparer.Ordinal)));

        _logger.LogInformation(
            "取证事件上报：Device={DeviceId}, Accepted={Accepted}, Skipped={Skipped}, Rejected={Rejected}, DroppedReasonRows={Dropped}",
            deviceId, accepted.Count, skipped.Count, rejected.Count, acceptedDropped);

        return new MobileForensicsIngestResult(
            accepted.Count,
            skipped.Count,
            rejected.Count,
            0,
            accepted,
            skipped,
            rejected,
            acceptedDropped);
    }

    /// <summary>
    /// 丢弃原因按天统计：天然键命中的行**覆盖**计数（不是累加），因此重复上报不会翻倍。
    /// </summary>
    private async Task<int> UpsertDroppedReasonSummariesAsync(
        Guid userId,
        string deviceId,
        IReadOnlyList<MobileDroppedReasonSummaryItem>? summaries,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (summaries is null || summaries.Count == 0)
        {
            return 0;
        }

        var cleaned = summaries
            .Where(item => !string.IsNullOrWhiteSpace(item.LocalDate) && !string.IsNullOrWhiteSpace(item.Reason))
            .GroupBy(item => (Date: item.LocalDate.Trim(), Reason: item.Reason.Trim()))
            .Select(group => new MobileDroppedReasonSummaryItem(
                group.Key.Date,
                group.Key.Reason,
                group.Max(item => Math.Max(0, item.Count))))
            .ToList();

        if (cleaned.Count == 0)
        {
            return 0;
        }

        var dates = cleaned.Select(item => item.LocalDate).Distinct().ToList();
        var existing = await _db.Set<MobileDroppedReasonDailyEntity>()
            .Where(entity => entity.UserId == userId
                && entity.DeviceId == deviceId
                && dates.Contains(entity.LocalDate))
            .ToListAsync(ct);

        var byKey = existing.ToDictionary(
            entity => (entity.LocalDate, entity.Reason),
            entity => entity);

        foreach (var item in cleaned)
        {
            if (byKey.TryGetValue((item.LocalDate, item.Reason), out var entity))
            {
                entity.Count = item.Count;
                entity.ReceivedAtUtc = now;
            }
            else
            {
                _db.Set<MobileDroppedReasonDailyEntity>().Add(new MobileDroppedReasonDailyEntity
                {
                    UserId = userId,
                    DeviceId = deviceId,
                    LocalDate = item.LocalDate,
                    Reason = item.Reason,
                    Count = item.Count,
                    ReceivedAtUtc = now,
                    CreatedAt = now,
                });
            }
        }

        return cleaned.Count;
    }

    /// <summary>唯一约束冲突（PostgreSQL 23505 / SQLite 2067）判定：并发重复提交的预期分支。</summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            var sqlState = type.GetProperty("SqlState")?.GetValue(current) as string;
            if (sqlState == "23505")
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("23505", StringComparison.Ordinal) ||
                message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 负载原样保存为 jsonb；空值存 <c>{}</c>。非法 JSON 一律收敛为 <c>{}</c> 并保留原始文本片段，
    /// 不因为一条畸形负载让整批上报失败。
    /// </summary>
    private static string NormalizePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? payloadJson
                : "{\"raw\":" + JsonSerializer.Serialize(payloadJson) + "}";
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new Dictionary<string, string> { ["malformed"] = payloadJson });
        }
    }
}
