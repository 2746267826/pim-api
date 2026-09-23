using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Liveness;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 「设备存活」数据服务（REQ-7 / REQ-10 / REQ-11 / REQ-13）。
/// <para>
/// 静默与覆盖率的口径**只有一份实现**：<see cref="DeviceLivenessCalculator"/>。
/// 本服务只负责取数（心搏事件 + 同步批次到达 + 死因事件）与把结果映射成接口契约，
/// 因此页面、REST 摘要、MCP 工具与体检数据项必然得到同一组数字（AC-11.2 / AC-13.1）。
/// </para>
/// <para>
/// 存活证据只用两类：<c>heartbeat</c> 事件（主）与 <c>mobile_sync_batches</c> 的**到达时刻**
/// （辅）。使用事件与缺口回补窗口都不是存活证据（AC-7.6 / AC-13.3），因此本服务不查询它们。
/// </para>
/// </summary>
public sealed class MobileLivenessService : IDeviceLivenessInspectionProvider
{
    /// <summary>默认区间：最近 7 天（AC-7.1）。</summary>
    public static readonly TimeSpan DefaultRange = TimeSpan.FromDays(7);

    /// <summary>单页事件上限（沿用既有分页约束，AC-31 无自拟阈值）。</summary>
    public const int MaxPageSize = 200;

    public const int DefaultPageSize = 50;

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileLivenessService(
        PimDbContext db,
        ICurrentUserService currentUser,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    /// <summary>解析查询区间；缺省为最近 7 天，终点不晚于"现在"。</summary>
    public (DateTimeOffset Start, DateTimeOffset End) ResolveRange(
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc)
    {
        var now = _timeProvider.GetUtcNow();
        var end = rangeEndUtc ?? now;
        if (end > now)
        {
            end = now;
        }

        var start = rangeStartUtc ?? end - DefaultRange;
        if (start >= end)
        {
            start = end - DefaultRange;
        }

        return (start, end);
    }

    /// <summary>Web「设备存活」子页首屏：按机型分块，手机与平板统计不混算（AC-7.3）。</summary>
    public async Task<MobileLivenessOverviewResponse> GetOverviewAsync(
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var (start, end) = ResolveRange(rangeStartUtc, rangeEndUtc);

        // 从未上报的设备也要出现在列表里并显示"无数据/未上报"（AC-7.5），因此以设备表为准。
        var devices = await _db.Set<MobileDeviceEntity>()
            .AsNoTracking()
            .Where(device => device.UserId == userId)
            .OrderBy(device => device.DisplayName)
            .ToListAsync(ct);

        var blocks = new List<MobileDeviceLivenessDto>();
        foreach (var device in devices)
        {
            blocks.Add(await BuildDeviceBlockAsync(userId, device, start, end, ct));
        }

        var phones = blocks.Where(block => block.DeviceKind == DeviceKinds.Phone).ToList();
        var tablets = blocks.Where(block => block.DeviceKind == DeviceKinds.Tablet).ToList();
        var unclassified = blocks.Where(block => block.DeviceKind == DeviceKinds.Unknown).ToList();

        return new MobileLivenessOverviewResponse(
            start,
            end,
            DeviceLivenessRules.ExpectedHeartbeatIntervalMinutes,
            phones,
            tablets,
            unclassified);
    }

    /// <summary>单设备指定区间存活摘要（REQ-11 / AC-11.1 / AC-11.4）。</summary>
    public async Task<MobileDeviceLivenessDto?> GetDeviceLivenessAsync(
        string deviceId,
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var (start, end) = ResolveRange(rangeStartUtc, rangeEndUtc);

        var device = await _db.Set<MobileDeviceEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.DeviceId == deviceId, ct);

        if (device is null)
        {
            return null;
        }

        return await BuildDeviceBlockAsync(userId, device, start, end, ct);
    }

    /// <summary>逐条存活事件（REQ-7.2：时刻、类型、原因、上下文，可查看原始 JSON）。</summary>
    public async Task<MobileLivenessEventPageDto> GetEventsAsync(
        string deviceId,
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        int? page,
        int? pageSize,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var (start, end) = ResolveRange(rangeStartUtc, rangeEndUtc);

        var effectivePage = Math.Max(1, page ?? 1);
        var effectivePageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var query = _db.Set<MobileForensicEventEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId
                && entity.DeviceId == deviceId
                && entity.OccurredAtUtc >= start
                && entity.OccurredAtUtc < end);

        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(entity => entity.OccurredAtUtc)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(ct);

        var items = rows.Select(MapEvent).ToList();
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / effectivePageSize);

        return new MobileLivenessEventPageDto(items, effectivePage, effectivePageSize, totalCount, totalPages);
    }

    /// <summary>按天按原因的丢弃统计（REQ-9 / AC-9.2）。</summary>
    public async Task<MobileDroppedReasonResponse> GetDroppedReasonsAsync(
        string deviceId,
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        CancellationToken ct = default)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var (start, end) = ResolveRange(rangeStartUtc, rangeEndUtc);

        // local_date 是设备本地日（yyyy-MM-dd）。这里按设备把该表读回内存再按日期过滤：
        // 表体量极小（每设备每天每原因一行），而字符串区间比较在不同数据库上的可翻译性不一致，
        // 放进 SQL 反而会带来"某些库能跑、某些库报错"的隐性差异。
        var startDate = start.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
        var endDate = end.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");

        var allRows = await _db.Set<MobileDroppedReasonDailyEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId && entity.DeviceId == deviceId)
            .Select(entity => new { entity.LocalDate, entity.Reason, entity.Count })
            .ToListAsync(ct);

        var rows = allRows
            .Where(row => string.CompareOrdinal(row.LocalDate, startDate) >= 0
                && string.CompareOrdinal(row.LocalDate, endDate) <= 0)
            .OrderBy(row => row.LocalDate, StringComparer.Ordinal)
            .ThenBy(row => row.Reason, StringComparer.Ordinal)
            .Select(row => new MobileDroppedReasonDailyDto(row.LocalDate, row.Reason, row.Count))
            .ToList();

        return new MobileDroppedReasonResponse(deviceId, start, end, rows, rows.Sum(row => row.Count));
    }

    /// <summary>体检输出里的「设备存活」数据项（REQ-10）。刻意不产出任何红/黄/绿档位（AC-10.3）。</summary>
    public async Task<IReadOnlyList<DeviceLivenessInspectionItem>> GetLivenessForInspectionAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken ct = default)
    {
        var userId = _currentUser.UserId;
        if (userId is null)
        {
            return Array.Empty<DeviceLivenessInspectionItem>();
        }

        var devices = await _db.Set<MobileDeviceEntity>()
            .AsNoTracking()
            .Where(device => device.UserId == userId.Value)
            .OrderBy(device => device.DisplayName)
            .ToListAsync(ct);

        var items = new List<DeviceLivenessInspectionItem>();
        foreach (var device in devices)
        {
            var block = await BuildDeviceBlockAsync(
                userId.Value,
                device,
                rangeStartUtc,
                rangeEndUtc,
                ct);
            items.Add(new DeviceLivenessInspectionItem(
                block.DeviceId,
                block.DisplayName,
                block.DeviceKind,
                ToSummary(block)));
        }

        return items;
    }

    private static DeviceLivenessSummary ToSummary(MobileDeviceLivenessDto block)
        => new(
            block.HasData,
            block.Conclusion,
            block.CoverageByHour,
            block.CoverageByExpectedHeartbeat,
            block.ObservedHours,
            block.TotalHours,
            block.ObservedHeartbeats,
            block.ExpectedHeartbeats,
            block.ExpectedHeartbeatIntervalMinutes,
            block.LongestSilenceMinutes,
            block.LongestSilenceStartUtc,
            block.LongestSilenceEndUtc,
            block.LongestSilenceSeverity,
            block.HasSilenceOverOneHour,
            block.Silences
                .Select(item => new SilenceWindow(item.StartUtc, item.EndUtc, item.Minutes, item.Severity))
                .ToList(),
            block.Causes
                .Select(item => new LivenessCauseCount(item.Cause, item.Label, item.Count, item.Inference))
                .ToList(),
            block.LastEventAtUtc,
            block.CoverageByHourDefinition,
            block.CoverageByExpectedHeartbeatDefinition);

    private async Task<MobileDeviceLivenessDto> BuildDeviceBlockAsync(
        Guid userId,
        MobileDeviceEntity device,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        var deviceId = device.DeviceId;

        var forensicEvents = await _db.Set<MobileForensicEventEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId
                && entity.DeviceId == deviceId
                && entity.OccurredAtUtc >= start
                && entity.OccurredAtUtc < end)
            .Select(entity => new
            {
                entity.EventType,
                entity.OccurredAtUtc,
                entity.PayloadJson,
            })
            .ToListAsync(ct);

        // 同步批次只取"到达时刻"（CreatedAt），它的 window_* 是数据补传语义，不能当存活证据（AC-13.3）。
        var batchArrivals = await _db.Set<MobileSyncBatchEntity>()
            .AsNoTracking()
            .Where(entity => entity.UserId == userId
                && entity.DeviceId == deviceId
                && entity.CreatedAt >= start
                && entity.CreatedAt < end)
            .Select(entity => entity.CreatedAt)
            .ToListAsync(ct);

        var evidence = new List<LivenessEvidence>();
        var causes = new List<LivenessCauseCount>();

        foreach (var item in forensicEvents)
        {
            if (item.EventType == ForensicEventTypes.Heartbeat)
            {
                evidence.Add(new LivenessEvidence(item.OccurredAtUtc, LivenessEvidenceSources.Heartbeat));
            }

            var classified = MobileLivenessCauseClassifier.Classify(item.EventType, item.PayloadJson);
            if (classified is { } cause)
            {
                causes.Add(new LivenessCauseCount(cause.Cause, cause.Label, 1, cause.Inference));
            }
        }

        evidence.AddRange(batchArrivals.Select(at => new LivenessEvidence(at, LivenessEvidenceSources.SyncBatch)));

        var summary = DeviceLivenessCalculator.Summarize(evidence, causes, start, end);
        var kind = ResolveDeviceKind(device.MetadataJson);

        return MapDeviceBlock(device, kind, summary);
    }

    private static MobileLivenessEventDto MapEvent(MobileForensicEventEntity entity)
    {
        using var document = MobileLivenessCauseClassifier.TryParse(entity.PayloadJson);
        var root = document?.RootElement;

        var classified = MobileLivenessCauseClassifier.Classify(entity.EventType, entity.PayloadJson);
        var context = ReadContext(root);

        return new MobileLivenessEventDto(
            entity.Id,
            entity.EventType,
            EventTypeLabel(entity.EventType),
            entity.OccurredAtUtc,
            classified is { } cause ? cause.Cause : null,
            classified is { } label ? label.Label : null,
            classified is { } inference ? inference.Inference : null,
            MobileLivenessCauseClassifier.ReadInt(root, "importance"),
            MobileLivenessCauseClassifier.ReadLong(root, "pssKb"),
            MobileLivenessCauseClassifier.ReadLong(root, "rssKb"),
            context,
            entity.PayloadJson);
    }

    /// <summary>系统描述文本为空时留空，不填猜测值（AC-4.2）。</summary>
    private static string? ReadContext(JsonElement? root)
    {
        if (root is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parts = new List<string>();
        Append(parts, "屏幕", MobileLivenessCauseClassifier.ReadString(element, "screenOn"), "亮", "灭");
        Append(parts, "解锁", MobileLivenessCauseClassifier.ReadString(element, "unlocked"), "已解锁", "未解锁");
        Append(parts, "充电", MobileLivenessCauseClassifier.ReadString(element, "charging"), "充电中", "未充电");

        var battery = MobileLivenessCauseClassifier.ReadInt(element, "batteryPercent");
        if (battery.HasValue)
        {
            parts.Add($"电量 {battery.Value}%");
        }

        var foreground = MobileLivenessCauseClassifier.ReadString(element, "foregroundAppLabel")
            ?? MobileLivenessCauseClassifier.ReadString(element, "foregroundPackage");
        if (!string.IsNullOrWhiteSpace(foreground))
        {
            parts.Add($"前台 {foreground}");
        }

        var description = MobileLivenessCauseClassifier.ReadString(element, "description");
        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add(description);
        }

        var unavailable = MobileLivenessCauseClassifier.ReadString(element, "unavailableFields");
        if (!string.IsNullOrWhiteSpace(unavailable))
        {
            parts.Add($"不可用字段：{unavailable}");
        }

        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private static void Append(List<string> parts, string label, string? raw, string onText, string offText)
    {
        switch (raw)
        {
            case null:
                return;
            case "true":
                parts.Add($"{label}{onText}");
                break;
            case "false":
                parts.Add($"{label}{offText}");
                break;
            default:
                return;
        }
    }

    public static string EventTypeLabel(string eventType) => eventType switch
    {
        ForensicEventTypes.ProcessExit => "进程退出",
        ForensicEventTypes.ForceStop => "强停/重启",
        ForensicEventTypes.Heartbeat => "存活心跳",
        _ => "未知事件",
    };

    private static MobileDeviceLivenessDto MapDeviceBlock(
        MobileDeviceEntity device,
        string kind,
        DeviceLivenessSummary summary)
        => new(
            device.DeviceId,
            string.IsNullOrWhiteSpace(device.DisplayName) ? device.DeviceId : device.DisplayName,
            kind,
            DeviceKinds.Label(kind),
            summary.HasData,
            summary.Conclusion,
            summary.CoverageByHour,
            summary.CoverageByExpectedHeartbeat,
            summary.ObservedHours,
            summary.TotalHours,
            summary.ObservedHeartbeats,
            summary.ExpectedHeartbeats,
            summary.ExpectedHeartbeatIntervalMinutes,
            summary.LongestSilenceMinutes,
            summary.LongestSilenceStartUtc,
            summary.LongestSilenceEndUtc,
            summary.LongestSilenceSeverity,
            summary.HasSilenceOverOneHour,
            summary.Silences
                .Select(item => new MobileSilenceWindowDto(
                    item.StartUtc,
                    item.EndUtc,
                    item.Minutes,
                    item.Severity,
                    SilenceSeverityLabel(item.Severity)))
                .ToList(),
            summary.Causes
                .Select(item => new MobileLivenessCauseDto(item.Cause, item.Label, item.Count, item.Inference))
                .ToList(),
            summary.LastEventAtUtc,
            summary.CoverageByHourDefinition,
            summary.CoverageByExpectedHeartbeatDefinition);

    public static string SilenceSeverityLabel(string severity) => severity switch
    {
        SilenceSeverities.Critical => "严重（≥1 小时）",
        SilenceSeverities.Warning => "警告（≥30 分钟）",
        _ => "未达标记线",
    };

    /// <summary>
    /// 机型分类（AC-7.3：手机与平板分别成块，统计不混算）。
    /// 新客户端在注册元数据里带 <c>deviceKind</c>；旧客户端没有该字段时退回
    /// <c>smallestScreenWidthDp ≥ 600</c> 的 Android 官方平板判定线，仍无法判定则归入"未分类"，
    /// 绝不把未知机型悄悄并进手机块。
    /// </summary>
    public static string ResolveDeviceKind(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return DeviceKinds.Unknown;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return DeviceKinds.Unknown;
            }

            if (root.TryGetProperty("deviceKind", out var kind) && kind.ValueKind == JsonValueKind.String)
            {
                var text = kind.GetString()?.Trim().ToLowerInvariant();
                if (text == DeviceKinds.Phone || text == DeviceKinds.Tablet)
                {
                    return text;
                }
            }

            if (root.TryGetProperty("smallestScreenWidthDp", out var sw) &&
                sw.ValueKind == JsonValueKind.Number &&
                sw.TryGetInt32(out var width))
            {
                return width >= 600 ? DeviceKinds.Tablet : DeviceKinds.Phone;
            }
        }
        catch (JsonException)
        {
            return DeviceKinds.Unknown;
        }

        return DeviceKinds.Unknown;
    }
}

/// <summary>机型分块标识（AC-7.3）。</summary>
public static class DeviceKinds
{
    public const string Phone = "phone";
    public const string Tablet = "tablet";
    public const string Unknown = "unknown";

    public static string Label(string kind) => kind switch
    {
        Phone => "手机",
        Tablet => "平板",
        _ => "未分类机型",
    };
}
