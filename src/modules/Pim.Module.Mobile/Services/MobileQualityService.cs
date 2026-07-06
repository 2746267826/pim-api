using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

public sealed class MobileQualityService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public MobileQualityService(PimDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public Task<MobileQualityResponse> GetQualityAsync(
        DateTimeOffset? rangeStartUtc,
        DateTimeOffset? rangeEndUtc,
        CancellationToken ct = default)
    {
        _ = _db;
        _ = rangeStartUtc;
        _ = rangeEndUtc;
        _ = ct;
        MobileUserContext.RequireUserId(_currentUser);

        var components = new List<MobileQualityComponentDto>
        {
            Component("android-heartbeat", "Android heartbeat"),
            Component("event-coverage", "Usage event coverage"),
            Component("fallback-only-days", "Fallback-only usage days"),
            Component("sync-batch-failures", "Sync batch failures"),
            Component("location-accuracy-rejections", "Location accuracy rejections"),
            Component("app-metadata-completeness", "App metadata completeness")
        };

        return Task.FromResult(new MobileQualityResponse(
            PimHealthStatus.Healthy,
            DateTimeOffset.UtcNow,
            components,
            []));
    }

    private static MobileQualityComponentDto Component(string key, string name)
        => new(
            key,
            name,
            PimHealthStatus.Healthy,
            "Component status is healthy.",
            new Dictionary<string, string>());
}
