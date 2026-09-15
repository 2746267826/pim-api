using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Module.Mobile.Entities;

/// <summary>
/// 派生分析数据的物化覆盖记录（#247）。
///
/// <c>mobile_usage_aggregates</c> / <c>mobile_timeline_blocks</c> 只有在"某个窗口被完整物化过"
/// 之后才可以被端点直接读取，否则会把"还没算过的区间"当成"没有使用记录"。
/// 这里按窗口记录覆盖范围：一行 = 一次物化（窗口按本地日对齐）。
/// </summary>
[Table("mobile_analytics_materializations")]
public sealed class MobileAnalyticsMaterializationEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("timezone")]
    [MaxLength(64)]
    public string Timezone { get; set; } = string.Empty;

    [Column("covered_from_utc")]
    public DateTimeOffset CoveredFromUtc { get; set; }

    [Column("covered_to_utc")]
    public DateTimeOffset CoveredToUtc { get; set; }

    [Column("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
