using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Pim.Core.Data;

namespace Pim.Infrastructure.Data.Entities;

[Table("refresh_tokens")]
public class RefreshTokenEntity : IUserOwnedEntity
{
    [Key][Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("user_id")] public Guid UserId { get; set; }
    [Column("token_hash")][MaxLength(255)] public string TokenHash { get; set; } = string.Empty;
    [Column("expires_at")] public DateTimeOffset ExpiresAt { get; set; }
    [Column("revoked_at")] public DateTimeOffset? RevokedAt { get; set; }
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(UserId))]
    public UserEntity User { get; set; } = null!;
}
