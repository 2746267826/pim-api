using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("ai_call_images")]
public sealed class AiCallImageEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("log_id")] public Guid LogId { get; set; }
    [Column("image_index")] public int Index { get; set; }
    [Column("name"), MaxLength(200)] public string Name { get; set; } = "";
    [Column("content_type"), MaxLength(40)] public string ContentType { get; set; } = "";
    [Column("size_bytes")] public long SizeBytes { get; set; }
    [Column("sha256"), MaxLength(64)] public string Sha256 { get; set; } = "";
    [Column("provider_id")] public Guid? ProviderId { get; set; }
    [Column("provider_key"), MaxLength(40)] public string? ProviderKey { get; set; }
    [Column("object_key")] public string? ObjectKey { get; set; }
}
