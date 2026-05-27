using Pim.Core.Ai;

namespace Pim.Infrastructure.Ai;

public sealed class DisabledAiGateway : IAiGateway
{
    public Task<AiResult> CompleteAsync(AiGatewayRequest request, CancellationToken ct = default)
        => Task.FromResult(new AiResult(
            AiRequestStatus.Blocked,
            null,
            null,
            [],
            new AiTokenUsage(null, null, null, null, null),
            Guid.NewGuid(),
            "AI gateway is not configured."));
}
