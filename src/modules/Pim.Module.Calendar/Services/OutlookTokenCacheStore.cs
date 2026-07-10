using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed record OutlookTokenCacheSnapshot(byte[]? Blob, long Version);

public sealed class OutlookTokenCacheStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretProtector _protector;

    public OutlookTokenCacheStore(IServiceScopeFactory scopeFactory, ISecretProtector protector)
    {
        _scopeFactory = scopeFactory;
        _protector = protector;
    }

    public async Task<OutlookTokenCacheSnapshot> LoadAsync(Guid connectionId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var stored = await db.Set<OutlookConnectionEntity>()
            .Where(connection => connection.Id == connectionId)
            .Select(connection => new { connection.MsalCacheEncrypted, connection.Version })
            .SingleAsync(ct);
        if (stored.MsalCacheEncrypted is not { Length: > 0 })
            return new OutlookTokenCacheSnapshot(null, stored.Version);

        try
        {
            var protectedText = Encoding.UTF8.GetString(stored.MsalCacheEncrypted);
            var blob = Convert.FromBase64String(_protector.Unprotect(protectedText));
            return new OutlookTokenCacheSnapshot(blob, stored.Version);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new OutlookTokenCacheCorruptedException();
        }
    }

    public async Task<OutlookTokenCacheSnapshot> SaveAsync(
        Guid connectionId,
        byte[] cacheBlob,
        long expectedVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cacheBlob);
        var protectedText = _protector.Protect(Convert.ToBase64String(cacheBlob));
        var encrypted = Encoding.UTF8.GetBytes(protectedText);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var connection = await db.Set<OutlookConnectionEntity>()
            .SingleAsync(item => item.Id == connectionId, ct);
        if (connection.Version != expectedVersion)
            throw new OutlookTokenCacheConcurrencyException();

        db.Entry(connection).Property(item => item.Version).OriginalValue = expectedVersion;
        connection.MsalCacheEncrypted = encrypted;
        connection.Version = checked(expectedVersion + 1);
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new OutlookTokenCacheConcurrencyException();
        }

        return new OutlookTokenCacheSnapshot(cacheBlob.ToArray(), connection.Version);
    }

    public async Task ClearAsync(Guid connectionId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var connection = await db.Set<OutlookConnectionEntity>()
            .SingleAsync(item => item.Id == connectionId, ct);
        if (connection.MsalCacheEncrypted is null && connection.HomeAccountId is null) return;

        connection.MsalCacheEncrypted = null;
        connection.HomeAccountId = null;
        connection.Version++;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class OutlookTokenCacheCorruptedException()
    : Exception("The encrypted MSAL token cache cannot be read.");

public sealed class OutlookTokenCacheConcurrencyException()
    : Exception("The Microsoft token cache changed during the operation.");
