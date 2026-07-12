using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookMsalAuthenticationTests
{
    [Fact]
    public void AuthenticationResult_ToString_DoesNotExposeAccessToken()
    {
        const string accessToken = "secret-access-token";
        var result = AuthenticationResult(accessToken: accessToken);

        Assert.DoesNotContain(accessToken, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceCodePrompt_ToString_DoesNotExposeUserCode()
    {
        const string userCode = "ABCD-EFGH";
        var prompt = new OutlookDeviceCodePrompt(
            userCode,
            "https://microsoft.com/devicelogin",
            DateTimeOffset.UtcNow.AddMinutes(15),
            $"Enter code {userCode}");

        Assert.DoesNotContain(userCode, prompt.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SilentAdapter_RejectsMissingHomeAccountIdWithoutBuildingClient()
    {
        await using var services = CreateServices();
        var store = CreateStore(services);
        var clientWasBuilt = false;
        var adapter = new MsalPublicClientAdapter(store, _ =>
        {
            clientWasBuilt = true;
            return new FakeMsalClientApplication();
        });

        var exception = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            adapter.AcquireTokenSilentAsync(AuthContext(homeAccountId: null), false, CancellationToken.None));

        Assert.Equal("account-anchor-missing", exception.Code);
        Assert.False(clientWasBuilt);
    }

    [Fact]
    public async Task SilentAdapter_DoesNotFallbackWhenExactHomeAccountIdIsMissing()
    {
        await using var services = CreateServices();
        var connection = Connection();
        await SeedAsync(services, connection);
        var application = new FakeMsalClientApplication { HasExactAccount = false };
        var adapter = new MsalPublicClientAdapter(CreateStore(services), _ => application);

        var exception = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            adapter.AcquireTokenSilentAsync(AuthContext(connection.Id, "anchored-account"), false, CancellationToken.None));

        Assert.Equal("account-missing", exception.Code);
        Assert.Equal("anchored-account", application.RequestedHomeAccountId);
        Assert.Equal(0, application.SilentAcquisitionCount);
    }

    [Fact]
    public async Task SilentAdapter_MapsDecryptedInvalidMsalJsonToCacheCorrupted()
    {
        await using var services = CreateServices();
        var connection = Connection();
        await SeedAsync(services, connection);
        var store = CreateStore(services);
        var snapshot = await store.LoadAsync(connection.Id, CancellationToken.None);
        await store.SaveAsync(
            connection.Id,
            Encoding.UTF8.GetBytes("{not-valid-msal-json"),
            snapshot.Version,
            CancellationToken.None);
        var adapter = new MsalPublicClientAdapter(
            store,
            _ => new FakeMsalClientApplication { HasExactAccount = true });

        var exception = await Assert.ThrowsAsync<OutlookTokenCacheCorruptedException>(() =>
            adapter.AcquireTokenSilentAsync(AuthContext(connection.Id), false, CancellationToken.None));

        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task SilentAdapter_UsesCallbackCancellationTokenForCacheLoad()
    {
        await using var services = CreateServices();
        var connection = Connection();
        await SeedAsync(services, connection);
        var adapter = new MsalPublicClientAdapter(
            CreateStore(services),
            _ => new FakeMsalClientApplication { HasExactAccount = true });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.AcquireTokenSilentAsync(AuthContext(connection.Id), false, cancellation.Token));
    }

    [Fact]
    public async Task CacheStore_EncryptsWholeMsalBlob()
    {
        await using var services = CreateServices();
        var connection = Connection();
        await SeedAsync(services, connection);
        var store = CreateStore(services);
        var snapshot = await store.LoadAsync(connection.Id, CancellationToken.None);

        await store.SaveAsync(connection.Id, [1, 2, 3, 4], snapshot.Version, CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var raw = await db.Set<OutlookConnectionEntity>().AsNoTracking().SingleAsync();
        Assert.NotNull(raw.MsalCacheEncrypted);
        Assert.DoesNotContain<byte>([1, 2, 3, 4], raw.MsalCacheEncrypted!);
        var loaded = await store.LoadAsync(connection.Id, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, loaded.Blob);
    }

    [Fact]
    public async Task CacheStore_SaveDoesNotCommitCallingScopesTrackedChanges()
    {
        await using var services = CreateServices();
        var connection = Connection();
        await SeedAsync(services, connection);
        var store = CreateStore(services);
        await using var callingScope = services.CreateAsyncScope();
        var callingDb = callingScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var tracked = await callingDb.Set<OutlookConnectionEntity>().SingleAsync();
        tracked.Status = "calling-scope-change";
        var snapshot = await store.LoadAsync(connection.Id, CancellationToken.None);

        await store.SaveAsync(connection.Id, [5, 6, 7], snapshot.Version, CancellationToken.None);

        await using var verificationScope = services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var stored = await verificationDb.Set<OutlookConnectionEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("connected", stored.Status);
        Assert.NotNull(stored.MsalCacheEncrypted);
    }

    [Fact]
    public async Task CacheStore_RejectsSecondSaveFromSameVersionSnapshot()
    {
        await using var services = CreateServices();
        var connection = Connection();
        await SeedAsync(services, connection);
        var store = CreateStore(services);
        var first = await store.LoadAsync(connection.Id, CancellationToken.None);
        var second = await store.LoadAsync(connection.Id, CancellationToken.None);

        await store.SaveAsync(connection.Id, [1], first.Version, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<OutlookTokenCacheConcurrencyException>(() =>
            store.SaveAsync(connection.Id, [2], second.Version, CancellationToken.None));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("1", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SilentAuth_RejectsChangedResultAccountId()
    {
        await using var db = CreateDb();
        var connection = Connection();
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var coordinator = new MsalOutlookAuthCoordinator(
            db,
            new FakeMsalClient { Result = AuthenticationResult(homeAccountId: "different-account") },
            new OutlookTokenCacheLock());

        var exception = await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None));

        Assert.Equal("account-changed", exception.Code);
        var stored = await db.Set<OutlookConnectionEntity>().AsNoTracking().SingleAsync();
        Assert.Equal("home-account", stored.HomeAccountId);
        Assert.Equal("reauth-required", stored.Status);
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
            new OutlookTokenCacheLock());

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
            new FakeMsalClient { SilentException = new OutlookTokenCacheCorruptedException() },
            new OutlookTokenCacheLock());

        await Assert.ThrowsAsync<OutlookTokenCacheCorruptedException>(() =>
            coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None));

        var stored = await db.Set<OutlookConnectionEntity>().SingleAsync();
        Assert.Equal("reauth-required", stored.Status);
        Assert.Equal("cache-corrupted", stored.TokenHealth);
    }

    [Fact]
    public Task SilentAuth_ReauthenticationFailure_DoesNotOverwriteNewerAccountState()
        => AssertNewerAccountStateIsPreservedAsync(
            new OutlookReauthenticationRequiredException("interaction_required"));

    [Fact]
    public Task SilentAuth_CacheCorruption_DoesNotOverwriteNewerAccountState()
        => AssertNewerAccountStateIsPreservedAsync(new OutlookTokenCacheCorruptedException());

    [Fact]
    public async Task SilentAuth_DoesNotWriteWhenConnectionStateAlreadyMatches()
    {
        await using var db = CreateDb();
        var connection = Connection();
        connection.AccountDisplayName = "User";
        connection.AccountLoginHint = "user@example.com";
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var initialVersion = connection.Version;
        var coordinator = new MsalOutlookAuthCoordinator(
            db,
            new FakeMsalClient(),
            new OutlookTokenCacheLock());

        await coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None);

        var stored = await db.Set<OutlookConnectionEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(initialVersion, stored.Version);
    }

    [Fact]
    public async Task SilentAuth_DoesNotRewriteIdenticalReauthState()
    {
        await using var db = CreateDb();
        var connection = Connection();
        connection.Status = "reauth-required";
        connection.TokenHealth = "interaction-required";
        connection.LastError = "Microsoft requires the account to be authorized again.";
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var initialVersion = connection.Version;
        var coordinator = new MsalOutlookAuthCoordinator(
            db,
            new FakeMsalClient { SilentException = new OutlookReauthenticationRequiredException("interaction_required") },
            new OutlookTokenCacheLock());

        await Assert.ThrowsAsync<OutlookReauthenticationRequiredException>(() =>
            coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None));

        var stored = await db.Set<OutlookConnectionEntity>().AsNoTracking().SingleAsync();
        Assert.Equal(initialVersion, stored.Version);
    }

    [Fact]
    public async Task SilentAuth_RetriesCacheConcurrencyConflictOnce()
    {
        await using var db = CreateDb();
        var connection = Connection();
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var client = new FakeMsalClient { CacheConcurrencyFailuresRemaining = 1 };
        var coordinator = new MsalOutlookAuthCoordinator(db, client, new OutlookTokenCacheLock());

        var token = await coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None);

        Assert.Equal("access-token", token);
        Assert.Equal(2, client.SilentAcquisitionCount);
    }

    [Fact]
    public async Task SilentAuth_StopsAfterOneCacheConcurrencyRetry()
    {
        await using var db = CreateDb();
        var connection = Connection();
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
        var client = new FakeMsalClient { CacheConcurrencyFailuresRemaining = 2 };
        var coordinator = new MsalOutlookAuthCoordinator(db, client, new OutlookTokenCacheLock());

        await Assert.ThrowsAsync<OutlookTokenCacheConcurrencyException>(() =>
            coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None));

        Assert.Equal(2, client.SilentAcquisitionCount);
    }

    [Fact]
    public async Task TokenCacheLock_SerializesAcquisitionForSameConnection()
    {
        var connectionLock = new OutlookTokenCacheLock();
        var connectionId = Guid.NewGuid();
        var first = await connectionLock.AcquireAsync(connectionId, CancellationToken.None);

        var secondAcquisition = connectionLock.AcquireAsync(connectionId, CancellationToken.None).AsTask();

        Assert.False(secondAcquisition.IsCompleted);
        await first.DisposeAsync();
        await using var second = await secondAcquisition.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static OutlookAuthContext AuthContext(
        Guid? connectionId = null,
        string? homeAccountId = "home-account")
        => new(
            connectionId ?? Guid.NewGuid(),
            "11111111-1111-1111-1111-111111111111",
            "https://login.microsoftonline.com/common",
            homeAccountId);

    private static MsalAuthenticationResult AuthenticationResult(
        string accessToken = "access-token",
        string homeAccountId = "home-account")
        => new(
            accessToken,
            homeAccountId,
            "user@example.com",
            "User",
            DateTimeOffset.UtcNow.AddHours(1),
            ["Calendars.ReadWrite", "User.Read"]);

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

    private static ServiceProvider CreateServices()
    {
        PimDbContext.RegisterModuleAssembly(typeof(OutlookConnectionEntity).Assembly);
        var services = new ServiceCollection();
        var databaseName = $"outlook-msal-store-{Guid.NewGuid()}";
        services.AddDbContext<PimDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static OutlookTokenCacheStore CreateStore(ServiceProvider services)
        => new(services.GetRequiredService<IServiceScopeFactory>(), new TestSecretProtector());

    private static async Task SeedAsync(ServiceProvider services, OutlookConnectionEntity connection)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        db.Set<OutlookConnectionEntity>().Add(connection);
        await db.SaveChangesAsync();
    }

    private static async Task AssertNewerAccountStateIsPreservedAsync(Exception authenticationException)
    {
        await using var services = CreateServices();
        var connection = Connection();
        await SeedAsync(services, connection);
        await using var coordinatorScope = services.CreateAsyncScope();
        var coordinatorDb = coordinatorScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var client = new FakeMsalClient
        {
            SilentException = authenticationException,
            BeforeSilentFailure = async (_, ct) =>
            {
                await using var updateScope = services.CreateAsyncScope();
                var updateDb = updateScope.ServiceProvider.GetRequiredService<PimDbContext>();
                var newer = await updateDb.Set<OutlookConnectionEntity>()
                    .SingleAsync(item => item.Id == connection.Id, ct);
                newer.HomeAccountId = "new-home-account";
                newer.AccountDisplayName = "New Account";
                newer.AccountLoginHint = "new-account@example.com";
                newer.Status = "connected";
                newer.TokenHealth = "healthy";
                newer.LastError = null;
                newer.Version++;
                await updateDb.SaveChangesAsync(ct);
            }
        };
        var coordinator = new MsalOutlookAuthCoordinator(
            coordinatorDb,
            client,
            new OutlookTokenCacheLock());

        var thrown = await Record.ExceptionAsync(() =>
            coordinator.AcquireAccessTokenAsync(connection.Id, false, CancellationToken.None));

        Assert.Same(authenticationException, thrown);
        await using var verificationScope = services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var stored = await verificationDb.Set<OutlookConnectionEntity>()
            .AsNoTracking()
            .SingleAsync(item => item.Id == connection.Id);
        Assert.Equal("new-home-account", stored.HomeAccountId);
        Assert.Equal("New Account", stored.AccountDisplayName);
        Assert.Equal("new-account@example.com", stored.AccountLoginHint);
        Assert.Equal("connected", stored.Status);
        Assert.Equal("healthy", stored.TokenHealth);
        Assert.Null(stored.LastError);
        Assert.Equal(connection.Version + 1, stored.Version);
    }
}

internal class FakeMsalClient : IMsalPublicClientAdapter
{
    public Exception? SilentException { get; set; }
    public Func<OutlookAuthContext, CancellationToken, Task>? BeforeSilentFailure { get; set; }
    public int CacheConcurrencyFailuresRemaining { get; set; }
    public int SilentAcquisitionCount { get; private set; }
    public MsalAuthenticationResult Result { get; set; } = new(
        "access-token", "home-account", "user@example.com", "User", DateTimeOffset.UtcNow.AddHours(1),
        ["Calendars.ReadWrite", "User.Read"]);

    public virtual async Task<MsalAuthenticationResult> AcquireTokenSilentAsync(
        OutlookAuthContext context, bool forceRefresh, CancellationToken ct)
    {
        SilentAcquisitionCount++;
        if (CacheConcurrencyFailuresRemaining > 0)
        {
            CacheConcurrencyFailuresRemaining--;
            throw new OutlookTokenCacheConcurrencyException();
        }

        if (BeforeSilentFailure is not null) await BeforeSilentFailure(context, ct);
        if (SilentException is not null) throw SilentException;
        return Result;
    }

    public virtual Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
        OutlookAuthContext context,
        Func<OutlookDeviceCodePrompt, Task> onPrompt,
        CancellationToken ct)
        => Task.FromResult(Result);
}

internal sealed class FakeMsalClientApplication : IMsalClientApplication
{
    private readonly ITokenCacheSerializer _serializer = (ITokenCacheSerializer)PublicClientApplicationBuilder
        .Create("11111111-1111-1111-1111-111111111111")
        .Build()
        .UserTokenCache;
    private Func<ITokenCacheSerializer, CancellationToken, Task>? _beforeAccess;
    private Func<ITokenCacheSerializer, bool, CancellationToken, Task>? _afterAccess;

    public bool HasExactAccount { get; set; }
    public string? RequestedHomeAccountId { get; private set; }
    public int SilentAcquisitionCount { get; private set; }
    public MsalAuthenticationResult Result { get; set; } = new(
        "access-token", "home-account", "user@example.com", "User", DateTimeOffset.UtcNow.AddHours(1),
        ["Calendars.ReadWrite", "User.Read"]);

    public void BindCache(
        Func<ITokenCacheSerializer, CancellationToken, Task> beforeAccess,
        Func<ITokenCacheSerializer, bool, CancellationToken, Task> afterAccess)
    {
        _beforeAccess = beforeAccess;
        _afterAccess = afterAccess;
    }

    public async Task<bool> TrySelectAccountAsync(string homeAccountId, CancellationToken ct)
    {
        RequestedHomeAccountId = homeAccountId;
        if (_beforeAccess is not null) await _beforeAccess(_serializer, ct);
        return HasExactAccount;
    }

    public async Task<MsalAuthenticationResult> AcquireTokenSilentAsync(bool forceRefresh, CancellationToken ct)
    {
        SilentAcquisitionCount++;
        if (_afterAccess is not null) await _afterAccess(_serializer, false, ct);
        return Result;
    }

    public Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
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
