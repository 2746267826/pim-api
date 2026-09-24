using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Module.Mobile.Entities;

/// <summary>
/// 安卓端取证事件（阶段一 REQ-1/2/3/4/6/9）：进程退出原因、强停/重启、存活心跳及其上下文。
/// <para>
/// 与 <c>mobile_sync_batches</c> 是**两条独立通道**（工单 §7.3）：取证事件不改动、也不复用
/// 既有批次语义。原始事件在服务端长期保留（REQ-6），不做时间清理。
/// </para>
/// <para>
/// 幂等键是 (user_id, device_id, client_item_key)（AC-5.2）：客户端重复提交同一批数据时
/// 命中唯一约束被跳过，事件条数不变。
/// </para>
/// </summary>
[Table("mobile_forensic_events")]
public sealed class MobileForensicEventEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>事件类型：<c>process-exit</c> / <c>force-stop</c> / <c>heartbeat</c>。</summary>
    [Column("event_type")]
    [MaxLength(64)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>设备端生成的幂等键（同设备内唯一）。</summary>
    [Column("client_item_key")]
    [MaxLength(256)]
    public string ClientItemKey { get; set; } = string.Empty;

    /// <summary>事件发生时刻（设备时钟，UTC）。</summary>
    [Column("occurred_at_utc")]
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>事件负载（原因、内存、开机时长、待机桶、上下文等）。不含经纬度与凭据（AC-27.1）。</summary>
    [Column("payload_json", TypeName = "jsonb")]
    public string PayloadJson { get; set; } = "{}";

    [Column("received_at_utc")]
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 设备端按「本地日 + 原因」聚合的定位丢弃统计（REQ-9）：明细留在设备（进诊断导出包），
/// 统计上报服务端（AC-9.2）。
/// <para>
/// 语义是**快照覆盖**而不是增量累加：同一 (user, device, 本地日, 原因) 重复上报时更新计数，
/// 因此重传不会把计数翻倍（AC-5.2 的幂等要求同样适用于这条通道）。
/// </para>
/// </summary>
[Table("mobile_dropped_reason_daily")]
public sealed class MobileDroppedReasonDailyEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备本地日（yyyy-MM-dd）。</summary>
    [Column("local_date")]
    [MaxLength(10)]
    public string LocalDate { get; set; } = string.Empty;

    /// <summary>丢弃原因标识（设备端枚举名，展示文案由设备端提供）。</summary>
    [Column("reason")]
    [MaxLength(128)]
    public string Reason { get; set; } = string.Empty;

    [Column("count")]
    public int Count { get; set; }

    [Column("received_at_utc")]
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
