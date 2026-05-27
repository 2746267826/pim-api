using Pim.Core.Ai;

namespace Pim.Infrastructure.Ai;

public sealed class DisabledAiGateway : IAiGateway
{
    public Task<AiGatewayResult> SendAsync(AiGatewayRequest request, CancellationToken ct = default)
        => Task.FromResult(new AiGatewayResult(
            "blocked",
            null,
            null,
            [],
            null,
            Guid.NewGuid(),
            "AI gateway is not configured.",
            request.Model));
}
