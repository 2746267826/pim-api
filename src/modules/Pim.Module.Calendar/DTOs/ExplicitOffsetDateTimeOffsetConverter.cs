using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pim.Module.Calendar.DTOs;

/// <summary>
/// R02：事件时间必须携带显式 UTC 偏移。System.Text.Json 默认把无偏移值按
/// 服务器本地时区解释，这会静默把机器时间当成用户时间；这里在绑定边界直接拒绝。
/// </summary>
public sealed class ExplicitOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("事件时间必须是带显式 UTC 偏移的 ISO-8601 字符串。");

        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw) || !HasExplicitOffset(raw))
        {
            throw new JsonException(
                "事件时间必须包含显式 UTC 偏移，例如 2026-09-01T09:00:00Z 或 2026-09-01T17:00:00+08:00；服务端不会按机器时区猜测。");
        }

        if (!DateTimeOffset.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new JsonException("事件时间必须是合法的 ISO-8601 时间。");
        }

        return parsed;
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value);

    internal static bool HasExplicitOffset(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith('Z') || trimmed.EndsWith('z'))
            return true;

        var timeStart = trimmed.IndexOfAny(['T', 't', ' ']);
        if (timeStart < 0)
            return false;

        var timePart = trimmed[(timeStart + 1)..];
        return timePart.Contains('+') || timePart.Contains('-');
    }
}
