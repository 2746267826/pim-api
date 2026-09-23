using System.Linq;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// REQ-11：「设备存活」只读查询（REST + MCP）。
/// AC-11.2 要求 MCP 结果与 REST 一致——实现上通过让 MCP 工具直接走同一个 REST 端点来保证；
/// AC-11.3 要求 MCP 侧不具备写入能力。
/// </summary>
public sealed class McpLivenessToolTests
{
    private const string ToolName = "get_mobile_liveness_summary";

    [Fact]
    public void LivenessTool_IsADeclaredReadTool()
    {
        var tool = McpToolCatalog.ReadTools.SingleOrDefault(item => item.Name == ToolName);

        Assert.NotNull(tool);
        Assert.False(McpToolCatalog.IsWrite(ToolName));
        Assert.True(McpToolCatalog.Contains(ToolName));
    }

    [Fact]
    public void LivenessTool_DispatchesToTheSameRESTEndpointAsTheSummaryApi()
    {
        // AC-11.2：同一区间用 MCP 查询与 REST 一致 —— 两边必须是同一个端点与同一组参数。
        var spec = McpToolTable.TryGet(ToolName);

        Assert.NotNull(spec);
        Assert.Equal("GET", spec!.Method);
        Assert.Equal("/api/v1/mobile/devices/{deviceId}/liveness", spec.Route);
        Assert.Contains("deviceId", spec.Route);
        Assert.Contains("rangeStartUtc", spec.QueryParams);
        Assert.Contains("rangeEndUtc", spec.QueryParams);
    }

    [Fact]
    public void LivenessTool_IsAllowedByTheReadOnlyEndpointPolicy()
    {
        // AC-11.3：只读策略下该路径可用；写入类方法一律被拒。
        var spec = McpToolTable.TryGet(ToolName)!;

        Assert.True(McpReadEndpointPolicy.IsReadAllowed(spec.Method, "/api/v1/mobile/devices/android-phone/liveness"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/mobile/devices/android-phone/liveness"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("DELETE", "/api/v1/mobile/devices/android-phone/liveness"));
        Assert.False(McpReadEndpointPolicy.IsReadAllowed("POST", "/api/v1/mobile/forensics/events"));
    }

    [Fact]
    public void LivenessTool_IsPresentInTheWireContractWithMatchingSchema()
    {
        var contract = McpToolExecutor.ToolContract.Single(tool => tool.Name == ToolName);
        var properties = contract.InputSchema.GetProperty("properties");

        Assert.Contains("deviceId", properties.EnumerateObject().Select(property => property.Name));
        Assert.Contains("rangeStartUtc", properties.EnumerateObject().Select(property => property.Name));
        Assert.Contains("rangeEndUtc", properties.EnumerateObject().Select(property => property.Name));

        // 契约 schema 的参数集合必须与工具表一致（否则调用方传了却被丢弃）。
        var spec = McpToolTable.TryGet(ToolName)!;
        Assert.Equal(
            spec.QueryParams.Append("deviceId").OrderBy(name => name, System.StringComparer.Ordinal),
            properties.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => name != "redactUrls")
                .OrderBy(name => name, System.StringComparer.Ordinal));
    }

    [Fact]
    public void LivenessTool_HasANonEmptyDescriptionInTheWireContract()
    {
        var contract = McpToolExecutor.ToolContract.Single(tool => tool.Name == ToolName);
        Assert.False(string.IsNullOrWhiteSpace(contract.Description));
    }
}
