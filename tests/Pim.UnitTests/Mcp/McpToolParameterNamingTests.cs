using System.Text.RegularExpressions;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// WO-R01-CAMEL / issue #460: camelCase is the canonical MCP parameter shape.
/// Legacy snake_case names stay callable as deprecated aliases, but only the
/// camelCase names are advertised as required.
/// </summary>
public sealed class McpToolParameterNamingTests
{
    private sealed record AliasExpectation(
        string Tool,
        string Canonical,
        string Legacy,
        bool CanonicalRequired);

    private static readonly AliasExpectation[] ExpectedAliases =
    [
        new("get_report", "reportId", "report_id", true),
        new("get_task_segments", "taskId", "task_id", true),
        new("get_event_by_id", "eventId", "event_id", true),
        new("get_task_by_id", "taskId", "task_id", true),
        new("get_habit_occurrences", "habitId", "habit_id", true),
        new("get_calendar_by_id", "calendarId", "calendar_id", true),
        new("get_task_checklist", "taskId", "task_id", true),
        new("get_quick_note", "noteId", "note_id", true),
        new("get_quick_note_attachment_meta", "attachmentId", "attachment_id", true),
        new("get_file", "fileId", "file_id", true),
        new("read_file_text", "fileId", "file_id", true),
        new("get_file_open_link", "fileId", "file_id", true),
        new("get_ai_requests", "fromTime", "from_time", false),
        new("get_ai_usage_summary", "fromTime", "from_time", true),
    ];

    [Fact]
    public void Contract_CanonicalParametersAreCamelCase()
    {
        var violations = new List<string>();

        foreach (var tool in McpToolExecutor.ToolContract)
        {
            if (!tool.InputSchema.TryGetProperty("properties", out var properties))
                continue;

            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.TryGetProperty("deprecated", out var deprecated)
                    && deprecated.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    continue;
                }

                if (!Regex.IsMatch(property.Name, "^[a-z][a-zA-Z0-9]*$"))
                    violations.Add($"{tool.Name}.{property.Name}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "以下非兼容参数不是 camelCase：" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Contract_LegacyAliasesAreDeprecatedOptionalAndDocumented()
    {
        var byName = McpToolExecutor.ToolContract.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var expected in ExpectedAliases)
        {
            if (!byName.TryGetValue(expected.Tool, out var tool))
            {
                failures.Add($"{expected.Tool}: tool missing");
                continue;
            }

            if (!tool.InputSchema.TryGetProperty("properties", out var properties))
            {
                failures.Add($"{expected.Tool}: properties missing");
                continue;
            }

            if (!properties.TryGetProperty(expected.Canonical, out var canonical))
                failures.Add($"{expected.Tool}: canonical property {expected.Canonical} missing");

            if (!properties.TryGetProperty(expected.Legacy, out var legacy))
            {
                failures.Add($"{expected.Tool}: deprecated alias {expected.Legacy} missing");
                continue;
            }

            if (!legacy.TryGetProperty("deprecated", out var deprecated)
                || deprecated.ValueKind != System.Text.Json.JsonValueKind.True)
            {
                failures.Add($"{expected.Tool}.{expected.Legacy}: deprecated=true missing");
            }

            var required = tool.InputSchema.TryGetProperty("required", out var requiredNode)
                && requiredNode.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? requiredNode.EnumerateArray().Select(v => v.GetString()).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string?>(StringComparer.Ordinal);

            if (required.Contains(expected.Legacy))
                failures.Add($"{expected.Tool}: required still advertises {expected.Legacy}");
            if (expected.CanonicalRequired && !required.Contains(expected.Canonical))
                failures.Add($"{expected.Tool}: required does not advertise {expected.Canonical}");
            if (!expected.CanonicalRequired && required.Contains(expected.Canonical))
                failures.Add($"{expected.Tool}: {expected.Canonical} must remain optional");

            if (!tool.Description.Contains(expected.Legacy, StringComparison.Ordinal)
                || !tool.Description.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{expected.Tool}: description does not document deprecated alias {expected.Legacy}");
            }
        }

        var deprecatedProperties = McpToolExecutor.ToolContract
            .SelectMany(tool => tool.InputSchema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject()
                    .Where(property => property.Value.TryGetProperty("deprecated", out var deprecated)
                        && deprecated.ValueKind == System.Text.Json.JsonValueKind.True)
                    .Select(property => $"{tool.Name}.{property.Name}")
                : [])
            .ToList();

        Assert.Equal(ExpectedAliases.Length, deprecatedProperties.Count);

        Assert.True(
            failures.Count == 0,
            "MCP 兼容别名契约不符合要求：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }
}
