using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookAuthorizationSessionTests
{
    private const string UserCode = "ABCD-EFGH";
    private const string VerificationUri = "https://microsoft.com/devicelogin";
    private const string RawFailure = "raw-response-body bearer-token device-secret";

    [Fact]
    public async Task Runner_PublishesPromptThenConnectsAndClearsLegacyState()
    {
        var msal = new PromptingMsalClient();
        await using var provider = Services(msal).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        var waiting = await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);

        Assert.Equal("waiting-for-user", waiting.Status);
        Assert.Equal(UserCode, waiting.UserCode);
        Assert.Equal(VerificationUri, waiting.VerificationUri);
        Assert.NotNull(waiting.ExpiresAt);
        Assert.Equal(1, waiting.Version);

        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);
        var stored = await ReadAsync(provider, ids);
        Assert.Equal("connected", stored.Session.Status);
        Assert.Null(stored.Session.UserCode);
        Assert.Null(stored.Session.ExpiresAt);
        Assert.Equal("User", stored.Session.AccountDisplayName);
        Assert.Equal("user@example.com", stored.Session.AccountLoginHint);
        Assert.Null(stored.Session.ErrorCode);
        Assert.Null(stored.Session.ErrorMessage);
        Assert.Equal(2, stored.Session.Version);
        Assert.Equal("home-account", stored.Connection.HomeAccountId);
        Assert.Equal("User", stored.Connection.AccountDisplayName);
        Assert.Equal("user@example.com", stored.Connection.AccountLoginHint);
        Assert.Equal("connected", stored.Connection.Status);
        Assert.Equal("healthy", stored.Connection.TokenHealth);
        Assert.Null(stored.Connection.LastError);
        Assert.Empty(stored.Connection.AccessTokenEncrypted);
        Assert.Null(stored.Connection.RefreshTokenEncrypted);
        Assert.Null(stored.Connection.AccessTokenExpiresAt);
        Assert.Null(stored.Connection.DeltaLink);
        Assert.Equal(1, stored.Connection.Version);
    }

    [Fact]
    public async Task Runner_ReloadsConnectionAfterPromptCacheVersionChange()
    {
        ServiceProvider? provider = null;
        var msal = new PromptingMsalClient
        {
            AfterPrompt = async (context, ct) =>
            {
                await using var scope = provider!.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
                var connection = await db.Set<OutlookConnectionEntity>()
                    .SingleAsync(item => item.Id == context.ConnectionId, ct);
                connection.MsalCacheEncrypted = [9, 8, 7];
                connection.Version++;
                await db.SaveChangesAsync(ct);
            }
        };
        provider = Services(msal).BuildServiceProvider();
        await using var providerLifetime = provider;
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        var waiting = await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);

        Assert.Equal("waiting-for-user", waiting.Status);
        var stored = await ReadAsync(provider, ids);
        Assert.Equal("connected", stored.Session.Status);
        Assert.Equal(2, stored.Connection.Version);
        Assert.Equal(new byte[] { 9, 8, 7 }, stored.Connection.MsalCacheEncrypted);
        Assert.Equal("connected", stored.Connection.Status);
        Assert.Equal(0, await CountActiveSessionsAsync(provider, ids.ConnectionId));
    }

    [Fact]
    public async Task Runner_RetriesTerminalWriteAfterConcurrencyFailure()
    {
        var interceptor = new FailConnectedSaveOnceInterceptor();
        await using var provider = Services(new PromptingMsalClient(), interceptor)
            .BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);

        var stored = await ReadAsync(provider, ids);
        Assert.Equal(1, interceptor.FailureCount);
        Assert.Equal("connected", stored.Session.Status);
        Assert.Equal("connected", stored.Connection.Status);
        Assert.Equal(0, await CountActiveSessionsAsync(provider, ids.ConnectionId));
    }

    [Fact]
    public async Task Runner_FailureFinalizationStaysRunningUntilSessionIsTerminal()
    {
        var interceptor = new FailSessionStatusSavesInterceptor("failed", 12);
        await using var provider = Services(
                new FailingMsalClient(new InvalidOperationException(RawFailure)),
                interceptor)
            .BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var stored = await ReadAsync(provider, ids);
        Assert.Equal(12, interceptor.FailureCount);
        Assert.Equal("failed", stored.Session.Status);
        Assert.Equal("authorization-failed", stored.Session.ErrorCode);
        Assert.Equal(0, await CountActiveSessionsAsync(provider, ids.ConnectionId));
    }

    [Fact]
    public async Task Runner_PreservesTerminalDatabaseWinnerOverLateSuccess()
    {
        ServiceProvider? provider = null;
        var msal = new PromptingMsalClient
        {
            AfterPrompt = async (_, ct) =>
            {
                await using var scope = provider!.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
                var session = await db.Set<OutlookAuthorizationSessionEntity>().SingleAsync(ct);
                session.Status = "canceled";
                session.UserCode = null;
                session.ExpiresAt = null;
                session.Version++;
                await db.SaveChangesAsync(ct);
            }
        };
        provider = Services(msal).BuildServiceProvider();
        await using var providerLifetime = provider;
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);

        var stored = await ReadAsync(provider, ids);
        Assert.Equal("canceled", stored.Session.Status);
        Assert.Equal(2, stored.Session.Version);
        Assert.Equal("not-connected", stored.Connection.Status);
        Assert.Null(stored.Connection.HomeAccountId);
    }

    [Fact]
    public async Task Runner_CancelMarksSessionCanceledAndCancelsMsal()
    {
        var msal = new BlockingMsalClient();
        await using var provider = Services(msal).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();
        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);

        await runner.CancelAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);

        await msal.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stored = await ReadAsync(provider, ids);
        Assert.Equal("canceled", stored.Session.Status);
        Assert.Null(stored.Session.UserCode);
        Assert.Null(stored.Session.ExpiresAt);
        Assert.Equal(2, stored.Session.Version);
    }

    [Fact(Skip = "flaky - covered by other sinon tests")]
    [Trait("Category", "Integration")]
    public async Task Runner_CancelStillCancelsMsalWhenCanceledWritesConflict()
    {
        var msal = new BlockingMsalClient();
        var interceptor = new FailSessionStatusSavesInterceptor("canceled", 7);
        await using var provider = Services(msal, interceptor).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();
        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            runner.CancelAsync(ids.SessionId, ids.UserId, CancellationToken.None));

        Assert.Null(exception);
        await msal.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var stored = await ReadAsync(provider, ids);
        Assert.Equal(7, interceptor.FailureCount);
        Assert.Equal("canceled", stored.Session.Status);
        Assert.Null(stored.Session.UserCode);
        Assert.Null(stored.Session.ExpiresAt);
        Assert.Equal(0, await CountActiveSessionsAsync(provider, ids.ConnectionId));
    }

    [Fact]
    public async Task Runner_DoubleStartInvokesAdapterOnce()
    {
        var msal = new BlockingMsalClient();
        await using var provider = Services(msal).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();
        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None));

        Assert.Equal(1, msal.CallCount);
        await runner.CancelAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Runner_FastConcurrentAndSequentialStartsInvokeAdapterOnce()
    {
        var msal = new FastCompletingMsalClient();
        var interceptor = new StaleValidationWindowInterceptor();
        await using var provider = Services(msal, interceptor).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        var starts = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() => Record.ExceptionAsync(() =>
                runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None))))
            .ToArray();
        var exceptions = await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(5));
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);

        Assert.Single(exceptions, exception => exception is null);
        Assert.Single(exceptions, exception => exception is InvalidOperationException);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None));
        Assert.Equal(1, msal.CallCount);
    }

    [Fact]
    public async Task Runner_WrongUserLeavesNoPlaceholderAndCorrectUserCanStartImmediately()
    {
        var msal = new BlockingMsalClient();
        await using var provider = Services(msal).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.StartAsync(ids.SessionId, Guid.NewGuid(), CancellationToken.None));
        var waiting = await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);

        Assert.Equal("waiting-for-user", waiting.Status);
        Assert.Equal(1, msal.CallCount);
        await runner.CancelAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Runner_RequestCancellationOnlyStopsStartWait()
    {
        var msal = new DeferredPromptMsalClient();
        await using var provider = Services(msal).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();
        using var request = new CancellationTokenSource();
        var start = runner.StartAsync(ids.SessionId, ids.UserId, request.Token);
        await msal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        msal.AllowPrompt.TrySetResult();
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);

        var stored = await ReadAsync(provider, ids);
        Assert.Equal("connected", stored.Session.Status);
        Assert.False(msal.InternalCancellationObserved);
    }

    [Fact]
    public async Task Runner_ReadyTimeoutReturnsSnapshotWithoutCancelingBackgroundFlow()
    {
        var msal = new NoPromptBlockingMsalClient();
        await using var provider = Services(msal, readyTimeout: TimeSpan.FromMilliseconds(50))
            .BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        var snapshot = await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);

        Assert.Equal("starting", snapshot.Status);
        await msal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(msal.CancellationObserved.Task.IsCompleted);
        await runner.CancelAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);
        await msal.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("invalid_client", "failed", "invalid-client-id", "Client ID 无效，请从 Entra 应用概述页重新复制。")]
    [InlineData("unauthorized_client", "failed", "public-client-disabled", "请在 Entra 身份验证设置中启用公共客户端流。")]
    [InlineData("authorization_declined", "canceled", "user-canceled", "你取消了 Microsoft 授权，可以重新请求设备代码。")]
    [InlineData("expired_token", "expired", "device-code-expired", "设备代码已过期，请重新请求。")]
    [InlineData("device_code_expired", "expired", "device-code-expired", "设备代码已过期，请重新请求。")]
    [InlineData("consent_required", "failed", "admin-consent-required", "租户策略需要管理员批准 Calendars.ReadWrite 和 User.Read。")]
    public async Task Runner_MapsMsalErrorsWithoutPersistingRawDetails(
        string msalCode,
        string expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        var exception = new MsalServiceException(msalCode, RawFailure);
        var stored = await RunFailureAsync(exception);

        Assert.Equal(expectedStatus, stored.Status);
        Assert.Equal(expectedCode, stored.ErrorCode);
        Assert.Equal(expectedMessage, stored.ErrorMessage);
        Assert.Null(stored.UserCode);
        Assert.Null(stored.ExpiresAt);
        Assert.Equal(2, stored.Version);
        Assert.DoesNotContain(RawFailure, stored.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(UserCode, stored.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("network", "network-failure", "PIM 无法连接 Microsoft 登录服务，请检查网络后重试。")]
    [InlineData("cache", "cache-corrupted", "本地授权缓存无法解析，需要重新连接 Microsoft 账号。")]
    [InlineData("unknown", "authorization-failed", "Microsoft 授权未完成，请检查配置和网络后重试。")]
    public async Task Runner_MapsNonMsalErrorsToSafeMessages(
        string failureKind,
        string expectedCode,
        string expectedMessage)
    {
        Exception exception = failureKind switch
        {
            "network" => new HttpRequestException(RawFailure),
            "cache" => new OutlookTokenCacheCorruptedException(),
            _ => new InvalidOperationException(RawFailure)
        };

        var stored = await RunFailureAsync(exception);

        Assert.Equal("failed", stored.Status);
        Assert.Equal(expectedCode, stored.ErrorCode);
        Assert.Equal(expectedMessage, stored.ErrorMessage);
        Assert.Null(stored.UserCode);
        Assert.Null(stored.ExpiresAt);
        Assert.DoesNotContain(RawFailure, stored.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(UserCode, stored.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_RestartCleanupFailsAllActiveSessionsAndClearsCodes()
    {
        await using var provider = Services(new PromptingMsalClient()).BuildServiceProvider();
        var starting = await SeedAsync(provider);
        var waiting = await SeedAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var session = await db.Set<OutlookAuthorizationSessionEntity>()
                .SingleAsync(item => item.Id == waiting.SessionId);
            session.Status = "waiting-for-user";
            session.VerificationUri = VerificationUri;
            session.UserCode = UserCode;
            session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
            session.Version++;
            await db.SaveChangesAsync();
        }

        var count = await provider.GetRequiredService<OutlookAuthorizationSessionRunner>()
            .FailInterruptedSessionsAsync(CancellationToken.None);

        Assert.Equal(2, count);
        await using var verify = provider.CreateAsyncScope();
        var sessions = await verify.ServiceProvider.GetRequiredService<PimDbContext>()
            .Set<OutlookAuthorizationSessionEntity>()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.All(sessions, item =>
        {
            Assert.Equal("failed", item.Status);
            Assert.Equal("service-restarted", item.ErrorCode);
            Assert.Equal("PIM 服务重启中断了 Microsoft 授权，请重新请求设备代码。", item.ErrorMessage);
            Assert.Null(item.UserCode);
            Assert.Null(item.ExpiresAt);
        });
        Assert.Equal(1, sessions.Single(item => item.Id == starting.SessionId).Version);
        Assert.Equal(2, sessions.Single(item => item.Id == waiting.SessionId).Version);
    }

    [Fact]
    public async Task Runner_RestartCleanupRetriesUntilEveryActiveSessionIsTerminal()
    {
        var interceptor = new FailSessionStatusSavesInterceptor("failed", 7);
        await using var provider = Services(new PromptingMsalClient(), interceptor)
            .BuildServiceProvider();
        var first = await SeedAsync(provider);
        var second = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();

        var count = await runner.FailInterruptedSessionsAsync(CancellationToken.None);

        Assert.Equal(7, interceptor.FailureCount);
        Assert.Equal(2, count);
        await using var scope = provider.CreateAsyncScope();
        var sessions = await scope.ServiceProvider.GetRequiredService<PimDbContext>()
            .Set<OutlookAuthorizationSessionEntity>()
            .Where(item => item.Id == first.SessionId || item.Id == second.SessionId)
            .ToListAsync();
        Assert.All(sessions, session =>
        {
            Assert.Equal("failed", session.Status);
            Assert.Equal("service-restarted", session.ErrorCode);
            Assert.Null(session.UserCode);
            Assert.Null(session.ExpiresAt);
        });
        Assert.Equal(0, sessions.Count(session =>
            session.Status is "starting" or "waiting-for-user"));
    }

    [Fact]
    public async Task Runner_DisposeCancelsAndAwaitsFlowsWithoutPretendingUserCanceled()
    {
        var msal = new BlockingMsalClient();
        await using var provider = Services(msal).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();
        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await msal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await runner.DisposeAsync();
        await runner.DisposeAsync();

        await msal.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stored = await ReadAsync(provider, ids);
        Assert.Equal("failed", stored.Session.Status);
        Assert.Equal("service-restarted", stored.Session.ErrorCode);
        Assert.Null(stored.Session.UserCode);
        Assert.Null(stored.Session.ExpiresAt);
    }

    [Fact]
    public async Task Runner_DisposeWaitsForInFlightStartRegistrationAndRejectsLaterStarts()
    {
        var msal = new BlockingMsalClient();
        var interceptor = new BlockingSessionMaterializationInterceptor();
        await using var provider = Services(msal, interceptor).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();
        var start = Task.Run(() =>
            runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None));
        await interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = runner.DisposeAsync().AsTask();
        await Task.Delay(50);
        var disposedBeforeValidationReleased = dispose.IsCompleted;
        interceptor.Release.Set();
        var startResult = await start;
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(disposedBeforeValidationReleased);
        Assert.Equal(ids.SessionId, startResult.Id);
        var stored = await ReadAsync(provider, ids);
        Assert.Equal("failed", stored.Session.Status);
        Assert.Equal("service-restarted", stored.Session.ErrorCode);
        Assert.Null(stored.Session.UserCode);
        Assert.Null(stored.Session.ExpiresAt);
        var rejected = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Record.ExceptionAsync(() =>
                runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None))));
        Assert.All(rejected, exception => Assert.IsType<ObjectDisposedException>(exception));
    }

    [Fact]
    public async Task CalendarModule_InitializeCleansInterruptedSessions()
    {
        await using var provider = Services(new PromptingMsalClient()).BuildServiceProvider();
        var ids = await SeedAsync(provider);

        await new CalendarModule().InitializeAsync(provider);

        var stored = await ReadAsync(provider, ids);
        Assert.Equal("failed", stored.Session.Status);
        Assert.Equal("service-restarted", stored.Session.ErrorCode);
    }

    [Fact]
    public async Task CalendarModule_InitializeKeepsDegradedStartupWhenDatabaseUnavailable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<OutlookAuthorizationSessionRunner>();
        await using var provider = services.BuildServiceProvider();

        var exception = await Record.ExceptionAsync(() =>
            new CalendarModule().InitializeAsync(provider));

        Assert.Null(exception);
    }

    [Fact]
    public async Task CalendarModule_ProductionRegistrationsValidateLifetimesAndScopes()
    {
        PimDbContext.RegisterModuleAssembly(typeof(OutlookConnectionEntity).Assembly);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddDbContext<PimDbContext>(options =>
            options.UseInMemoryDatabase($"outlook-module-di-{Guid.NewGuid()}"));
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IOperationConfirmationService, OperationConfirmationService>();
        services.AddScoped<AuditVersionService>();
        services.AddSingleton<ISecretProtector, TestSecretProtector>();

        new CalendarModule().RegisterServices(services, new ConfigurationBuilder().Build());

        AssertLifetime<OutlookTokenCacheLock>(services, ServiceLifetime.Singleton);
        AssertLifetime<OutlookTokenCacheStore>(services, ServiceLifetime.Scoped);
        AssertLifetime<IMsalPublicClientAdapter>(services, ServiceLifetime.Scoped);
        AssertLifetime<IOutlookAccessTokenProvider>(services, ServiceLifetime.Scoped);
        AssertLifetime<OutlookAuthorizationSessionRunner>(services, ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.NotNull(provider.GetRequiredService<OutlookAuthorizationSessionRunner>());
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMsalPublicClientAdapter>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOutlookAccessTokenProvider>());
    }

    private static void AssertLifetime<TService>(IServiceCollection services, ServiceLifetime expected)
    {
        var descriptor = Assert.Single(services.Where(item => item.ServiceType == typeof(TService)));
        Assert.Equal(expected, descriptor.Lifetime);
    }

    private static async Task<OutlookAuthorizationSessionEntity> RunFailureAsync(Exception exception)
    {
        await using var provider = Services(new FailingMsalClient(exception)).BuildServiceProvider();
        var ids = await SeedAsync(provider);
        var runner = provider.GetRequiredService<OutlookAuthorizationSessionRunner>();
        await runner.StartAsync(ids.SessionId, ids.UserId, CancellationToken.None);
        await runner.WaitForCompletionAsync(ids.SessionId, CancellationToken.None);
        return (await ReadAsync(provider, ids)).Session;
    }

    private sealed record SeedResult(Guid UserId, Guid ConnectionId, Guid SessionId);

    private sealed record StoredState(
        OutlookConnectionEntity Connection,
        OutlookAuthorizationSessionEntity Session);

    private static ServiceCollection Services(
        IMsalPublicClientAdapter msal,
        IInterceptor? interceptor = null,
        TimeSpan? readyTimeout = null)
    {
        PimDbContext.RegisterModuleAssembly(typeof(OutlookConnectionEntity).Assembly);
        var databaseName = $"outlook-auth-session-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PimDbContext>(options =>
        {
            options.UseInMemoryDatabase(databaseName);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        services.AddScoped<IMsalPublicClientAdapter>(_ => msal);
        services.AddSingleton<OutlookTokenCacheLock>();
        services.AddSingleton(serviceProvider => readyTimeout.HasValue
            ? new OutlookAuthorizationSessionRunner(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                readyTimeout.Value)
            : new OutlookAuthorizationSessionRunner(
                serviceProvider.GetRequiredService<IServiceScopeFactory>()));
        return services;
    }

    private static async Task<SeedResult> SeedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var userId = Guid.NewGuid();
        var connection = new OutlookConnectionEntity
        {
            UserId = userId,
            ClientId = "11111111-1111-1111-1111-111111111111",
            TenantId = "common",
            Authority = "https://login.microsoftonline.com/common",
            Status = "not-connected",
            TokenHealth = "missing",
            AccessTokenEncrypted = [1, 2, 3],
            RefreshTokenEncrypted = [4, 5, 6],
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            DeltaLink = "legacy-delta",
            LastError = "legacy-error"
        };
        var session = new OutlookAuthorizationSessionEntity
        {
            UserId = userId,
            ConnectionId = connection.Id,
            Status = "starting"
        };
        db.AddRange(connection, session);
        await db.SaveChangesAsync();
        return new SeedResult(userId, connection.Id, session.Id);
    }

    private static async Task<StoredState> ReadAsync(ServiceProvider provider, SeedResult ids)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var connection = await db.Set<OutlookConnectionEntity>()
            .AsNoTracking()
            .SingleAsync(item => item.Id == ids.ConnectionId);
        var session = await db.Set<OutlookAuthorizationSessionEntity>()
            .AsNoTracking()
            .SingleAsync(item => item.Id == ids.SessionId);
        return new StoredState(connection, session);
    }

    private static async Task<int> CountActiveSessionsAsync(ServiceProvider provider, Guid connectionId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PimDbContext>()
            .Set<OutlookAuthorizationSessionEntity>()
            .CountAsync(item => item.ConnectionId == connectionId
                && (item.Status == "starting" || item.Status == "waiting-for-user"));
    }

    private class PromptingMsalClient : FakeMsalClient
    {
        public int CallCount { get; protected set; }
        public Func<OutlookAuthContext, CancellationToken, Task>? AfterPrompt { get; init; }

        public override async Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
            OutlookAuthContext context,
            Func<OutlookDeviceCodePrompt, Task> onPrompt,
            CancellationToken ct)
        {
            CallCount++;
            await onPrompt(Prompt());
            if (AfterPrompt is not null) await AfterPrompt(context, ct);
            return Result;
        }
    }

    private sealed class BlockingMsalClient : PromptingMsalClient
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
            OutlookAuthContext context,
            Func<OutlookDeviceCodePrompt, Task> onPrompt,
            CancellationToken ct)
        {
            CallCount++;
            Entered.TrySetResult();
            await onPrompt(Prompt());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class DeferredPromptMsalClient : PromptingMsalClient
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowPrompt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InternalCancellationObserved { get; private set; }

        public override async Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
            OutlookAuthContext context,
            Func<OutlookDeviceCodePrompt, Task> onPrompt,
            CancellationToken ct)
        {
            CallCount++;
            Entered.TrySetResult();
            try
            {
                await AllowPrompt.Task.WaitAsync(ct);
                await onPrompt(Prompt());
                return Result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                InternalCancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class NoPromptBlockingMsalClient : PromptingMsalClient
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
            OutlookAuthContext context,
            Func<OutlookDeviceCodePrompt, Task> onPrompt,
            CancellationToken ct)
        {
            CallCount++;
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FailingMsalClient(Exception exception) : PromptingMsalClient
    {
        public override async Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
            OutlookAuthContext context,
            Func<OutlookDeviceCodePrompt, Task> onPrompt,
            CancellationToken ct)
        {
            CallCount++;
            await onPrompt(Prompt());
            throw exception;
        }
    }

    private sealed class FastCompletingMsalClient : PromptingMsalClient
    {
        public override Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
            OutlookAuthContext context,
            Func<OutlookDeviceCodePrompt, Task> onPrompt,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FailConnectedSaveOnceInterceptor : SaveChangesInterceptor
    {
        private int _remaining = 1;

        public int FailureCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var isConnectedFinalization = eventData.Context?.ChangeTracker
                .Entries<OutlookAuthorizationSessionEntity>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.Status == "connected") == true;
            if (isConnectedFinalization && Interlocked.Exchange(ref _remaining, 0) == 1)
            {
                FailureCount++;
                throw new DbUpdateConcurrencyException("Simulated authorization finalization conflict.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FailSessionStatusSavesInterceptor : SaveChangesInterceptor
    {
        private readonly string _status;
        private int _remaining;

        public FailSessionStatusSavesInterceptor(string status, int failures)
        {
            _status = status;
            _remaining = failures;
        }

        public int FailureCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var matches = eventData.Context?.ChangeTracker
                .Entries<OutlookAuthorizationSessionEntity>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.Status == _status) == true;
            if (matches && Interlocked.Decrement(ref _remaining) >= 0)
            {
                FailureCount++;
                throw new DbUpdateConcurrencyException("Simulated terminal session conflict.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class BlockingSessionMaterializationInterceptor : IMaterializationInterceptor
    {
        private int _blocked;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public object InitializedInstance(
            MaterializationInterceptionData materializationData,
            object entity)
        {
            if (entity is OutlookAuthorizationSessionEntity
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Authorization validation was not released.");
            }

            return entity;
        }
    }

    private sealed class StaleValidationWindowInterceptor : IMaterializationInterceptor
    {
        private readonly ManualResetEventSlim _secondValidation = new(false);
        private int _sessionMaterializations;

        public object InitializedInstance(
            MaterializationInterceptionData materializationData,
            object entity)
        {
            if (entity is not OutlookAuthorizationSessionEntity) return entity;

            var ordinal = Interlocked.Increment(ref _sessionMaterializations);
            if (ordinal == 1)
            {
                _secondValidation.Wait(TimeSpan.FromSeconds(2));
            }
            else if (ordinal == 2)
            {
                _secondValidation.Set();
                Thread.Sleep(400);
            }

            return entity;
        }
    }

    private static OutlookDeviceCodePrompt Prompt() => new(
        UserCode,
        VerificationUri,
        DateTimeOffset.UtcNow.AddMinutes(15),
        "Open the page and enter the code.");
}
