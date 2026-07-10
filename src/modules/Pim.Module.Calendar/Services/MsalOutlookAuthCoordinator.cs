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
    private const string ReauthenticationMessage = "Microsoft requires the account to be authorized again.";
    private const string CacheCorruptedMessage = "The local Microsoft token cache cannot be decrypted.";

    private readonly PimDbContext _db;
    private readonly IMsalPublicClientAdapter _msal;
    private readonly OutlookTokenCacheLock _tokenCacheLock;

    public MsalOutlookAuthCoordinator(
        PimDbContext db,
        IMsalPublicClientAdapter msal,
        OutlookTokenCacheLock tokenCacheLock)
    {
        _db = db;
        _msal = msal;
        _tokenCacheLock = tokenCacheLock;
    }

    public async Task<string> AcquireAccessTokenAsync(
        Guid connectionId,
        bool forceRefresh,
        CancellationToken ct)
    {
        await using var held = await _tokenCacheLock.AcquireAsync(connectionId, ct);
        var connection = await _db.Set<OutlookConnectionEntity>()
            .SingleAsync(item => item.Id == connectionId, ct);
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            throw new InvalidOperationException("Microsoft Client ID is not configured.");

        var anchor = connection.HomeAccountId;
        var context = new OutlookAuthContext(
            connection.Id,
            connection.ClientId,
            connection.Authority,
            anchor);

        try
        {
            var result = await AcquireWithCacheRetryAsync(context, forceRefresh, ct);
            await _db.Entry(connection).ReloadAsync(ct);
            if (!string.Equals(result.HomeAccountId, anchor, StringComparison.Ordinal)
                || !string.Equals(connection.HomeAccountId, anchor, StringComparison.Ordinal))
            {
                throw new OutlookReauthenticationRequiredException("account-changed");
            }

            await MarkConnectedAsync(connection, result, ct);
            return result.AccessToken;
        }
        catch (OutlookReauthenticationRequiredException)
        {
            await _db.Entry(connection).ReloadAsync(ct);
            await MarkFailureAsync(
                connection,
                "interaction-required",
                ReauthenticationMessage,
                ct);
            throw;
        }
        catch (OutlookTokenCacheCorruptedException)
        {
            await _db.Entry(connection).ReloadAsync(ct);
            await MarkFailureAsync(
                connection,
                "cache-corrupted",
                CacheCorruptedMessage,
                ct);
            throw;
        }
    }

    private async Task<MsalAuthenticationResult> AcquireWithCacheRetryAsync(
        OutlookAuthContext context,
        bool forceRefresh,
        CancellationToken ct)
    {
        try
        {
            return await _msal.AcquireTokenSilentAsync(context, forceRefresh, ct);
        }
        catch (OutlookTokenCacheConcurrencyException)
        {
            return await _msal.AcquireTokenSilentAsync(context, forceRefresh, ct);
        }
    }

    private async Task MarkConnectedAsync(
        OutlookConnectionEntity connection,
        MsalAuthenticationResult result,
        CancellationToken ct)
    {
        var changed = connection.AccountDisplayName != result.DisplayName
            || connection.AccountLoginHint != result.Username
            || connection.Status != "connected"
            || connection.TokenHealth != "healthy"
            || connection.LastError is not null;
        if (!changed) return;

        connection.AccountDisplayName = result.DisplayName;
        connection.AccountLoginHint = result.Username;
        connection.Status = "connected";
        connection.TokenHealth = "healthy";
        connection.LastError = null;
        connection.Version++;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkFailureAsync(
        OutlookConnectionEntity connection,
        string tokenHealth,
        string lastError,
        CancellationToken ct)
    {
        var changed = connection.Status != "reauth-required"
            || connection.TokenHealth != tokenHealth
            || connection.LastError != lastError;
        if (!changed) return;

        connection.Status = "reauth-required";
        connection.TokenHealth = tokenHealth;
        connection.LastError = lastError;
        connection.Version++;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
