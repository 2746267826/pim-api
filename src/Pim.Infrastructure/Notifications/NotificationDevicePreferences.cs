using System.Text.Json;
using Pim.Core.Exceptions;
using Pim.Core.Notifications;

namespace Pim.Infrastructure.Notifications;

internal static class NotificationDeviceId
{
    public static string NormalizeOptional(string? value)
    {
        var deviceId = value?.Trim() ?? string.Empty;
        if (deviceId.Length == 0) return string.Empty;
        if (deviceId.Length > 160 ||
            deviceId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_' or '.' or ':')))
            throw new DomainException(40001, "设备标识无效");
        return deviceId;
    }

    public static string NormalizeRequired(string? value)
    {
        var deviceId = NormalizeOptional(value);
        if (deviceId.Length == 0) throw new DomainException(40001, "设备标识无效");
        return deviceId;
    }
}

internal sealed record NotificationDevicePreferenceOverride(
    bool PushEnabled,
    bool TaskCompleted,
    bool NoteTranscriptionCompleted,
    bool NoteDerivedCompleted,
    bool CalendarReminder,
    DateTimeOffset UpdatedAt);

internal static class NotificationDevicePreferences
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxDevices = 20;

    public static Dictionary<string, NotificationDevicePreferenceOverride> Read(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, NotificationDevicePreferenceOverride>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, NotificationDevicePreferenceOverride>>(
                       json, Json)
                   ?? new Dictionary<string, NotificationDevicePreferenceOverride>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, NotificationDevicePreferenceOverride>();
        }
    }

    public static string Write(
        IReadOnlyDictionary<string, NotificationDevicePreferenceOverride> overrides) =>
        JsonSerializer.Serialize(overrides, Json);

    public static bool CanAddDevice(
        IReadOnlyDictionary<string, NotificationDevicePreferenceOverride> overrides,
        string deviceId) =>
        overrides.ContainsKey(deviceId) || overrides.Count < MaxDevices;

    public static bool AllowsPush(
        string eventType,
        string deviceId,
        NotificationPreferenceEntity? account,
        IReadOnlyDictionary<string, NotificationDevicePreferenceOverride> overrides)
    {
        var device = overrides.GetValueOrDefault(deviceId);
        var pushEnabled = device?.PushEnabled ?? account?.PushEnabled ?? true;
        if (!pushEnabled) return false;
        return NotificationPreferencePolicy.IsCategoryEnabled(
            eventType,
            device?.TaskCompleted ?? account?.TaskCompleted ?? true,
            device?.NoteTranscriptionCompleted ?? account?.NoteTranscriptionCompleted ?? true,
            device?.NoteDerivedCompleted ?? account?.NoteDerivedCompleted ?? true,
            device?.CalendarReminder ?? account?.CalendarReminder ?? true);
    }
}

internal static class NotificationPreferencePolicy
{
    public static bool IsCategoryEnabled(
        string eventType,
        bool taskCompleted,
        bool noteTranscriptionCompleted,
        bool noteDerivedCompleted,
        bool calendarReminder) =>
        eventType switch
        {
            NotificationEventTypes.TaskCompleted => taskCompleted,
            NotificationEventTypes.CalendarReminderDue => calendarReminder,
            NotificationEventTypes.NoteTranscriptionCompleted => noteTranscriptionCompleted,
            NotificationEventTypes.NoteDerivedCompleted => noteDerivedCompleted,
            NotificationEventTypes.Test => true,
            _ => false
        };
}
