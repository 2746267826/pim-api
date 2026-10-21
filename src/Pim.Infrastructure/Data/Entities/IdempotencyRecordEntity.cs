using System.ComponentModel.DataAnnotations;
using Pim.Core.Data;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

/// <summary>
/// 写操作幂等记录（契约语义见 <c>docs/client-contract-semantics.md</c> §2）。
/// <para>
/// 作用域是「用户 + 方法 + 路径模板 + key」：不同用户、不同端点之间互不影响。
/// 注意模板作用域的含义：同一 key 用在 <c>/tasks/{a}</c> 与 <c>/tasks/{b}</c> 上会命中**同一条**记录，
/// 因此客户端必须为每一次逻辑操作生成新 key，不能拿一个 key 去操作不同资源。
/// </para>
/// <para>
/// 首次请求成功后保存该次的成功响应（状态码 + 少量响应头 + 响应体），24 小时内同 key 重放返回缓存响应。
/// </para>
/// </summary>
[Table("idempotency_records")]
public sealed class IdempotencyRecordEntity : IUserOwnedEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("method")]
    [MaxLength(8)]
    public string Method { get; set; } = string.Empty;

    /// <summary>路径模板（例如 <c>/api/v1/calendar/tasks/{id}</c>），不是具体 URL：重试时 URL 里的 id 不同也能命中。</summary>
    [Column("route")]
    [MaxLength(512)]
    public string Route { get; set; } = string.Empty;

    [Column("idempotency_key")]
    [MaxLength(128)]
    public string Key { get; set; } = string.Empty;

    /// <summary><c>in_progress</c> / <c>completed</c>。</summary>
    [Column("state")]
    [MaxLength(16)]
    public string State { get; set; } = "in_progress";

    [Column("status_code")]
    public int? StatusCode { get; set; }

    [Column("content_type")]
    [MaxLength(128)]
    public string? ContentType { get; set; }

    /// <summary>缓存的响应体（超过 <c>IdempotencyMiddleware.MaxResponseBodyBytes</c> 时不缓存，为 null）。</summary>
    [Column("response_body")]
    public string? ResponseBody { get; set; }

    /// <summary>需要随重放一起返回的响应头（JSON 对象：Location / ETag / Last-Modified；无则为 null）。</summary>
    [Column("response_headers")]
    public string? ResponseHeaders { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(1);
}
