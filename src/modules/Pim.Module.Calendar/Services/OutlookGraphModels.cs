namespace Pim.Module.Calendar.Services;

public interface IMicrosoftGraphClient
{
    Task<DeviceCodeResult> RequestDeviceCodeAsync(
        string tenant,
        string clientId,
        string scopes,
        CancellationToken ct);

    Task<TokenResult> PollDeviceCodeAsync(
        string tenant,
        string clientId,
        string deviceCode,
        CancellationToken ct);

    Task<TokenResult> RefreshAsync(
        string tenant,
        string clientId,
        string refreshToken,
        string scopes,
        CancellationToken ct);

    Task<GraphDeltaPage> GetDeltaPageAsync(
        string accessToken,
        string url,
        CancellationToken ct);

    Task<GraphEvent> PatchEventAsync(
        string accessToken,
        string eventId,
        string changeKey,
        object patch,
        CancellationToken ct);
}

public sealed record DeviceCodeResult(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string Message,
    int ExpiresInSeconds);

public sealed record TokenResult(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    string Scopes);

public sealed record GraphDeltaPage(
    IReadOnlyList<GraphEvent> Events,
    string? NextLink,
    string? DeltaLink);

public sealed record GraphEvent(
    string Id,
    string Subject,
    string? BodyPreview,
    GraphDateTimeTimeZone Start,
    GraphDateTimeTimeZone End,
    string? LastModifiedDateTime,
    string? ICalUId,
    string? ChangeKey,
    string? ETag,
    string? Location,
    string? WebLink);

public sealed record GraphDateTimeTimeZone(
    string DateTime,
    string? TimeZone);
