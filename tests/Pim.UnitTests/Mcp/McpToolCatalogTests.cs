using System.Linq;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void Catalog_Has101ReadAnd50Write()
    {
        Assert.Equal(101, McpToolCatalog.ReadTools.Count);
        Assert.Equal(50, McpToolCatalog.WriteTools.Count);
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
        Assert.Equal(101, permissions["read"].Count);
        Assert.Equal(50, permissions["write"].Count);
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
