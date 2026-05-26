using Pim.Core.Common;

namespace Pim.Core.Ai;

public interface IAiUsageService
{
    Task<AiStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<PagedResult<AiRequestLogListItemDto>> ListRequestsAsync(AiRequestLogFilter filter, CancellationToken ct = default);
    Task<AiRequestLogDetailDto?> GetRequestDetailAsync(Guid id, CancellationToken ct = default);
    Task<AiUsageSummaryDto> GetUsageSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default);
}
