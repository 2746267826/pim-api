using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

[Table("ai_model_profiles")]
public sealed class AiModelProfileEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("name"), MaxLength(120)] public string Name { get; set; } = "";
    [Column("normalized_name"), MaxLength(120)] public string NormalizedName { get; set; } = "";
    [Column("base_url"), MaxLength(2048)] public string BaseUrl { get; set; } = "";
    [Column("model"), MaxLength(128)] public string Model { get; set; } = "";
    [Column("enabled")] public bool Enabled { get; set; }
    [Column("capabilities")] public int Capabilities { get; set; }
    [Column("asr_preset_id"), MaxLength(64)] public string? AsrPresetId { get; set; }
    [Column("api_key_secret")] public string ApiKeySecret { get; set; } = "";
    [Column("timeout_seconds")] public int TimeoutSeconds { get; set; } = 60;
    [Column("max_output_tokens")] public int MaxOutputTokens { get; set; } = 1024;
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [Column("updated_at")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("ai_model_runtime")]
public sealed class AiModelRuntimeEntity
{
    public const string SingletonId = "sdk";
    [Key, Column("id"), MaxLength(16)] public string Id { get; set; } = SingletonId;
    [Column("revision")] public Guid Revision { get; set; } = Guid.NewGuid();
    [Column("enabled")] public bool Enabled { get; set; }
    [Column("text_model_id")] public Guid? TextModelId { get; set; }
    [Column("vision_model_id")] public Guid? VisionModelId { get; set; }
    [Column("transcription_model_id")] public Guid? TranscriptionModelId { get; set; }
}
