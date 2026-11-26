using System.Linq;
using System.Text.Json;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void Catalog_Has108ReadAnd60Write()
    {
        // P4：read_file_text 加入读工具（OneDrive 文件模块 v2，设计 §12）
        // WO-ANDROID-KEEPALIVE-20260923 阶段一：get_mobile_liveness_summary 加入读工具（REQ-11）
        // WO-PC-BACKEND-20261001 REQ-6（#379）：get_pc_aw_heatmap 随遗留端点 pc/aw/heatmap 下线，
        // 读工具 101 → 100（AC-6.3：工具表必须与最终行为一致）；R05 增加 8 读 + 11 写。
        Assert.Equal(108, McpToolCatalog.ReadTools.Count);
        Assert.Equal(60, McpToolCatalog.WriteTools.Count);
    }

    /// <summary>
    /// WO-PC-BACKEND-20261001 · AC-6.3：AW 退役后的 <c>pc/aw/heatmap</c> 一律返回 0，
    /// 与 <c>summary.heatmap</c> 同名不同源；该端点下线后，MCP 工具表里不得再登记它，
    /// 否则调用方会拿到一份「看起来正常、其实全是 0」的数据。
    /// </summary>
    [Fact]
    public void Catalog_NoLongerExposesTheRetiredAwHeatmapTool()
    {
        Assert.DoesNotContain("get_pc_aw_heatmap", McpToolCatalog.ReadTools.Select(t => t.Name));
        Assert.DoesNotContain("get_pc_aw_heatmap", McpToolCatalog.WriteTools.Select(t => t.Name));
        Assert.False(McpToolTable.All.ContainsKey("get_pc_aw_heatmap"));

        // 替代端点仍在册：下线的是 AW 遗留口径，不是热力图能力本身。
        Assert.Contains("get_pc_heatmap", McpToolCatalog.ReadTools.Select(t => t.Name));
        Assert.True(McpToolTable.All.ContainsKey("get_pc_heatmap"));
    }

    [Fact]
    public void WriteTools_CoverExpectedModules()
    {
        var names = McpToolCatalog.WriteTools.Select(t => t.Name).ToHashSet();
        Assert.Contains("create_event", names);
        Assert.Contains("create_task", names);
        Assert.Contains("create_reminder", names);
        Assert.Contains("create_quick_note", names);
        Assert.Contains("upload_file", names);
        Assert.Contains("create_category", names);
        Assert.Contains("create_mobile_goal", names);
    }

    [Fact]
    public void Catalog_NamesAreUniqueAndDisjoint()
    {
        var read = McpToolCatalog.ReadTools.Select(t => t.Name).ToList();
        var write = McpToolCatalog.WriteTools.Select(t => t.Name).ToList();
        Assert.Equal(read.Count, read.Distinct().Count());
        Assert.Equal(write.Count, write.Distinct().Count());
        Assert.Empty(read.Intersect(write));
    }

    [Fact]
    public void AllWriteTools_AreFlaggedIsWrite_AndReadAreNot()
    {
        Assert.All(McpToolCatalog.WriteTools, t => Assert.True(t.IsWrite));
        Assert.All(McpToolCatalog.ReadTools, t => Assert.False(t.IsWrite));
    }

    [Fact]
    public void DefaultPermissions_OrdinaryReadsOn_NativeNoteReadsAndWritesOff()
    {
        var permissions = McpToolCatalog.DefaultPermissions();
        Assert.True(permissions["read"]["get_tasks"]);
        Assert.True(permissions["read"]["get_events"]);
        Assert.True(permissions["read"]["get_quick_notes"]);
        Assert.All(McpToolCatalog.ReadTools.Where(tool => tool.Group == "native-notes"),
            tool => Assert.False(permissions["read"][tool.Name], tool.Name));
        Assert.True(permissions["write"].All(kv => !kv.Value));
        Assert.Equal(108, permissions["read"].Count);
        Assert.Equal(60, permissions["write"].Count);
        Assert.False(McpToolCatalog.IsEnabledByDefault("get_native_note"));
        Assert.False(McpToolCatalog.IsEnabledByDefault("get_native_notes"));
        Assert.True(McpToolCatalog.IsEnabledByDefault("get_tasks"));
    }

    [Fact]
    public void IsWrite_And_Contains_Behave()
    {
        Assert.True(McpToolCatalog.IsWrite("create_task"));
        Assert.False(McpToolCatalog.IsWrite("get_tasks"));
        Assert.True(McpToolCatalog.Contains("get_tasks"));
        Assert.False(McpToolCatalog.Contains("bogus_tool"));
    }

    [Fact]
    public void NativeNoteTools_AreCataloguedMappedAndPartitioned()
    {
        var reads = new[]
        {
            "get_native_notes", "get_native_note", "get_native_note_versions",
            "get_native_note_results", "get_native_note_processing",
            "get_native_note_operation",
            "get_native_note_package", "get_native_note_suggestions",
        };
        var writes = new[]
        {
            "upload_native_note_package", "promote_native_note_version",
            "cancel_native_note_upload", "restore_native_note",
            "delete_native_note", "start_native_note_processing",
            "retry_native_note_processing", "adopt_native_note_result",
            "extract_native_note_suggestions", "confirm_native_note_suggestion",
            "dismiss_native_note_suggestion",
        };

        foreach (var name in reads)
        {
            Assert.Contains(McpToolCatalog.ReadTools, tool => tool.Name == name);
            Assert.False(McpToolCatalog.IsWrite(name));
            var spec = McpToolTable.TryGet(name)!;
            Assert.Equal("GET", spec.Method);
            Assert.StartsWith("/api/v1/native-notes/mcp/", spec.Route);
            var contract = McpToolExecutor.ToolContract.Single(tool => tool.Name == name);
            Assert.Equal("native-notes",
                McpToolCatalog.ReadTools.Single(tool => tool.Name == name).Group);
            Assert.Equal(JsonValueKind.Object, contract.InputSchema.ValueKind);
        }

        foreach (var name in writes)
        {
            Assert.Contains(McpToolCatalog.WriteTools, tool => tool.Name == name);
            Assert.True(McpToolCatalog.IsWrite(name));
            var spec = McpToolTable.TryGet(name)!;
            Assert.Equal("POST", spec.Method);
            Assert.StartsWith("/api/v1/native-notes/mcp/", spec.Route);
            Assert.True(McpWriteEndpointMap.IsAllowedForTool(name, spec.Method,
                System.Text.RegularExpressions.Regex.Replace(spec.Route, @"\{[^}]+\}", "dummy-id")));
            var contract = McpToolExecutor.ToolContract.Single(tool => tool.Name == name);
            var required = contract.InputSchema.GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(spec.Required.OrderBy(item => item, StringComparer.Ordinal),
                required.OrderBy(item => item, StringComparer.Ordinal));
            Assert.Equal("native-notes",
                McpToolCatalog.WriteTools.Single(tool => tool.Name == name).Group);
        }

        // Recycle and restore remain distinct endpoints, so their token scopes cannot overlap.
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("delete_native_note", "POST",
            "/api/v1/native-notes/mcp/notes/x/recycle"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("delete_native_note", "POST",
            "/api/v1/native-notes/mcp/notes/x/restore"));
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("restore_native_note", "POST",
            "/api/v1/native-notes/mcp/notes/x/restore"));
        Assert.False(McpToolTable.All.ContainsKey("archive_native_note"));
    }

    [Fact]
    public void NativeNoteReadRoutes_AreBoundToExactlyOneTool()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["get_native_notes"] = "/api/v1/native-notes/mcp/notes",
            ["get_native_note"] = "/api/v1/native-notes/mcp/notes/note-1",
            ["get_native_note_versions"] = "/api/v1/native-notes/mcp/notes/note-1/versions",
            ["get_native_note_results"] = "/api/v1/native-notes/mcp/notes/note-1/results",
            ["get_native_note_processing"] = "/api/v1/native-notes/mcp/notes/note-1/processing",
            ["get_native_note_operation"] = "/api/v1/native-notes/mcp/notes/note-1/operations/request-1",
            ["get_native_note_package"] = "/api/v1/native-notes/mcp/notes/note-1/package",
            ["get_native_note_suggestions"] = "/api/v1/native-notes/mcp/notes/note-1/suggestions",
        };

        foreach (var (tool, path) in expected)
        {
            Assert.True(McpReadEndpointMap.IsAllowedForTool(tool, "GET", path), $"{tool} -> {path}");
            foreach (var other in expected.Keys.Where(other => other != tool))
                Assert.False(McpReadEndpointMap.IsAllowedForTool(other, "GET", path),
                    $"{other} must not read {path}");
        }

        Assert.False(McpReadEndpointMap.IsAllowedForTool("get_tasks", "GET",
            "/api/v1/native-notes/mcp/notes"));
        Assert.False(McpReadEndpointMap.IsAllowedForTool("get_native_note", "GET",
            "/api/v1/native-notes/versions/version-1"));
        Assert.False(McpReadEndpointMap.IsAllowedForTool("get_native_note", "POST",
            "/api/v1/native-notes/mcp/notes/note-1"));
    }
}
