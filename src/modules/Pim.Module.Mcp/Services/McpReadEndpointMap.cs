using System.Text.RegularExpressions;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// Exact native-note GET route ownership. A read-tool token may only access the route that
/// belongs to its own tool; a generic read token (for example get_tasks) cannot reuse the
/// broad "all GETs are readable" policy to read native notes.
/// </summary>
public static class McpReadEndpointMap
{
    private static readonly Dictionary<string, Regex[]> Map = Build();

    public static bool IsNativeNotePath(string path)
        => path.Equals("/api/v1/native-notes", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/api/v1/native-notes/", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedForTool(string tool, string method, string path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Map.TryGetValue(tool, out var patterns)) return false;
        var normalized = path.TrimEnd('/');
        return patterns.Any(pattern => pattern.IsMatch(normalized));
    }

    private static Dictionary<string, Regex[]> Build()
    {
        var map = new Dictionary<string, Regex[]>(StringComparer.Ordinal);
        Add(map, "get_native_notes", "/api/v1/native-notes/mcp/notes");
        Add(map, "get_native_note", "/api/v1/native-notes/mcp/notes/{id}");
        Add(map, "get_native_note_versions", "/api/v1/native-notes/mcp/notes/{id}/versions");
        Add(map, "get_native_note_results", "/api/v1/native-notes/mcp/notes/{id}/results");
        Add(map, "get_native_note_processing", "/api/v1/native-notes/mcp/notes/{id}/processing");
        Add(map, "get_native_note_operation",
            "/api/v1/native-notes/mcp/notes/{id}/operations/{requestId}");
        Add(map, "get_native_note_package", "/api/v1/native-notes/mcp/notes/{id}/package");
        Add(map, "get_native_note_suggestions",
            "/api/v1/native-notes/mcp/notes/{id}/suggestions");
        return map;
    }

    private static void Add(Dictionary<string, Regex[]> map, string tool, string pathTemplate)
    {
        var regex = "^" + Regex.Replace(Regex.Escape(pathTemplate), @"\\\{[^}]+\\\}",
            "[^/]+") + "$";
        map[tool] =
        [
            new Regex(regex, RegexOptions.Compiled | RegexOptions.IgnoreCase |
                             RegexOptions.CultureInvariant)
        ];
    }
}
