using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookMsalAuthenticationTests
{
    [Fact]
    public async Task CacheStore_EncryptsWholeMsalBlob()
    {
        await using var db = CreateDb();
        var connection = Connection();
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var store = new OutlookTokenCacheStore(db, new TestSecretProtector());

        await store.SaveAsync(connection.Id, [1, 2, 3, 4], CancellationToken.None);

        var raw = await db.Set<OutlookConnectionEntity>().AsNoTracking().SingleAsync();
        Assert.NotNull(raw.MsalCacheEncrypted);
        Assert.DoesNotContain<byte>([1, 2, 3, 4], raw.MsalCacheEncrypted!);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await store.LoadAsync(connection.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SilentAuth_MarksConnectionReauthRequiredWithoutExposingRefreshToken()
    {
        await using var db = CreateDb();
        var connection = Connection();
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var coordinator = new MsalOutlookAuthCoordinator(
            db,
            new FakeMsalClient { SilentException = new OutlookReauthenticationRequiredException("interaction_required") },
            new OutlookConnectionLock());

        await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None));

        var stored = await db.Set<OutlookConnectionEntity>().SingleAsync();
        Assert.Equal("reauth-required", stored.Status);
        Assert.Equal("interaction-required", stored.TokenHealth);
    }

    [Fact]
    public async Task SilentAuth_MarksConnectionReauthRequiredWhenCacheIsCorrupted()
    {
        await using var db = CreateDb();
        var connection = Connection();
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var coordinator = new MsalOutlookAuthCoordinator(
            db,
            new FakeMsalClient
            {
                SilentException = new OutlookTokenCacheCorruptedException(new FormatException())
            },
            new OutlookConnectionLock());

        await Assert.ThrowsAsync<OutlookTokenCacheCorruptedException>(() =>
            coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None));

        var stored = await db.Set<OutlookConnectionEntity>().SingleAsync();
        Assert.Equal("reauth-required", stored.Status);
        Assert.Equal("cache-corrupted", stored.TokenHealth);
    }

    [Fact]
    public async Task ConnectionLock_SerializesAcquisitionForSameConnection()
    {
        var connectionLock = new OutlookConnectionLock();
        var connectionId = Guid.NewGuid();
        var first = await connectionLock.AcquireAsync(connectionId, CancellationToken.None);

        var secondAcquisition = connectionLock.AcquireAsync(connectionId, CancellationToken.None).AsTask();

        Assert.False(secondAcquisition.IsCompleted);
        await first.DisposeAsync();
        await using var second = await secondAcquisition.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static OutlookConnectionEntity Connection() => new()
    {
        UserId = Guid.NewGuid(),
        ClientId = "11111111-1111-1111-1111-111111111111",
        TenantId = "common",
        Authority = "https://login.microsoftonline.com/common",
        HomeAccountId = "home-account",
        Status = "connected",
        TokenHealth = "healthy"
    };

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(OutlookConnectionEntity).Assembly);
        return new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-msal-{Guid.NewGuid()}")
            .Options);
    }
}

internal sealed class FakeMsalClient : IMsalPublicClientAdapter
{
    public Exception? SilentException { get; set; }
    public MsalAuthenticationResult Result { get; set; } = new(
        "access-token", "home-account", "user@example.com", "User", DateTimeOffset.UtcNow.AddHours(1),
        ["Calendars.ReadWrite", "User.Read"]);

    public Task<MsalAuthenticationResult> AcquireTokenSilentAsync(
        OutlookAuthContext context, bool forceRefresh, CancellationToken ct)
        => SilentException is null
            ? Task.FromResult(Result)
            : Task.FromException<MsalAuthenticationResult>(SilentException);

    public Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
        OutlookAuthContext context,
        Func<OutlookDeviceCodePrompt, Task> onPrompt,
        CancellationToken ct)
        => Task.FromResult(Result);
}

internal sealed class TestSecretProtector : ISecretProtector
{
    public string Protect(string value)
        => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public string Unprotect(string protectedValue)
        => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["protected:".Length..]));
}
