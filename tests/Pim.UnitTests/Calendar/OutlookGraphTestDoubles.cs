using System.Text;
using System.Text.Json;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.Services;

namespace Pim.UnitTests.Calendar;

internal sealed class FakeMicrosoftGraphClient : IMicrosoftGraphClient
{
    public DeviceCodeResult DeviceCode { get; set; } = new(
        "device-code",
        "USER-CODE",
        "https://www.microsoft.com/link",
        "Open link.",
        900);

    public TokenResult Token { get; set; } = new(
        "access-token",
        "refresh-token",
        3600,
        "Calendars.ReadWrite offline_access");

    public Queue<GraphDeltaPage> DeltaPages { get; } = new();

    public List<PatchRequest> PatchRequests { get; } = [];

    public Task<DeviceCodeResult> RequestDeviceCodeAsync(
        string tenant,
        string clientId,
        string scopes,
        CancellationToken ct)
        => Task.FromResult(DeviceCode);

    public Task<TokenResult> PollDeviceCodeAsync(
        string tenant,
        string clientId,
        string deviceCode,
        CancellationToken ct)
        => Task.FromResult(Token);

    public Task<TokenResult> RefreshAsync(
        string tenant,
        string clientId,
        string refreshToken,
        string scopes,
        CancellationToken ct)
        => Task.FromResult(Token);

    public Task<GraphDeltaPage> GetDeltaPageAsync(
        string accessToken,
        string url,
        CancellationToken ct)
        => Task.FromResult(DeltaPages.Count == 0
            ? new GraphDeltaPage([], null, "delta-link")
            : DeltaPages.Dequeue());

    public Task<GraphEvent> PatchEventAsync(
        string accessToken,
        string eventId,
        string changeKey,
        object patch,
        CancellationToken ct)
    {
        PatchRequests.Add(new PatchRequest(eventId, changeKey, JsonSerializer.Serialize(patch)));
        return Task.FromResult(GraphEventFactory.Create(eventId, "Patched", changeKey: "patched-change"));
    }

    public sealed record PatchRequest(string EventId, string ChangeKey, string Body);
}

internal static class GraphEventFactory
{
    public static GraphEvent Create(
        string id,
        string subject,
        string? location = null,
        string changeKey = "change-key")
        => new(
            id,
            subject,
            "Preview",
            new GraphDateTimeTimeZone("2026-07-08T09:00:00Z", "UTC"),
            new GraphDateTimeTimeZone("2026-07-08T10:00:00Z", "UTC"),
            "2026-07-08T01:00:00Z",
            "ical-" + id,
            changeKey,
            "etag-" + id,
            location ?? "Room A",
            null);
}

internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    public string Unprotect(string protectedValue)
        => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["protected:".Length..]));
}
