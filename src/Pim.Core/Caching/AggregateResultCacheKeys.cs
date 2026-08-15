using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Pim.Core.Caching;

public static class AggregateResultCacheKeys
{
    public static string Build(
        HttpRequest request,
        string excludedQueryParam = "force",
        IReadOnlyList<KeyValuePair<string, string>>? overrides = null)
    {
        var pairs = request.Query
            .Where(pair => !string.Equals(pair.Key, excludedQueryParam, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => (Key: pair.Key, Value: value ?? string.Empty)));

        if (overrides is not null)
        {
            pairs = pairs.Where(pair => !overrides.Any(overridePair =>
                    string.Equals(pair.Key, overridePair.Key, StringComparison.OrdinalIgnoreCase)))
                .Concat(overrides.Select(pair => (pair.Key, Value: pair.Value ?? string.Empty)));
        }

        var normalized = pairs
            .OrderBy(pair => pair.Key, StringComparer.InvariantCulture)
            .ThenBy(pair => pair.Value, StringComparer.InvariantCulture)
            .Select(pair => string.Concat(
                Uri.EscapeDataString(pair.Key),
                "=",
                Uri.EscapeDataString(pair.Value)));

        var query = string.Join("&", normalized);
        var key = string.IsNullOrEmpty(query)
            ? request.Path.Value ?? string.Empty
            : string.Concat(request.Path.Value, "?", query);

        // 未认证请求统一归入 anon 桶。pc 读端点匿名开放且数据全局（非用户级），共享 anon 桶安全；
        // 认证端点（mobile/today）按 ClaimTypes.NameIdentifier 隔离，避免跨用户串数据。
        var userId = request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anon";
        return string.Concat("u:", Uri.EscapeDataString(userId), "|", key);
    }
}
