using System.Text.Json.Nodes;

namespace Pim.Module.Mcp.Services;

public static class McpToolResponseProjection
{
    public static JsonNode? Apply(string toolName, JsonNode? response)
    {
        if (response is not JsonObject envelope || envelope.ContainsKey("error")
            || envelope["code"] is not JsonValue code || !code.TryGetValue<int>(out var status) || status != 0
            || envelope["data"] is not JsonObject data)
            return response;

        if (toolName == "get_mobile_timeline" && IsCompletePartition(data))
        {
            var copy = envelope.DeepClone().AsObject();
            copy["data"]!.AsObject().Remove("sessions");
            copy["data"]!.AsObject().Remove("fallbackSummaries");
            return copy;
        }

        if (toolName == "get_pc_summary" && data["heatmap"] is JsonArray { Count: > 0 } heatmap
            && heatmap.All(IsEmptyBucket))
        {
            // This only describes the grid, not whether collection or other signals exist.
            var copy = envelope.DeepClone().AsObject();
            copy["data"]!["heatmap"] = new JsonArray();
            return copy;
        }

        return response;
    }

    private static bool IsCompletePartition(JsonObject data)
    {
        if (data["items"] is not JsonArray items || data["sessions"] is not JsonArray sessions
            || data["fallbackSummaries"] is not JsonArray fallback || items.Count != sessions.Count + fallback.Count)
            return false;

        var remaining = items.ToList();
        foreach (var item in sessions.Concat(fallback))
        {
            var index = remaining.FindIndex(candidate => JsonNode.DeepEquals(candidate, item));
            if (index < 0)
                return false;
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }

    private static bool IsEmptyBucket(JsonNode? bucket)
        => bucket is JsonObject obj && IsZero(obj["activeMinutes"]) && IsZero(obj["totalEvents"])
            && IsZero(obj["intensityLevel"]);

    private static bool IsZero(JsonNode? value)
        => value is JsonValue number && number.TryGetValue<double>(out var result) && result == 0;
}
