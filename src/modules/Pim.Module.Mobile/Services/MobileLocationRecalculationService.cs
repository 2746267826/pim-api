using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

public sealed record MobileLocationRecalculationScope(
    string DeviceId,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc);

public interface IMobileLocationRecalculationService
{
    /// <summary>
    /// 同步重算受影响窗口的轨迹/分段与总览。只有全部计算完成后才返回成功时间；
    /// 抛出的异常由 API 按真实失败返回 5xx，不会包装成“已完成”。
    /// </summary>
    Task<DateTimeOffset> RecalculateAsync(
        IReadOnlyCollection<MobileLocationRecalculationScope> scopes,
        CancellationToken ct = default);
}

public sealed class MobileLocationRecalculationService : IMobileLocationRecalculationService
{
    private readonly MobileLocationAggregationService _aggregationService;
    private readonly MobileFrequentPlaceService _frequentPlaceService;
    private readonly TimeProvider _timeProvider;

    public MobileLocationRecalculationService(
        MobileLocationAggregationService aggregationService,
        MobileFrequentPlaceService frequentPlaceService,
        TimeProvider timeProvider)
    {
        _aggregationService = aggregationService;
        _frequentPlaceService = frequentPlaceService;
        _timeProvider = timeProvider;
    }

    public async Task<DateTimeOffset> RecalculateAsync(
        IReadOnlyCollection<MobileLocationRecalculationScope> scopes,
        CancellationToken ct = default)
    {
        foreach (var scope in NormalizeScopes(scopes))
        {
            var request = new MobileLocationQueryRequest(
                scope.RangeStartUtc,
                scope.RangeEndUtc,
                MobileAnalyticsDefaults.DefaultTimezone,
                scope.DeviceId);
            // 轨迹、分段、总览和常去地点都直接读取原始点；显式执行它们，
            // 失败就不会返回“已重算”的成功回执。
            await _aggregationService.GetOverviewAsync(request, ct);
            await _aggregationService.GetTracksAsync(request, ct);
            await _frequentPlaceService.GetFrequentPlacesAsync(request, ct);
        }

        return _timeProvider.GetUtcNow();
    }

    private static IReadOnlyList<MobileLocationRecalculationScope> NormalizeScopes(
        IReadOnlyCollection<MobileLocationRecalculationScope> scopes)
        => scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope.DeviceId)
                && scope.RangeEndUtc > scope.RangeStartUtc)
            .GroupBy(scope => scope.DeviceId.Trim(), StringComparer.Ordinal)
            .Select(group => new MobileLocationRecalculationScope(
                group.Key,
                group.Min(scope => scope.RangeStartUtc),
                group.Max(scope => scope.RangeEndUtc)))
            .ToList();
}
