using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Operations;

public sealed class DaemonHeartbeatService : IDaemonHeartbeatService
{
    private readonly PimDbContext _db;
    private readonly TimeProvider _timeProvider;

    public DaemonHeartbeatService(PimDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DaemonHeartbeatDto> UpsertAsync(
        DaemonHeartbeatRequest request,
        CancellationToken ct = default)
    {
        var statusJson = NormalizeStatusJson(request.StatusJson);
        var entity = await FindLatestDeviceEntityAsync(request.DeviceId, request.DaemonKind, ct);

        var isNew = entity is null;
        if (entity is null)
        {
            entity = new DaemonHeartbeatEntity
            {
                DeviceId = Truncate(request.DeviceId, 128) ?? string.Empty,
                DaemonKind = Truncate(request.DaemonKind, 32) ?? "windows"
            };
            _db.DaemonHeartbeats.Add(entity);
        }

        Apply(request, statusJson, entity);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            entity = await FindLatestDeviceEntityAsync(request.DeviceId, request.DaemonKind, ct);

            if (entity is null)
            {
                entity = new DaemonHeartbeatEntity
                {
                    DeviceId = Truncate(request.DeviceId, 128) ?? string.Empty,
                    DaemonKind = Truncate(request.DaemonKind, 32) ?? "windows"
                };
                _db.DaemonHeartbeats.Add(entity);
            }

            Apply(request, statusJson, entity);
            await _db.SaveChangesAsync(ct);
        }

        return Map(entity);
    }

    /// <summary>
    /// 接收客户端在休眠/关机/注销钩子里上报的「计划内下线」声明。
    ///
    /// 实现口径（WO-ISSUES-396-400-20261007 REQ-4 / issue #398，本轮修正）：
    ///   这里**不做陈旧性丢弃**。旧实现有一条 5 分钟容忍窗（<c>PlannedOfflineStaleTolerance</c>），
    ///   迟到超过 5 分钟的 suspend 声明被直接丢弃、连行都不建。而"钩子里发出、进程随即被系统冻结"
    ///   正是这种迟到形态：请求在挂起瞬间未送达，唤醒后重传，抵达时早已超过 5 分钟 —— 声明被吃掉，
    ///   整段休眠区间在 S6 里变成「无声明空档」。
    ///   声明本身带 <c>occurredAt</c>（休眠起点），迟到与否不影响它对**那段区间**的解释力：
    ///   S6 按「声明时刻是否落在这个空档 ±5 分钟内」认定覆盖，因此迟到声明既不会失效，
    ///   也不会被当成"此后永久离线"（时点声明只对它覆盖的那一段负责）。
    /// </summary>
    public async Task<DaemonHeartbeatDto?> RecordPlannedOfflineAsync(
        PlannedOfflineRequest request,
        CancellationToken ct = default)
    {
        var entity = await FindLatestDeviceEntityAsync(request.DeviceId, request.DaemonKind, ct);

        var isNew = entity is null;
        if (entity is null)
        {
            entity = new DaemonHeartbeatEntity
            {
                DeviceId = Truncate(request.DeviceId, 128) ?? string.Empty,
                DaemonKind = Truncate(request.DaemonKind, 32) ?? "windows"
            };
            _db.DaemonHeartbeats.Add(entity);
        }

        ApplyPlannedOffline(request, entity, isNew);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            entity = await FindLatestDeviceEntityAsync(request.DeviceId, request.DaemonKind, ct);

            if (entity is null)
            {
                entity = new DaemonHeartbeatEntity
                {
                    DeviceId = Truncate(request.DeviceId, 128) ?? string.Empty,
                    DaemonKind = Truncate(request.DaemonKind, 32) ?? "windows"
                };
                _db.DaemonHeartbeats.Add(entity);
            }

            ApplyPlannedOffline(request, entity, isNew: false);
            await _db.SaveChangesAsync(ct);
        }

        return Map(entity);
    }

    private async Task<DaemonHeartbeatEntity?> FindLatestDeviceEntityAsync(
        string deviceId,
        string daemonKind,
        CancellationToken ct)
    {
        var matches = await _db.DaemonHeartbeats
            .Where(d => d.DeviceId == deviceId && d.DaemonKind == daemonKind)
            .OrderByDescending(d => d.ReceivedAt)
            .ToListAsync(ct);

        if (matches.Count == 0) return null;

        var latest = matches[0];
        if (matches.Count > 1)
        {
            // 自愈清理：清除历史并发或脏数据产生的重复旧行，保留最新的一条
            _db.DaemonHeartbeats.RemoveRange(matches.Skip(1));
        }

        return latest;
    }

    public async Task<DaemonHeartbeatDto?> GetLatestAsync(string deviceId, CancellationToken ct = default)
    {
        var entity = await _db.DaemonHeartbeats
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId)
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : Map(entity);
    }

    public async Task<DaemonHeartbeatDto?> GetLatestWindowsAsync(CancellationToken ct = default)
    {
        var entity = await _db.DaemonHeartbeats
            .AsNoTracking()
            .Where(d => d.DaemonKind == "windows")
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<DaemonHeartbeatDto>> ListAsync(CancellationToken ct = default)
    {
        var entities = await _db.DaemonHeartbeats
            .AsNoTracking()
            .OrderByDescending(d => d.ReceivedAt)
            .ToListAsync(ct);

        return entities
            .GroupBy(d => new { d.DeviceId, d.DaemonKind })
            .Select(g => Map(g.First()))
            .ToList();
    }

    private static DaemonHeartbeatDto Map(DaemonHeartbeatEntity entity)
    {
        return new DaemonHeartbeatDto(
            entity.DeviceId,
            entity.DaemonKind,
            entity.Version,
            entity.ServerUrl,
            entity.LastSuccessfulUploadAt,
            entity.LastAttemptedUploadAt,
            entity.LastError,
            entity.UploadQueueCount,
            ParseSourceState(entity.ActivityWatchState),
            ParseSourceState(entity.KeyStatsState),
            entity.CollectionPaused,
            entity.StatusJson,
            entity.ReceivedAt,
            entity.PlannedOfflineAt,
            entity.OfflineReason);
    }

    private void ApplyPlannedOffline(
        PlannedOfflineRequest request,
        DaemonHeartbeatEntity entity,
        bool isNew)
    {
        // planned_offline 只写 planned 标记，不刷新 received_at（received_at 语义 = 最近普通心跳）。
        //
        // 声明时刻**原样存储客户端上报的 occurredAt**，不做任何改写
        // （WO-ISSUES-396-400-20261007 REQ-4 / issue #398；复审 Important 修正）：
        //   * S6 用这个时刻判断"这个空档是否被声明覆盖"，S7 也跟着变；
        //     把迟到声明的时刻改写成"最近一次心跳时刻"，会让它落进另一段空档的 ±5 分钟宽限里，
        //     从而把一段**没有声明的真实断档**判成已声明；
        //   * 验收项 AC-4.5 要求唤醒后 planned_offline_at 仍等于休眠起点，改写同样会破坏它。
        // 旧实现有一条"planned_at >= received_at"的时钟钳制；"设备此刻是否处于计划内离线"改由
        // <see cref="DaemonLifecycleClassifier.IsCurrentlyPlannedOffline"/> 用比较判断（声明比最近心跳新），
        // 不再靠改写存储时刻实现。
        var plannedAt = request.OccurredAt ?? _timeProvider.GetUtcNow();

        entity.PlannedOfflineAt = plannedAt;
        entity.OfflineReason = Truncate(request.Reason, 32);

        // 新建行时用同一注入时钟统一 received_at/planned_offline_at，保证 planned_offline_at >= received_at
        // 对"刚从声明建出来的行"恒成立（该设备确实还没有新的心跳）。
        if (isNew)
        {
            entity.ReceivedAt = plannedAt;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private void Apply(
        DaemonHeartbeatRequest request,
        string statusJson,
        DaemonHeartbeatEntity entity)
    {
        entity.Version = Truncate(request.Version, 64) ?? string.Empty;
        entity.ServerUrl = Truncate(request.ServerUrl, 512) ?? string.Empty;
        entity.LastSuccessfulUploadAt = request.LastSuccessfulUploadAt;
        entity.LastAttemptedUploadAt = request.LastAttemptedUploadAt;
        entity.LastError = request.LastError;
        entity.UploadQueueCount = request.UploadQueueCount;
        entity.ActivityWatchState = Truncate(request.ActivityWatchState.ToString(), 32) ?? DaemonSourceState.Unknown.ToString();
        entity.KeyStatsState = Truncate(request.KeyStatsState.ToString(), 32) ?? DaemonSourceState.Unknown.ToString();
        entity.CollectionPaused = request.CollectionPaused;
        entity.StatusJson = statusJson;
        entity.ReceivedAt = _timeProvider.GetUtcNow();

        // 普通心跳**不得**清空 planned_offline_at / offline_reason
        // （WO-ISSUES-396-400-20261007 REQ-4 / issue #398）。
        //
        // 旧实现每次心跳都把这两个字段置空，于是"休眠前留下声明 → 唤醒后第一跳心跳"必然把声明吃掉：
        // 服务端再也读不到它，S6 只能把整段休眠判成「无声明空档」，S7 判成「未标记空洞」。
        //
        // 保留声明不会把设备当成"永久离线"：声明是**时点**语义（客户端只报"我正要下线"），
        // 生命周期分类器要求 planned_offline_at >= received_at 才算「计划内下线」，
        // 唤醒后 received_at 已经晚于声明时刻，分类自动回到在线/退化分支；
        // S6 也只要求声明时刻落在**那一个**空档内，之后 40 分钟的真实断档照常报红（REQ-4 反面行为）。
    }

    private static string NormalizeStatusJson(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return "{}";
        }

        try
        {
            using var _ = JsonDocument.Parse(statusJson);
            return statusJson;
        }
        catch (JsonException)
        {
            throw new DomainException(3010, "StatusJson 必须是有效 JSON");
        }
    }

    private static DaemonSourceState ParseSourceState(string value)
        => Enum.TryParse<DaemonSourceState>(value, ignoreCase: true, out var state)
            ? state
            : DaemonSourceState.Unknown;
}
