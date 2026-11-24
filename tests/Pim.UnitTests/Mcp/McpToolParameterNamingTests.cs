using System.Text.RegularExpressions;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// R01: camelCase is the only supported MCP argument shape. The 14 snake_case
/// aliases that #573 normalized for compatibility are removed without a
/// compatibility window: the contract no longer advertises them, and the executor
/// rejects them with 400 plus the camelCase replacement. <c>get_tasks.status</c>
/// remains a separate deprecated single-value alias (not snake_case).
/// </summary>
public sealed class McpToolParameterNamingTests
{
    private sealed record RemovedAlias(
        string Tool,
        string Canonical,
        string Legacy,
        bool CanonicalRequired);

    private static readonly RemovedAlias[] RemovedAliases =
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
                if (!Regex.IsMatch(property.Name, "^[a-z][a-zA-Z0-9]*$"))
                    violations.Add($"{tool.Name}.{property.Name}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "以下参数不是 camelCase：" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Contract_DoesNotAdvertiseRemovedSnakeCaseAliases()
    {
        var byName = McpToolExecutor.ToolContract.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var expected in RemovedAliases)
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

            if (!properties.TryGetProperty(expected.Canonical, out _))
                failures.Add($"{expected.Tool}: canonical property {expected.Canonical} missing");

            if (properties.TryGetProperty(expected.Legacy, out _))
                failures.Add($"{expected.Tool}: removed alias {expected.Legacy} is still advertised");

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

            if (tool.Description.Contains(expected.Legacy, StringComparison.Ordinal)
                || tool.Description.Contains("Deprecated compatibility alias", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{expected.Tool}: description still documents removed alias {expected.Legacy}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "MCP 契约仍声明已删除的 snake_case 别名：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Contract_HasNoSnakeCaseOrUnexpectedDeprecatedProperties()
    {
        var properties = McpToolExecutor.ToolContract
            .SelectMany(tool => tool.InputSchema.TryGetProperty("properties", out var props)
                ? props.EnumerateObject().Select(property => (Tool: tool.Name, Property: property))
                : [])
            .ToList();

        var snakeCase = properties
            .Where(p => p.Property.Name.Contains('_'))
            .Select(p => $"{p.Tool}.{p.Property.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(snakeCase);

        // status 是 get_tasks 的单值筛选别名，不是 R01 的 snake_case 兼容层；
        // 除它之外不应再有 deprecated 参数。
        var deprecated = properties
            .Where(p => p.Property.Value.TryGetProperty("deprecated", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(p => $"{p.Tool}.{p.Property.Name}")
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(new[] { "get_tasks.status" }, deprecated);
    }

    [Theory]
    [InlineData("report_id", "reportId")]
    [InlineData("task_id", "taskId")]
    [InlineData("event_id", "eventId")]
    [InlineData("habit_id", "habitId")]
    [InlineData("calendar_id", "calendarId")]
    [InlineData("note_id", "noteId")]
    [InlineData("attachment_id", "attachmentId")]
    [InlineData("file_id", "fileId")]
    [InlineData("from_time", "fromTime")]
    public void RemovedAlias_IsRejectedWithCamelCaseReplacement(string legacy, string canonical)
    {
        var error = McpToolInputPolicy.RejectLegacyAliases([legacy]);

        Assert.Equal(400, error!["code"]!.GetValue<int>());
        var message = error["error"]!.GetValue<string>();
        Assert.Contains(legacy, message);
        Assert.Contains(canonical, message);
    }

    [Fact]
    public void RemovedAliases_CoverAll14ParameterPairs()
    {
        Assert.Equal(14, RemovedAliases.Length);
        Assert.Equal(9, RemovedAliases.Select(alias => alias.Legacy).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            RemovedAliases.Select(alias => alias.Tool).Distinct(StringComparer.Ordinal).Count(),
            RemovedAliases.Length);
    }

    [Fact]
    public void GetHabitOccurrences_ToolTableRequiredMatchesContract()
    {
        var tool = McpToolExecutor.ToolContract.Single(t => t.Name == "get_habit_occurrences");
        var contractRequired = tool.InputSchema.GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString());
        var tableRequired = McpToolTable.TryGet(tool.Name)!.Required;

        Assert.Equal(contractRequired, tableRequired);
    }
}
