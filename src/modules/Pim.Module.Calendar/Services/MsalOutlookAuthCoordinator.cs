using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public interface IOutlookAccessTokenProvider
{
    Task<string> AcquireAccessTokenAsync(Guid connectionId, bool forceRefresh, CancellationToken ct);
}

public sealed class MsalOutlookAuthCoordinator : IOutlookAccessTokenProvider
{
    private readonly PimDbContext _db;
    private readonly IMsalPublicClientAdapter _msal;
    private readonly OutlookConnectionLock _connectionLock;

    public MsalOutlookAuthCoordinator(
        PimDbContext db,
        IMsalPublicClientAdapter msal,
        OutlookConnectionLock connectionLock)
    {
        _db = db;
        _msal = msal;
        _connectionLock = connectionLock;
    }

    public async Task<string> AcquireAccessTokenAsync(
        Guid connectionId,
        bool forceRefresh,
        CancellationToken ct)
    {
        await using var held = await _connectionLock.AcquireAsync(connectionId, ct);
        var connection = await _db.Set<OutlookConnectionEntity>()
            .SingleAsync(item => item.Id == connectionId, ct);
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            throw new InvalidOperationException("Microsoft Client ID is not configured.");

        try
        {
            var result = await _msal.AcquireTokenSilentAsync(
                new OutlookAuthContext(
                    connection.Id,
                    connection.ClientId,
                    connection.Authority,
                    connection.HomeAccountId),
                forceRefresh,
                ct);
            connection.HomeAccountId = result.HomeAccountId;
            connection.AccountDisplayName = result.DisplayName;
            connection.AccountLoginHint = result.Username;
            connection.Status = "connected";
            connection.TokenHealth = "healthy";
            connection.LastError = null;
            connection.Version++;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return result.AccessToken;
        }
        catch (OutlookReauthenticationRequiredException)
        {
            connection.Status = "reauth-required";
            connection.TokenHealth = "interaction-required";
            connection.LastError = "Microsoft requires the account to be authorized again.";
            connection.Version++;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            throw;
        }
        catch (OutlookTokenCacheCorruptedException)
        {
            connection.Status = "reauth-required";
            connection.TokenHealth = "cache-corrupted";
            connection.LastError = "The local Microsoft token cache cannot be decrypted.";
            connection.Version++;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }
}
