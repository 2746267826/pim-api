using System.Linq;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void Catalog_Has100ReadAnd49Write()
    {
        // P4：read_file_text 加入读工具（OneDrive 文件模块 v2，设计 §12）
        // WO-ANDROID-KEEPALIVE-20260923 阶段一：get_mobile_liveness_summary 加入读工具（REQ-11）
        // WO-PC-BACKEND-20261001 REQ-6（#379）：get_pc_aw_heatmap 随遗留端点 pc/aw/heatmap 下线，
        // 读工具 101 → 100（AC-6.3：工具表必须与最终行为一致）。
        Assert.Equal(100, McpToolCatalog.ReadTools.Count);
        Assert.Equal(49, McpToolCatalog.WriteTools.Count);
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
    public void DefaultPermissions_ReadAllOn_WriteAllOff()
    {
        var permissions = McpToolCatalog.DefaultPermissions();
        Assert.True(permissions["read"].All(kv => kv.Value));
        Assert.True(permissions["write"].All(kv => !kv.Value));
        // WO-PC-BACKEND-20261001 REQ-6：读工具随 get_pc_aw_heatmap 下线变为 100。
        Assert.Equal(100, permissions["read"].Count);
        Assert.Equal(49, permissions["write"].Count);
    }

    [Fact]
    public void IsWrite_And_Contains_Behave()
    {
        Assert.True(McpToolCatalog.IsWrite("create_task"));
        Assert.False(McpToolCatalog.IsWrite("get_tasks"));
        Assert.True(McpToolCatalog.Contains("get_tasks"));
        Assert.False(McpToolCatalog.Contains("bogus_tool"));
    }
}
