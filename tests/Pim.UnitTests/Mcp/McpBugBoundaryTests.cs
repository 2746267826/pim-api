using System.Text.Json.Nodes;
using Pim.Module.Calendar.Entities;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpBugBoundaryTests
{
    [Theory]
    [InlineData("NEEDS-ACTION,IN-PROCESS")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("in-process")]
    public void TaskStatus_InvalidValueIsAnError_NotAnEmptyQuery(string status)
    {
        var error = McpToolInputPolicy.Apply("get_tasks", new JsonObject { ["status"] = status });
        Assert.Equal(400, error!["code"]!.GetValue<int>());
        Assert.Contains("one value only", error["error"]!.GetValue<string>());
    }

    [Fact]
    public void TaskStatus_ContractAndValidationMatchServerValues()
    {
        var schema = McpToolExecutor.ToolContract.Single(t => t.Name == "get_tasks").InputSchema;
        var statuses = schema.GetProperty("properties").GetProperty("status").GetProperty("anyOf")[0]
            .GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(TaskStatuses.All, statuses);
        foreach (var status in TaskStatuses.All)
            Assert.Null(McpToolInputPolicy.Apply("get_tasks", new JsonObject { ["status"] = status }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"start\":\"2026-10-01\"}")]
    [InlineData("{\"start\":null,\"end\":\"2026-10-02\"}")]
    [InlineData("{\"start\":\"bad\",\"end\":\"2026-10-02\"}")]
    public void Events_RejectUnboundedOrInvalidDates(string json)
        => Assert.Equal(400, McpToolInputPolicy.Apply("get_events", JsonNode.Parse(json)!.AsObject())!["code"]!.GetValue<int>());

    [Theory]
    [InlineData("get_tasks")]
    [InlineData("get_events")]
    public void Queries_ApplyPaginationEvenWhenCallerOmitsDefaults(string name)
    {
        var args = JsonNode.Parse("""{"start":"2026-10-01","end":"2026-10-02","page":null}""")!.AsObject();
        Assert.Null(McpToolInputPolicy.Apply(name, args));
        Assert.Equal(1, args["page"]!.GetValue<int>());
        Assert.Equal(20, args["pageSize"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("\"2\"")]
    [InlineData("1.2")]
    [InlineData("true")]
    [InlineData("2147483648")]
    public void Queries_RejectWrongPaginationTypes(string page)
        => Assert.NotNull(McpToolInputPolicy.Apply("get_tasks", JsonNode.Parse("{\"page\":" + page + "}")!.AsObject()));

    [Fact]
    public void Registry_RejectsSectionFilterAndPointsToTheDetailTool()
    {
        var error = McpToolInputPolicy.Apply("get_today_sections", new JsonObject { ["sectionId"] = "pc.activity" });
        Assert.Contains("get_today_section(", error!["error"]!.GetValue<string>());
        Assert.Null(McpToolInputPolicy.Apply("get_today_section", new JsonObject { ["sectionId"] = "pc.activity" }));
    }

    [Fact]
    public void CategoryResource_IsExplicitAndPermissionsStayToolScoped()
    {
        Assert.Null(McpToolInputPolicy.Apply("delete_category", new JsonObject { ["resourceType"] = "appRule" }));
        Assert.Null(McpToolInputPolicy.Apply("delete_category", new JsonObject()));
        Assert.NotNull(McpToolInputPolicy.Apply("delete_category", new JsonObject { ["resourceType"] = "guess" }));
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("delete_category", "DELETE", "/api/v1/pc/app-category-rules/id"));
        Assert.True(McpWriteEndpointMap.IsAllowedForTool("delete_category", "DELETE", "/api/v1/pc/categories/id"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("create_category", "DELETE", "/api/v1/pc/app-category-rules/id"));
        Assert.False(McpWriteEndpointMap.IsAllowedForTool("delete_category", "POST", "/api/v1/pc/app-category-rules"));
    }

    [Theory]
    [InlineData("create_event")]
    [InlineData("update_event")]
    [InlineData("delete_event")]
    [InlineData("batch_delete_events")]
    public void EventWrites_AdvertiseTheRealOutlookBoundary(string name)
    {
        var description = McpToolExecutor.ToolContract.Single(t => t.Name == name).Description;
        Assert.Contains("LOCAL EVENTS ONLY", description);
        Assert.Contains("does NOT expose", description);
        Assert.Contains("canEdit", description);
        Assert.Contains("Outlook", McpToolCatalog.WriteTools.Single(t => t.Name == name).Description);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MobileProjection_KeepsCanonicalItemsAndPaginationIncludingFallback(bool fallback)
    {
        var response = JsonNode.Parse("""{"code":0,"data":{"items":[{"kind":"session","id":"s"}],"sessions":[{"kind":"session","id":"s"}],"fallbackSummaries":[],"page":2,"totalCount":101,"hasMore":true,"truncated":true}}""")!;
        if (fallback)
        {
            var item = JsonNode.Parse("""{"kind":"fallback","id":"f"}""")!;
            response["data"]!["items"]!.AsArray().Add(item.DeepClone());
            response["data"]!["fallbackSummaries"]!.AsArray().Add(item);
        }
        var projected = McpToolResponseProjection.Apply("get_mobile_timeline", response)!;
        Assert.True(JsonNode.DeepEquals(response["data"]!["items"], projected["data"]!["items"]));
        Assert.Null(projected["data"]!["sessions"]);
        Assert.Null(projected["data"]!["fallbackSummaries"]);
        foreach (var field in new[] { "page", "totalCount", "hasMore", "truncated" })
            Assert.True(JsonNode.DeepEquals(response["data"]![field], projected["data"]![field]));
        Assert.NotNull(response["data"]!["sessions"]);
    }

    [Theory]
    [InlineData("{\"sessions\":[{\"id\":\"s\"}],\"fallbackSummaries\":[]}")]
    [InlineData("{\"items\":[],\"sessions\":[{\"id\":\"s\"}],\"fallbackSummaries\":[]}")]
    [InlineData("{\"items\":[{\"id\":\"a\"}],\"sessions\":[{\"id\":\"b\"}],\"fallbackSummaries\":[]}")]
    public void MobileProjection_DoesNotDiscardNonDuplicateOrUnknownShapes(string data)
    {
        var response = JsonNode.Parse("{\"code\":0,\"data\":" + data + "}")!;
        Assert.Same(response, McpToolResponseProjection.Apply("get_mobile_timeline", response));
    }

    [Fact]
    public void PcProjection_EmptyActivityGridDoesNotDiscardOtherEvidenceOrInventHasData()
    {
        var response = JsonNode.Parse("""{"code":0,"data":{"heatmap":[{"activeMinutes":0,"totalEvents":0,"intensityLevel":0}],"keystats":{"keyPresses":0},"sessions":[{"durationMinutes":0}],"metrics":{"idleDuration":"5m"}}}""")!;
        var projected = McpToolResponseProjection.Apply("get_pc_summary", response)!;
        Assert.Empty(projected["data"]!["heatmap"]!.AsArray());
        foreach (var field in new[] { "keystats", "sessions", "metrics" })
            Assert.True(JsonNode.DeepEquals(response["data"]![field], projected["data"]![field]));
        Assert.Null(projected["data"]!["hasData"]);
        Assert.Single(response["data"]!["heatmap"]!.AsArray());
    }

    [Theory]
    [InlineData("{\"activeMinutes\":0,\"totalEvents\":1,\"intensityLevel\":0}")]
    [InlineData("{\"activeMinutes\":1,\"totalEvents\":0,\"intensityLevel\":0}")]
    [InlineData("{\"activeMinutes\":0,\"intensityLevel\":0}")]
    public void PcProjection_ObservedZeroOrUnknownBucketIsPreserved(string bucket)
    {
        var response = JsonNode.Parse("{\"code\":0,\"data\":{\"heatmap\":[" + bucket + "]}}")!;
        Assert.Same(response, McpToolResponseProjection.Apply("get_pc_summary", response));
    }

    [Theory]
    [InlineData("get_mobile_timeline")]
    [InlineData("get_pc_summary")]
    public void Projection_NeverTransformsErrors(string name)
    {
        var error = JsonNode.Parse("""{"code":500,"error":"unavailable","data":{}}""")!;
        Assert.Same(error, McpToolResponseProjection.Apply(name, error));
    }
}
