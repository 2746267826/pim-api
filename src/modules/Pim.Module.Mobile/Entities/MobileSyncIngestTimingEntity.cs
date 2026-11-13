using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Module.Mobile.Entities;

/// <summary>
/// 单次移动端入库尝试的耗时分解（#568）。
///
/// 为什么落表而不是只打日志：生产 API 日志按 ~1GB 滚动、只留最近一两天，
/// 而复盘一次排队事故需要按分布看「等连接 / 等咨询锁 / 计算 / 提交」各自占多少，
/// 靠捞日志聚合撑不到第二天。表故意保持窄，只放决策需要的段。
///
/// 写入必须 best-effort：失败只记日志，绝不影响入库结果（见 <c>MobileSyncIngestTimingWriter</c>）。
/// </summary>
[Table("mobile_sync_ingest_timings")]
public sealed class MobileSyncIngestTimingEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("batch_id")]
    [MaxLength(128)]
    public string BatchId { get; set; } = string.Empty;

    [Column("window_start_utc")]
    public DateTimeOffset WindowStartUtc { get; set; }

    [Column("window_end_utc")]
    public DateTimeOffset WindowEndUtc { get; set; }

    /// <summary>本次请求内的第几次处理尝试（与 envelope 的 AttemptCount 同源）。</summary>
    [Column("attempt")]
    public int Attempt { get; set; }

    /// <summary>completed / processing / deadline / cancelled / failed。</summary>
    [Column("outcome")]
    [MaxLength(32)]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>从进入入库到第一条数据库命令完成（含连接池等待）——连接池饱和会直接放大这一段。</summary>
    [Column("connection_wait_ms")]
    public long ConnectionWaitMs { get; set; }

    /// <summary>
    /// 等 <c>pg_advisory_xact_lock(user, device)</c> 的累计毫秒。同一台设备的入库是串行的，
    /// 这一段大就说明排队而不是计算慢。
    /// </summary>
    [Column("advisory_lock_wait_ms")]
    public long AdvisoryLockWaitMs { get; set; }

    [Column("session_rebuild_ms")]
    public long SessionRebuildMs { get; set; }

    [Column("materialize_ms")]
    public long MaterializeMs { get; set; }

    /// <summary>其余计算时间 = 总耗时 - 连接等待 - 提交。</summary>
    [Column("compute_ms")]
    public long ComputeMs { get; set; }

    [Column("commit_ms")]
    public long CommitMs { get; set; }

    [Column("total_ms")]
    public long TotalMs { get; set; }

    /// <summary>
    /// 慢请求时抓的 <c>pg_stat_activity</c> 快照（非 idle 的前若干个后端，≤500 字符）。
    /// 定位批次不取咨询锁也会一起卡住，说明瓶颈在上游；这一段用来回答"当时谁在占着"。
    /// </summary>
    [Column("blockers")]
    [MaxLength(500)]
    public string? Blockers { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
