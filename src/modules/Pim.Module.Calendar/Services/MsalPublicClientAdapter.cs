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
    string Message);

public sealed record MsalAuthenticationResult(
    string AccessToken,
    string HomeAccountId,
    string? Username,
    string? DisplayName,
    DateTimeOffset ExpiresOn,
    IReadOnlyList<string> Scopes);

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

public sealed class MsalPublicClientAdapter : IMsalPublicClientAdapter
{
    private readonly OutlookTokenCacheStore _cacheStore;

    public MsalPublicClientAdapter(OutlookTokenCacheStore cacheStore) => _cacheStore = cacheStore;

    public async Task<MsalAuthenticationResult> AcquireTokenSilentAsync(
        OutlookAuthContext context,
        bool forceRefresh,
        CancellationToken ct)
    {
        var app = Build(context);
        BindCache(app.UserTokenCache, context.ConnectionId);
        var accounts = await app.GetAccountsAsync();
        var account = accounts.SingleOrDefault(item => item.HomeAccountId.Identifier == context.HomeAccountId)
            ?? accounts.SingleOrDefault();
        if (account is null) throw new OutlookReauthenticationRequiredException("account-missing");

        try
        {
            var result = await app.AcquireTokenSilent(OutlookAuthScopes.Required, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(ct);
            return Map(result);
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
        var app = Build(context);
        BindCache(app.UserTokenCache, context.ConnectionId);
        var result = await app.AcquireTokenWithDeviceCode(
                OutlookAuthScopes.Required,
                code => onPrompt(new OutlookDeviceCodePrompt(
                    code.UserCode,
                    code.VerificationUrl,
                    code.ExpiresOn,
                    code.Message)))
            .ExecuteAsync(ct);
        return Map(result);
    }

    private static IPublicClientApplication Build(OutlookAuthContext context)
        => PublicClientApplicationBuilder.Create(context.ClientId)
            .WithAuthority(context.Authority)
            .Build();

    private void BindCache(ITokenCache tokenCache, Guid connectionId)
    {
        tokenCache.SetBeforeAccessAsync(async args =>
        {
            var bytes = await _cacheStore.LoadAsync(connectionId, args.CancellationToken);
            if (bytes is { Length: > 0 })
                args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true);
        });
        tokenCache.SetAfterAccessAsync(async args =>
        {
            if (args.HasStateChanged)
            {
                await _cacheStore.SaveAsync(
                    connectionId,
                    args.TokenCache.SerializeMsalV3(),
                    args.CancellationToken);
            }
        });
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
