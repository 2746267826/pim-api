using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookTokenCacheStore
{
    private readonly PimDbContext _db;
    private readonly ISecretProtector _protector;

    public OutlookTokenCacheStore(PimDbContext db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<byte[]?> LoadAsync(Guid connectionId, CancellationToken ct)
    {
        var encrypted = await _db.Set<OutlookConnectionEntity>()
            .Where(connection => connection.Id == connectionId)
            .Select(connection => connection.MsalCacheEncrypted)
            .SingleAsync(ct);
        if (encrypted is not { Length: > 0 }) return null;

        try
        {
            var protectedText = Encoding.UTF8.GetString(encrypted);
            return Convert.FromBase64String(_protector.Unprotect(protectedText));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new OutlookTokenCacheCorruptedException(exception);
        }
    }

    public async Task SaveAsync(Guid connectionId, byte[] cacheBlob, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .SingleAsync(item => item.Id == connectionId, ct);
        var protectedText = _protector.Protect(Convert.ToBase64String(cacheBlob));
        connection.MsalCacheEncrypted = Encoding.UTF8.GetBytes(protectedText);
        connection.Version++;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .SingleAsync(item => item.Id == connectionId, ct);
        connection.MsalCacheEncrypted = null;
        connection.HomeAccountId = null;
        connection.Version++;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

public sealed class OutlookTokenCacheCorruptedException(Exception innerException)
    : Exception("The encrypted MSAL token cache cannot be read.", innerException);
