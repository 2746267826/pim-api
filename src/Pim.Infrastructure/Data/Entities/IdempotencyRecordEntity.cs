using System.ComponentModel.DataAnnotations;
using Pim.Core.Data;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

/// <summary>
/// 写操作幂等记录（契约语义见 <c>docs/client-contract-semantics.md</c> §2）。
/// <para>
/// 作用域是「用户 + 方法 + 路径模板 + key」：不同用户、不同端点之间互不影响。
/// 首次请求成功后保存该次的成功响应（状态码 + 响应体），24 小时内同 key 重放原样返回。
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

    /// <summary>路径模板（例如 <c>/api/v1/calendar/tasks/{id}</c>），不是具体 URL —— 同一客户端 key 不同 id 不得互相命中。</summary>
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

    [Column("response_body")]
    public string? ResponseBody { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(1);
}
