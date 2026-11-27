using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pim.Core.Data;

namespace Pim.Module.Mobile.Entities;

/// <summary>
/// 定位点轻量修订记录（P05）。只记录人工纠正/排除/恢复/删除/补点，不引入笔记式整篇版本系统。
/// </summary>
[Table("mobile_location_point_revisions")]
public sealed class MobileLocationPointRevisionEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("point_id")]
    public Guid PointId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("operation")]
    [MaxLength(32)]
    public string Operation { get; set; } = string.Empty;

    [Column("revision_before")]
    public int RevisionBefore { get; set; }

    [Column("revision_after")]
    public int RevisionAfter { get; set; }

    [Column("original_latitude")]
    public decimal? OriginalLatitude { get; set; }

    [Column("original_longitude")]
    public decimal? OriginalLongitude { get; set; }

    [Column("new_latitude")]
    public decimal? NewLatitude { get; set; }

    [Column("new_longitude")]
    public decimal? NewLongitude { get; set; }

    [Column("excluded_before")]
    public bool? ExcludedBefore { get; set; }

    [Column("excluded_after")]
    public bool? ExcludedAfter { get; set; }

    [Column("reason")]
    [MaxLength(500)]
    public string? Reason { get; set; }

    [Column("actor_user_id")]
    public Guid ActorUserId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 用户保存的稳定地点名称。常去地点聚类本身没有稳定 ID，因此按中心点距离匹配本表。
/// </summary>
[Table("mobile_saved_places")]
public sealed class MobileSavedPlaceEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("place_type")]
    [MaxLength(32)]
    public string PlaceType { get; set; } = "other";

    [Column("center_latitude")]
    public decimal CenterLatitude { get; set; }

    [Column("center_longitude")]
    public decimal CenterLongitude { get; set; }

    [Column("radius_meters")]
    public decimal RadiusMeters { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("revision")]
    public int Revision { get; set; } = 1;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 相邻轨迹分段的人工合并覆盖。轨迹仍由定位点实时计算，本表只记录“在这个边界两侧按同一种段类型合并”。
/// </summary>
[Table("mobile_location_segment_merges")]
public sealed class MobileLocationSegmentMergeEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("device_id")]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Column("before_point_id")]
    public Guid BeforePointId { get; set; }

    [Column("after_point_id")]
    public Guid AfterPointId { get; set; }

    [Column("kind")]
    [MaxLength(16)]
    public string Kind { get; set; } = "move";

    [Column("actor_user_id")]
    public Guid ActorUserId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
