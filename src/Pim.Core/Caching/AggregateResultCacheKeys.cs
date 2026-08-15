using Microsoft.AspNetCore.Http;

namespace Pim.Core.Caching;

public static class AggregateResultCacheKeys
{
    public static string Build(HttpRequest request, string excludedQueryParam = "force")
    {
        var pairs = request.Query
            .Where(pair => !string.Equals(pair.Key, excludedQueryParam, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => (Key: pair.Key, Value: value ?? string.Empty)))
            .OrderBy(pair => pair.Key, StringComparer.InvariantCulture)
            .ThenBy(pair => pair.Value, StringComparer.InvariantCulture)
            .Select(pair => string.Concat(
                Uri.EscapeDataString(pair.Key),
                "=",
                Uri.EscapeDataString(pair.Value)));

        var query = string.Join("&", pairs);
        return string.IsNullOrEmpty(query)
            ? request.Path.Value ?? string.Empty
            : string.Concat(request.Path.Value, "?", query);
    }
}
