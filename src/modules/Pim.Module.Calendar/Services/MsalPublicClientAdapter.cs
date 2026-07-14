using Microsoft.Identity.Client;

namespace Pim.Module.Calendar.Services;

public static class OutlookAuthScopes
{
    public static readonly string[] Required = ["Calendars.ReadWrite", "User.Read"];
}

public sealed record OutlookAuthContext(
    Guid ConnectionId,
    string ClientId,
    string Authority,
    string? HomeAccountId);

public sealed record OutlookDeviceCodePrompt(
    string UserCode,
    string VerificationUri,
    DateTimeOffset ExpiresAt,
    string Message)
{
    public override string ToString()
        => $"{nameof(OutlookDeviceCodePrompt)} {{ VerificationUri = {VerificationUri}, ExpiresAt = {ExpiresAt:O} }}";
}

public sealed record MsalAuthenticationResult(
    string AccessToken,
    string HomeAccountId,
    string? Username,
    string? DisplayName,
    DateTimeOffset ExpiresOn,
    IReadOnlyList<string> Scopes)
{
    public override string ToString()
        => $"{nameof(MsalAuthenticationResult)} {{ ExpiresOn = {ExpiresOn:O}, ScopeCount = {Scopes.Count} }}";
}

public interface IMsalPublicClientAdapter
{
    Task<MsalAuthenticationResult> AcquireTokenSilentAsync(
        OutlookAuthContext context,
        bool forceRefresh,
        CancellationToken ct);

    Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
        OutlookAuthContext context,
        Func<OutlookDeviceCodePrompt, Task> onPrompt,
        CancellationToken ct);
}

public sealed class OutlookReauthenticationRequiredException(string code, Exception? innerException = null)
    : Exception("Microsoft account interaction is required.", innerException)
{
    public string Code { get; } = code;
}

internal interface IMsalClientApplication
{
    void BindCache(
        Func<ITokenCacheSerializer, CancellationToken, Task> beforeAccess,
        Func<ITokenCacheSerializer, bool, CancellationToken, Task> afterAccess);

    Task<bool> TrySelectAccountAsync(string homeAccountId, CancellationToken ct);
    Task<MsalAuthenticationResult> AcquireTokenSilentAsync(bool forceRefresh, CancellationToken ct);

    Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
        Func<OutlookDeviceCodePrompt, Task> onPrompt,
        CancellationToken ct);
}

public sealed class MsalPublicClientAdapter : IMsalPublicClientAdapter
{
    private readonly OutlookTokenCacheStore _cacheStore;
    private readonly Func<OutlookAuthContext, IMsalClientApplication> _clientFactory;

    public MsalPublicClientAdapter(OutlookTokenCacheStore cacheStore)
        : this(cacheStore, Build)
    {
    }

    internal MsalPublicClientAdapter(
        OutlookTokenCacheStore cacheStore,
        Func<OutlookAuthContext, IMsalClientApplication> clientFactory)
    {
        _cacheStore = cacheStore;
        _clientFactory = clientFactory;
    }

    public async Task<MsalAuthenticationResult> AcquireTokenSilentAsync(
        OutlookAuthContext context,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.HomeAccountId))
            throw new OutlookReauthenticationRequiredException("account-anchor-missing");

        var app = _clientFactory(context);
        BindCache(app, context.ConnectionId);
        if (!await app.TrySelectAccountAsync(context.HomeAccountId, ct))
            throw new OutlookReauthenticationRequiredException("account-missing");

        try
        {
            return await app.AcquireTokenSilentAsync(forceRefresh, ct);
        }
        catch (MsalUiRequiredException exception)
        {
            throw new OutlookReauthenticationRequiredException(exception.ErrorCode, exception);
        }
    }

    public async Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
        OutlookAuthContext context,
        Func<OutlookDeviceCodePrompt, Task> onPrompt,
        CancellationToken ct)
    {
        var app = _clientFactory(context);
        BindCache(app, context.ConnectionId);
        return await app.AcquireTokenWithDeviceCodeAsync(onPrompt, ct);
    }

    private void BindCache(IMsalClientApplication app, Guid connectionId)
    {
        OutlookTokenCacheSnapshot? snapshot = null;
        app.BindCache(
            async (tokenCache, callbackCt) =>
            {
                snapshot = await _cacheStore.LoadAsync(connectionId, callbackCt);
                if (snapshot.Blob is not { Length: > 0 }) return;

                try
                {
                    tokenCache.DeserializeMsalV3(snapshot.Blob, shouldClearExistingCache: true);
                }
                catch (MsalClientException exception) when (exception.ErrorCode == MsalError.JsonParseError)
                {
                    throw new OutlookTokenCacheCorruptedException();
                }
            },
            async (tokenCache, hasStateChanged, callbackCt) =>
            {
                if (!hasStateChanged) return;

                snapshot ??= await _cacheStore.LoadAsync(connectionId, callbackCt);
                snapshot = await _cacheStore.SaveAsync(
                    connectionId,
                    tokenCache.SerializeMsalV3(),
                    snapshot.Version,
                    callbackCt);
            });
    }

    private static IMsalClientApplication Build(OutlookAuthContext context)
        => new MsalClientApplication(
            PublicClientApplicationBuilder.Create(context.ClientId)
                .WithAuthority(context.Authority)
                .Build());
}

internal sealed class MsalClientApplication(IPublicClientApplication application) : IMsalClientApplication
{
    private IAccount? _selectedAccount;

    public void BindCache(
        Func<ITokenCacheSerializer, CancellationToken, Task> beforeAccess,
        Func<ITokenCacheSerializer, bool, CancellationToken, Task> afterAccess)
    {
        application.UserTokenCache.SetBeforeAccessAsync(args =>
            beforeAccess(args.TokenCache, args.CancellationToken));
        application.UserTokenCache.SetAfterAccessAsync(args =>
            afterAccess(args.TokenCache, args.HasStateChanged, args.CancellationToken));
    }

    public async Task<bool> TrySelectAccountAsync(string homeAccountId, CancellationToken ct)
    {
        if (application is ClientApplicationBase concreteApplication)
        {
            _selectedAccount = await concreteApplication.GetAccountAsync(homeAccountId, ct);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            _selectedAccount = await application.GetAccountAsync(homeAccountId);
            ct.ThrowIfCancellationRequested();
        }

        return _selectedAccount is not null;
    }

    public async Task<MsalAuthenticationResult> AcquireTokenSilentAsync(
        bool forceRefresh,
        CancellationToken ct)
    {
        var account = _selectedAccount
            ?? throw new InvalidOperationException("An exact Microsoft account must be selected first.");
        var result = await application.AcquireTokenSilent(OutlookAuthScopes.Required, account)
            .WithForceRefresh(forceRefresh)
            .ExecuteAsync(ct);
        return Map(result);
    }

    public async Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
        Func<OutlookDeviceCodePrompt, Task> onPrompt,
        CancellationToken ct)
    {
        var result = await application.AcquireTokenWithDeviceCode(
                OutlookAuthScopes.Required,
                code => onPrompt(new OutlookDeviceCodePrompt(
                    code.UserCode,
                    code.VerificationUrl,
                    code.ExpiresOn,
                    code.Message)))
            .ExecuteAsync(ct);
        return Map(result);
    }

    private static MsalAuthenticationResult Map(AuthenticationResult result)
        => new(
            result.AccessToken,
            result.Account.HomeAccountId.Identifier,
            result.Account.Username,
            result.ClaimsPrincipal?.Identity?.Name,
            result.ExpiresOn,
            result.Scopes.ToArray());
}
