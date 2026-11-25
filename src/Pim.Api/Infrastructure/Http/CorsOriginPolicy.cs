namespace Pim.Api.Infrastructure.Http;

public sealed record CorsOriginFilterResult(
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> RejectedOrigins);

/// <summary>
/// 跨域白名单只接受 HTTPS 源；本机回环 HTTP 保留为开发例外。
/// Web 同源部署不需要 CORS，跨域浏览器访问必须走 HTTPS。
/// </summary>
public static class CorsOriginPolicy
{
    private static readonly HashSet<string> LoopbackHosts =
        new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1", "[::1]" };

    public static CorsOriginFilterResult Filter(string? raw)
    {
        var allowed = new List<string>();
        var rejected = new List<string>();
        foreach (var token in (raw ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(token, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length > 0
                || uri.AbsolutePath != "/"
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
            {
                rejected.Add(token);
                continue;
            }

            var loopback = LoopbackHosts.Contains(uri.Host);
            if (uri.Scheme == Uri.UriSchemeHttp && !loopback)
            {
                rejected.Add(token);
                continue;
            }

            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                allowed.Add(origin);
            }
        }

        return new CorsOriginFilterResult(allowed, rejected);
    }
}
