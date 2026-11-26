using System.Text.Json;

namespace Pim.Infrastructure.Notifications;

/// <summary>
/// Persists the subscriptions that were eligible when the event was created. This keeps
/// "disabled while the event happened" from replaying after a device is re-enabled.
/// </summary>
internal static class NotificationEventSnapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Write(IEnumerable<Guid> subscriptionIds) =>
        JsonSerializer.Serialize(
            subscriptionIds.Distinct().OrderBy(id => id).ToArray(),
            Json);

    /// <summary>
    /// Null means a legacy row predating the snapshot column; an empty array means the event
    /// was intentionally created without any eligible push subscription.
    /// </summary>
    public static Guid[]? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
