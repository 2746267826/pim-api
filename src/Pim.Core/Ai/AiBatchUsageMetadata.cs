using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pim.Core.Ai;

/// <summary>
/// Durable Batch usage marker stored in <c>ai_request_logs.metadata_json</c>. A reconciled Batch
/// always reports the confirmed successful-item token sum; this marker makes the unconfirmed
/// remainder explicit instead of silently treating it as zero or as an open-ended unknown.
/// </summary>
public static class AiBatchUsageMetadata
{
    public const string Key = "batchUsage";
    public const string RetryDepthKey = "batchRetryDepth";
    /// <summary>
    /// Terminal Batch with no item reporting a usable usage object; the whole submission stays
    /// token-unknown, closes the connection limit and is not re-scanned by the repair page.
    /// </summary>
    public const string UnavailableCode = "batch_usage_unavailable";

    public readonly record struct Snapshot(int ReportedItems, int FailedItems, int UnconfirmedItems,
        long ConfirmedTokens)
    {
        public bool Partial => FailedItems > 0 || UnconfirmedItems > 0;
    }

    public static string Write(string? metadata, Snapshot snapshot)
    {
        var root = ParseObject(metadata);
        root[Key] = new JsonObject
        {
            ["reportedItems"] = snapshot.ReportedItems,
            ["failedItems"] = snapshot.FailedItems,
            ["unconfirmedItems"] = snapshot.UnconfirmedItems,
            ["confirmedTokens"] = snapshot.ConfirmedTokens,
            ["partial"] = snapshot.Partial
        };
        return root.ToJsonString();
    }

    public static string Clear(string? metadata)
    {
        var root = ParseObject(metadata);
        root.Remove(Key);
        return root.ToJsonString();
    }

    public static bool TryRead(string? metadata, out Snapshot snapshot)
    {
        snapshot = default;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadata) ? "{}" : metadata);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(Key, out var marker) ||
                marker.ValueKind != JsonValueKind.Object ||
                !TryInt(marker, "reportedItems", out var reported) ||
                !TryInt(marker, "failedItems", out var failed) ||
                !TryInt(marker, "unconfirmedItems", out var unconfirmed))
                return false;
            var confirmedTokens = marker.TryGetProperty("confirmedTokens", out var confirmed) &&
                                  confirmed.ValueKind == JsonValueKind.Number &&
                                  confirmed.TryGetInt64(out var value) && value >= 0
                ? value
                : 0;
            snapshot = new Snapshot(reported, failed, unconfirmed, confirmedTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static int ReadRetryDepth(string? metadata)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadata) ? "{}" : metadata);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(RetryDepthKey, out var depth) &&
                   depth.ValueKind == JsonValueKind.Number && depth.TryGetInt32(out var value) && value >= 0
                ? value
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value) && value >= 0;
    }

    private static JsonObject ParseObject(string? metadata)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(metadata) ? "{}" : metadata)
                as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
