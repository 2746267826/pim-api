using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookAuthorizationSessionRunner : IAsyncDisposable
{
    private const int FinalizationAttempts = 5;
    private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(30);
    private const string ServiceRestartedMessage =
        "PIM 服务重启中断了 Microsoft 授权，请重新请求设备代码。";
    private const string StateConflictMessage =
        "Microsoft 授权状态更新冲突，请重新请求设备代码。";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _readyTimeout;
    private readonly ConcurrentDictionary<Guid, RunningSession> _running = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _disposeStarted;

    public OutlookAuthorizationSessionRunner(IServiceScopeFactory scopeFactory)
        : this(scopeFactory, DefaultReadyTimeout)
    {
    }

    internal OutlookAuthorizationSessionRunner(
        IServiceScopeFactory scopeFactory,
        TimeSpan readyTimeout)
    {
        _scopeFactory = scopeFactory;
        _readyTimeout = readyTimeout > TimeSpan.Zero
            ? readyTimeout
            : throw new ArgumentOutOfRangeException(nameof(readyTimeout));
    }

    public async Task<OutlookAuthorizationSessionEntity> StartAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken requestToken)
    {
        AuthorizationContext context;
        RunningSession running;
        await _lifecycleGate.WaitAsync(requestToken);
        try
        {
            if (_disposeStarted != 0)
                throw new ObjectDisposedException(nameof(OutlookAuthorizationSessionRunner));

            context = await ValidateStartAsync(sessionId, userId, requestToken);
            if (_running.ContainsKey(sessionId))
                throw new InvalidOperationException("This Microsoft authorization session is already running.");

            running = new RunningSession();
            if (!_running.TryAdd(sessionId, running))
            {
                running.DisposeCancellation();
                throw new InvalidOperationException("This Microsoft authorization session is already running.");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _ = ExecuteRegisteredAsync(context, running);
        try
        {
            return await running.Ready.Task.WaitAsync(_readyTimeout, requestToken);
        }
        catch (TimeoutException)
        {
            return await ReadSessionAsync(sessionId, userId, requestToken);
        }
    }

    public async Task CancelAsync(Guid sessionId, Guid userId, CancellationToken ct)
    {
        await ReadSessionAsync(sessionId, userId, ct);
        if (_running.TryGetValue(sessionId, out var running))
        {
            running.TryCancel(CancellationReason.ApiCancel);
            return;
        }

        await FinalizeFailureAsync(sessionId, userId, "canceled", null, null);
    }

    public async Task WaitForCompletionAsync(Guid sessionId, CancellationToken ct)
    {
        if (_running.TryGetValue(sessionId, out var running))
            await running.Completion.Task.WaitAsync(ct);
    }

    public async Task<int> FailInterruptedSessionsAsync(CancellationToken ct)
    {
        var failedIds = new HashSet<Guid>();
        for (var attempt = 0; attempt < FinalizationAttempts; attempt++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var interrupted = await db.Set<OutlookAuthorizationSessionEntity>()
                .Where(item => item.Status == "starting" || item.Status == "waiting-for-user")
                .ToListAsync(ct);
            if (interrupted.Count == 0) return failedIds.Count;

            var now = DateTimeOffset.UtcNow;
            foreach (var session in interrupted)
            {
                failedIds.Add(session.Id);
                MarkTerminal(
                    session,
                    "failed",
                    "service-restarted",
                    ServiceRestartedMessage,
                    now);
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return failedIds.Count;
            }
            catch (DbUpdateConcurrencyException) when (attempt + 1 < FinalizationAttempts)
            {
            }
        }

        return failedIds.Count;
    }

    public async ValueTask DisposeAsync()
    {
        RunningSession[] running;
        await _lifecycleGate.WaitAsync();
        try
        {
            _disposeStarted = 1;
            running = _running.Values.ToArray();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        foreach (var item in running) item.TryCancel(CancellationReason.HostDispose);
        await Task.WhenAll(running.Select(item => item.Completion.Task));
    }

    private async Task<AuthorizationContext> ValidateStartAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var session = await db.Set<OutlookAuthorizationSessionEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sessionId && item.UserId == userId, ct)
            ?? throw new InvalidOperationException("Microsoft authorization session was not found.");
        if (!IsActive(session.Status))
            throw new InvalidOperationException("Microsoft authorization session is not active.");

        var connection = await db.Set<OutlookConnectionEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == session.ConnectionId && item.UserId == userId,
                ct)
            ?? throw new InvalidOperationException("Microsoft connection was not found.");
        if (string.IsNullOrWhiteSpace(connection.ClientId))
            throw new InvalidOperationException("Microsoft Client ID is not configured.");

        return new AuthorizationContext(
            session.Id,
            userId,
            connection.Id,
            connection.ClientId,
            connection.Authority,
            connection.HomeAccountId);
    }

    private async Task ExecuteRegisteredAsync(AuthorizationContext context, RunningSession running)
    {
        try
        {
            await RunAsync(context, running);
        }
        catch
        {
            running.Ready.TrySetException(
                new InvalidOperationException("Microsoft authorization could not be started."));
        }
        finally
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                if (((ICollection<KeyValuePair<Guid, RunningSession>>)_running)
                    .Remove(new KeyValuePair<Guid, RunningSession>(context.SessionId, running)))
                {
                    running.DisposeCancellation();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
            running.Completion.TrySetResult();
        }
    }

    private async Task RunAsync(AuthorizationContext context, RunningSession running)
    {
        try
        {
            await using var flowScope = _scopeFactory.CreateAsyncScope();
            var msal = flowScope.ServiceProvider.GetRequiredService<IMsalPublicClientAdapter>();
            var tokenCacheLock = flowScope.ServiceProvider.GetRequiredService<OutlookTokenCacheLock>();
            await using var held = await tokenCacheLock.AcquireAsync(
                context.ConnectionId,
                running.Cancellation.Token);
            var result = await msal.AcquireTokenWithDeviceCodeAsync(
                new OutlookAuthContext(
                    context.ConnectionId,
                    context.ClientId,
                    context.Authority,
                    context.HomeAccountId),
                async prompt =>
                {
                    var published = await PublishPromptAsync(
                        context.SessionId,
                        context.UserId,
                        prompt,
                        CancellationToken.None);
                    running.Ready.TrySetResult(published);
                },
                running.Cancellation.Token);
            running.Cancellation.Token.ThrowIfCancellationRequested();

            var completed = await FinalizeSuccessAsync(context, result);
            running.Ready.TrySetResult(completed);
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
        {
            var completed = running.Reason switch
            {
                CancellationReason.HostDispose => await FinalizeFailureAsync(
                    context.SessionId,
                    context.UserId,
                    "failed",
                    "service-restarted",
                    ServiceRestartedMessage),
                CancellationReason.ApiCancel => await FinalizeFailureAsync(
                    context.SessionId,
                    context.UserId,
                    "canceled",
                    null,
                    null),
                _ => await ReadSessionAsync(
                    context.SessionId,
                    context.UserId,
                    CancellationToken.None)
            };
            running.Ready.TrySetResult(completed);
        }
        catch (Exception exception)
        {
            var code = MapErrorCode(exception);
            var status = code switch
            {
                "device-code-expired" => "expired",
                "user-canceled" => "canceled",
                _ => "failed"
            };
            var completed = await FinalizeFailureAsync(
                context.SessionId,
                context.UserId,
                status,
                code,
                SafeMessage(code));
            running.Ready.TrySetResult(completed);
        }
    }

    private async Task<OutlookAuthorizationSessionEntity> PublishPromptAsync(
        Guid sessionId,
        Guid userId,
        OutlookDeviceCodePrompt prompt,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < FinalizationAttempts; attempt++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var session = await db.Set<OutlookAuthorizationSessionEntity>()
                .SingleAsync(item => item.Id == sessionId && item.UserId == userId, ct);
            if (!IsActive(session.Status)) return Clone(session);

            session.Status = "waiting-for-user";
            session.VerificationUri = prompt.VerificationUri;
            session.UserCode = prompt.UserCode;
            session.ExpiresAt = prompt.ExpiresAt;
            session.ErrorCode = null;
            session.ErrorMessage = null;
            IncrementVersion(session);
            try
            {
                await db.SaveChangesAsync(ct);
                return Clone(session);
            }
            catch (DbUpdateConcurrencyException) when (attempt + 1 < FinalizationAttempts)
            {
            }
        }

        return await FinalizeFailureAsync(
            sessionId,
            userId,
            "failed",
            "authorization-state-conflict",
            StateConflictMessage);
    }

    private async Task<OutlookAuthorizationSessionEntity> FinalizeSuccessAsync(
        AuthorizationContext context,
        MsalAuthenticationResult result)
    {
        for (var attempt = 0; attempt < FinalizationAttempts; attempt++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var session = await db.Set<OutlookAuthorizationSessionEntity>()
                .SingleAsync(
                    item => item.Id == context.SessionId && item.UserId == context.UserId,
                    CancellationToken.None);
            if (!IsActive(session.Status)) return Clone(session);

            var connection = await db.Set<OutlookConnectionEntity>()
                .SingleAsync(
                    item => item.Id == context.ConnectionId && item.UserId == context.UserId,
                    CancellationToken.None);
            connection.HomeAccountId = result.HomeAccountId;
            connection.AccountDisplayName = result.DisplayName;
            connection.AccountLoginHint = result.Username;
            connection.Status = "connected";
            connection.TokenHealth = "healthy";
            connection.LastError = null;
            connection.AccessTokenEncrypted = [];
            connection.RefreshTokenEncrypted = null;
            connection.AccessTokenExpiresAt = null;
            connection.DeltaLink = null;
            connection.Version = checked(connection.Version + 1);
            connection.UpdatedAt = DateTimeOffset.UtcNow;

            session.Status = "connected";
            session.AccountDisplayName = result.DisplayName;
            session.AccountLoginHint = result.Username;
            session.ErrorCode = null;
            session.ErrorMessage = null;
            session.UserCode = null;
            session.ExpiresAt = null;
            IncrementVersion(session);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return Clone(session);
            }
            catch (DbUpdateConcurrencyException) when (attempt + 1 < FinalizationAttempts)
            {
            }
        }

        return await FinalizeFailureAsync(
            context.SessionId,
            context.UserId,
            "failed",
            "authorization-state-conflict",
            StateConflictMessage);
    }

    private async Task<OutlookAuthorizationSessionEntity> FinalizeFailureAsync(
        Guid sessionId,
        Guid userId,
        string status,
        string? errorCode,
        string? errorMessage)
    {
        while (true)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var session = await db.Set<OutlookAuthorizationSessionEntity>()
                .SingleAsync(
                    item => item.Id == sessionId && item.UserId == userId,
                    CancellationToken.None);
            if (!IsActive(session.Status)) return Clone(session);

            MarkTerminal(session, status, errorCode, errorMessage, DateTimeOffset.UtcNow);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return Clone(session);
            }
            catch (DbUpdateConcurrencyException)
            {
            }
        }
    }

    private async Task<OutlookAuthorizationSessionEntity> ReadSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var session = await db.Set<OutlookAuthorizationSessionEntity>()
            .AsNoTracking()
            .SingleAsync(item => item.Id == sessionId && item.UserId == userId, ct);
        return Clone(session);
    }

    private static void MarkTerminal(
        OutlookAuthorizationSessionEntity session,
        string status,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset now)
    {
        session.Status = status;
        session.ErrorCode = errorCode;
        session.ErrorMessage = errorMessage;
        session.UserCode = null;
        session.ExpiresAt = null;
        session.UpdatedAt = now;
        session.Version = checked(session.Version + 1);
    }

    private static void IncrementVersion(OutlookAuthorizationSessionEntity session)
    {
        session.Version = checked(session.Version + 1);
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static bool IsActive(string status)
        => status is "starting" or "waiting-for-user";

    private static string MapErrorCode(Exception exception)
    {
        if (exception is MsalException msal)
        {
            return msal.ErrorCode switch
            {
                "invalid_client" => "invalid-client-id",
                "unauthorized_client" => "public-client-disabled",
                "authorization_declined" => "user-canceled",
                "expired_token" or "device_code_expired" => "device-code-expired",
                "consent_required" => "admin-consent-required",
                _ => "authorization-failed"
            };
        }

        return exception switch
        {
            HttpRequestException => "network-failure",
            OutlookTokenCacheCorruptedException => "cache-corrupted",
            _ => "authorization-failed"
        };
    }

    private static string SafeMessage(string code) => code switch
    {
        "invalid-client-id" => "Client ID 无效，请从 Entra 应用概述页重新复制。",
        "public-client-disabled" => "请在 Entra 身份验证设置中启用公共客户端流。",
        "user-canceled" => "你取消了 Microsoft 授权，可以重新请求设备代码。",
        "device-code-expired" => "设备代码已过期，请重新请求。",
        "admin-consent-required" => "租户策略需要管理员批准 Calendars.ReadWrite 和 User.Read。",
        "network-failure" => "PIM 无法连接 Microsoft 登录服务，请检查网络后重试。",
        "cache-corrupted" => "本地授权缓存无法解析，需要重新连接 Microsoft 账号。",
        _ => "Microsoft 授权未完成，请检查配置和网络后重试。"
    };

    private static OutlookAuthorizationSessionEntity Clone(
        OutlookAuthorizationSessionEntity source) => new()
    {
        Id = source.Id,
        UserId = source.UserId,
        ConnectionId = source.ConnectionId,
        Status = source.Status,
        VerificationUri = source.VerificationUri,
        UserCode = source.UserCode,
        ExpiresAt = source.ExpiresAt,
        AccountDisplayName = source.AccountDisplayName,
        AccountLoginHint = source.AccountLoginHint,
        ErrorCode = source.ErrorCode,
        ErrorMessage = source.ErrorMessage,
        Version = source.Version,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt
    };

    private sealed record AuthorizationContext(
        Guid SessionId,
        Guid UserId,
        Guid ConnectionId,
        string ClientId,
        string Authority,
        string? HomeAccountId);

    private enum CancellationReason
    {
        None,
        ApiCancel,
        HostDispose
    }

    private sealed class RunningSession
    {
        private int _reason;
        private int _cancellationDisposed;

        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<OutlookAuthorizationSessionEntity> Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationReason Reason => (CancellationReason)Volatile.Read(ref _reason);

        public bool TryCancel(CancellationReason reason)
        {
            Interlocked.CompareExchange(ref _reason, (int)reason, (int)CancellationReason.None);
            try
            {
                Cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
                Cancellation.Dispose();
        }
    }
}
