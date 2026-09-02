using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Module.PcTracker.Entities;

/// <summary>
/// 域名级每日聚合（来自 Time Tracker fork 通道 + 历史导入）。
/// focus/run/media 单位毫秒；time 语义为访问次数（与 tt4b 的 Row.time 一致）。
/// </summary>
[Table("pc_browser_site_daily")]
public class PcBrowserSiteDailyEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("device_id")][MaxLength(64)] public string DeviceId { get; set; } = string.Empty;
    [Column("date")][MaxLength(10)] public string Date { get; set; } = string.Empty;
    [Column("host")][MaxLength(253)] public string Host { get; set; } = string.Empty;
    [Column("focus_ms")] public long FocusMs { get; set; }
    [Column("visit_count")] public long VisitCount { get; set; }
    [Column("run_ms")] public long RunMs { get; set; }
    [Column("media_ms")] public long MediaMs { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>域名级时间段（timeline Tick），用于按日还原浏览时段分布。</summary>
[Table("pc_browser_site_tick")]
public class PcBrowserSiteTickEntity
{
    [Key][Column("id")] public long Id { get; set; }
    [Column("device_id")][MaxLength(64)] public string DeviceId { get; set; } = string.Empty;
    [Column("host")][MaxLength(253)] public string Host { get; set; } = string.Empty;
    /// <summary>本机本地日期（yyyy-MM-dd），由守护进程推导，按日查询与清理都靠它。</summary>
    [Column("date")][MaxLength(10)] public string Date { get; set; } = string.Empty;
    [Column("start_utc")] public DateTimeOffset StartUtc { get; set; }
    [Column("duration_ms")] public long DurationMs { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>站点元数据（别名/图标/分类），用户级，与设备无关。</summary>
[Table("pc_browser_site_meta")]
public class PcBrowserSiteMetaEntity
{
    [Key][Column("host")][MaxLength(253)] public string Host { get; set; } = string.Empty;
    [Column("alias")][MaxLength(256)] public string? Alias { get; set; }
    [Column("icon_url")][MaxLength(512)] public string? IconUrl { get; set; }
    [Column("cate")][MaxLength(128)] public string? Cate { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
