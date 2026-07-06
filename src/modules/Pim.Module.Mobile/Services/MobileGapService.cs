using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

public sealed class MobileGapService
{
    private static readonly TimeSpan MaxBackfillAge = TimeSpan.FromDays(14);
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;

    public MobileGapService(PimDbContext db, ICurrentUserService currentUser, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
    }

    public Task<MobileGapResponse> GetGapsAsync(MobileGapRequest request, CancellationToken ct = default)
    {
        _ = _db;
        _ = ct;
        MobileUserContext.RequireUserId(_currentUser);

        var now = _timeProvider.GetUtcNow();
        var maxBackfillStart = now - MaxBackfillAge;
        var start = request.RangeStartUtc < maxBackfillStart ? maxBackfillStart : request.RangeStartUtc;
        var end = request.RangeEndUtc > now ? now : request.RangeEndUtc;
        if (end < start)
            end = start;

        var windows = new List<MobileGapWindowDto>();
        var cursor = start;
        do
        {
            var windowEnd = cursor.AddDays(1);
            if (windowEnd > end)
                windowEnd = end;

            windows.Add(new MobileGapWindowDto(
                cursor,
                windowEnd,
                "client-backfill",
                request.CapabilitiesJson));

            cursor = windowEnd;
        }
        while (cursor < end);

        return Task.FromResult(new MobileGapResponse(maxBackfillStart, windows));
    }
}
