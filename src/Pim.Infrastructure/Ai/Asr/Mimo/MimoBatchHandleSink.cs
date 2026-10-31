namespace Pim.Infrastructure.Ai.Asr.Mimo;

/// <summary>
/// Caller-provided persistence boundary. The routing layer owns the durable store; the adapter
/// only hands over a verified or UNKNOWN create outcome with its immutable intent snapshot.
/// </summary>
public interface IMimoBatchHandleSink
{
    Task SaveAsync(MimoBatchCreateOutcome outcome, MimoBatchIntent intent, CancellationToken ct);
}
