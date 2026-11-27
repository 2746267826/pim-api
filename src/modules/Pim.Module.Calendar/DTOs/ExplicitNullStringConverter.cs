using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pim.Module.Calendar.DTOs;

/// <summary>
/// Keeps JSON presence information for nullable strings: a missing property leaves
/// the CLR default (<c>null</c>), while an explicit JSON <c>null</c> becomes a
/// marker that is converted back to <c>null</c> after validation.
/// </summary>
public sealed class ExplicitNullStringConverter : JsonConverter<string?>
{
    private const string ExplicitNullMarker = "\0pim-explicit-null";

    public override bool HandleNull => true;

    public static bool IsExplicitNull(string? value)
        => string.Equals(value, ExplicitNullMarker, StringComparison.Ordinal);

    public static string? ToValue(string? value)
        => IsExplicitNull(value) ? null : value;

    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return ExplicitNullMarker;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("时间字段必须是字符串或 null。");

        return reader.GetString();
    }

    public override void Write(
        Utf8JsonWriter writer,
        string? value,
        JsonSerializerOptions options)
    {
        if (IsExplicitNull(value) || value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
