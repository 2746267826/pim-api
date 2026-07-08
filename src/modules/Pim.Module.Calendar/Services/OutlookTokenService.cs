using System.Text;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookTokenService
{
    private readonly PimDbContext _db;
    private readonly ISecretProtector _secretProtector;

    public OutlookTokenService(PimDbContext db, ISecretProtector secretProtector)
    {
        _db = db;
        _secretProtector = secretProtector;
    }

    public void StoreTokens(
        OutlookConnectionEntity connection,
        TokenResult token,
        DateTimeOffset now)
    {
        connection.AccessTokenEncrypted = Protect(token.AccessToken);
        connection.RefreshTokenEncrypted = Protect(token.RefreshToken);
        connection.AccessTokenExpiresAt = now.AddSeconds(Math.Max(0, token.ExpiresInSeconds));
        connection.Scopes = string.IsNullOrWhiteSpace(token.Scopes) ? connection.Scopes : token.Scopes;
        connection.Status = "connected";
        connection.TokenHealth = "healthy";
        connection.LastError = null;
        connection.UpdatedAt = now;
    }

    public async Task<string?> GetValidAccessTokenAsync(
        OutlookConnectionEntity connection,
        IMicrosoftGraphClient graph,
        CancellationToken ct)
    {
        if (connection.AccessTokenEncrypted.Length == 0)
        {
            connection.TokenHealth = "missing";
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (connection.AccessTokenExpiresAt is not null
            && connection.AccessTokenExpiresAt <= now)
        {
            connection.TokenHealth = "expired";
        }

        if (connection.AccessTokenExpiresAt is not null
            && connection.AccessTokenExpiresAt <= now.AddMinutes(5)
            && connection.RefreshTokenEncrypted is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(connection.ClientId))
        {
            try
            {
                var refreshed = await graph.RefreshAsync(
                    connection.TenantId,
                    connection.ClientId,
                    Unprotect(connection.RefreshTokenEncrypted),
                    connection.Scopes,
                    ct);
                StoreTokens(connection, refreshed, now);
                await _db.SaveChangesAsync(ct);
            }
            catch
            {
                connection.TokenHealth = "refresh-failed";
                await _db.SaveChangesAsync(ct);
                return null;
            }
        }

        return Unprotect(connection.AccessTokenEncrypted);
    }

    public void ClearTokens(OutlookConnectionEntity connection)
    {
        connection.AccessTokenEncrypted = [];
        connection.RefreshTokenEncrypted = null;
        connection.AccessTokenExpiresAt = null;
        connection.Status = "not-connected";
        connection.TokenHealth = "missing";
        connection.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public string Unprotect(byte[] protectedValue)
        => _secretProtector.Unprotect(Encoding.UTF8.GetString(protectedValue));

    private byte[] Protect(string value)
        => Encoding.UTF8.GetBytes(_secretProtector.Protect(value));
}
