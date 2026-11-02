using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Pim.Infrastructure.Data.Entities;

/// <summary>
/// Durable browser transport evidence. This table is deliberately independent from terminal job
/// and audit state: a gateway 504 observed while polling cannot rewrite a provider result.
/// </summary>
[Table("ai_asr_transport_observations")]
public sealed class AiAsrTransportObservationEntity
{
    [Key, Column("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [Column("owner_id")] public Guid OwnerId { get; set; }
    [Column("log_id")] public Guid LogId { get; set; }
    [Column("client_request_id")] public Guid ClientRequestId { get; set; }
    [Column("transport_kind"), MaxLength(32)] public string TransportKind { get; set; } = "";
    [Column("source"), MaxLength(64)] public string Source { get; set; } = "";
    [Column("gateway_status")] public int? GatewayStatus { get; set; }
    [Column("gateway_name"), MaxLength(200)] public string? GatewayName { get; set; }
    [Column("observed_at")] public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}
