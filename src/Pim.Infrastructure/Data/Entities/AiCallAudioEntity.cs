using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("ai_call_audio")]
public sealed class AiCallAudioEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("log_id")] public Guid LogId { get; set; }
    [Column("name"), MaxLength(200)] public string Name { get; set; } = "";
    [Column("format"), MaxLength(16)] public string Format { get; set; } = "";
    [Column("size_bytes")] public long SizeBytes { get; set; }
    [Column("sha256"), MaxLength(64)] public string Sha256 { get; set; } = "";
    [Column("provider_id")] public Guid? ProviderId { get; set; }
    [Column("provider_key"), MaxLength(40)] public string? ProviderKey { get; set; }
    [Column("object_key")] public string? ObjectKey { get; set; }
}
