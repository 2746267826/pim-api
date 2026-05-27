namespace Pim.Core.Ai;

public interface IAiGateway
{
    Task<AiResult> CompleteAsync(AiGatewayRequest request, CancellationToken ct = default);
}
