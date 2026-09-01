using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// End-to-end protocol tests over the real Pim.Api host (WebApplicationFactory):
/// bearer guard (401 40101 JSON), trailing-slash 308, Streamable HTTP initialize /
/// tools/list / tools/call, and in-process dispatch parity. DB-free on purpose
/// (CI has no Postgres): tool calls fail verification with the expected 401 result,
/// which still exercises the full pipeline (guard → SDK → executor → verify).
/// </summary>
public sealed class McpProtocolIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpProtocolIntegrationTests(WebApplicationFactory<Program> factory)
        // DisableHangfire like the other full-app factory tests: CI has no Postgres, and the
        // Hangfire background server cannot stop cleanly when its storage is unreachable
        // (factory disposal then fails and every test in the class reports a cleanup failure).
        => _factory = factory.WithWebHostBuilder(b => b.UseSetting("DisableHangfire", "true"));

    private HttpClient CreateClient() => _factory.CreateClient();

    private static HttpRequestMessage JsonRpc(string method, object? @params, string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params,
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (sessionId is not null)
            request.Headers.Add("MCP-Session-Id", sessionId);
        return request;
    }

    /// <summary>
    /// The SDK streams Streamable HTTP responses as SSE (event: message / data: ...)
    /// when the client accepts text/event-stream. Returns the JSON-RPC payload of the
    /// first data frame.
    /// </summary>
    private static JsonDocument ReadJsonFromResponse(HttpResponseMessage response)
    {
        var raw = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var dataLine = raw.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
        if (dataLine is null)
            throw new InvalidOperationException($"No SSE data frame in response: {raw[..Math.Min(200, raw.Length)]}");
        return JsonDocument.Parse(dataLine["data:".Length..].Trim());
    }

    // ---------- bearer guard ----------

    [Fact]
    public async Task GetMcp_WithoutToken_Returns401Json_NotSpa()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(40101, body.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("missing bearer token", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PostMcp_WithoutToken_Returns401Json()
    {
        using var client = CreateClient();
        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(40101, body.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task GetMcp_TrailingSlash_Redirects308_PreservingMethod()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pim_mcp_dummy");
        // Inspect the 308 itself — auto-redirect would strip the Authorization header on the follow-up.
        using var noRedirectClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await noRedirectClient.GetAsync("/mcp/");
        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("/mcp", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task OptionsMcp_PassesGuard()
    {
        using var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        var response = await client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- protocol ----------

    [Fact]
    public async Task Initialize_ReturnsServerInfo_WithDummyBearer()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pim_mcp_dummy");
        var response = await client.SendAsync(JsonRpc("initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "pim-test", version = "1.0" },
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = ReadJsonFromResponse(response);
        var root = body.RootElement;
        Assert.Equal("pim-mcp-server", root.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("2025-03-26", root.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.True(root.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task ToolsList_ReturnsAll151Tools()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pim_mcp_dummy");
        var response = await client.SendAsync(JsonRpc("tools/list", new { }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = ReadJsonFromResponse(response);
        var tools = body.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(151, tools.GetArrayLength());

        var contractNames = McpToolExecutor.ToolContract.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var wireNames = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(contractNames, wireNames);
    }

    [Fact]
    public async Task ToolsCall_WithDummyToken_Returns401ToolResult()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pim_mcp_invalid");
        var response = await client.SendAsync(JsonRpc("tools/call", new
        {
            name = "get_calendars",
            arguments = new { },
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = ReadJsonFromResponse(response);
        var content = body.RootElement.GetProperty("result").GetProperty("content");
        var text = content.EnumerateArray().First(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
        using var payload = JsonDocument.Parse(text!);
        // No Postgres in CI: verification fails like Python's "verify request failed" (500),
        // never an RPC-level error — the tool result carries the failure.
        Assert.Equal(500, payload.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("request failed", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsRpcError()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pim_mcp_dummy");
        var response = await client.SendAsync(JsonRpc("tools/call", new
        {
            name = "no_such_tool",
            arguments = new { },
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unknown tool", raw);
    }

    [Fact]
    public async Task GetMcp_WithBearer_DoesNotReturnSpaPage()
    {
        using var client = CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pim_mcp_dummy");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync("/mcp");
        }
        catch (TaskCanceledException)
        {
            // Session-less GET keeps the SSE stream open without headers — acceptable SDK behavior.
            return;
        }
        // Either the SDK started an SSE stream, or it must never fall back to the SPA page.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("text/html", response.Content.Headers.ContentType?.ToString() ?? string.Empty);
    }

    // ---------- in-process dispatch parity ----------

    [Fact]
    public async Task ToolsCall_GetVersion_VerifiesFirst_LikePython()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pim_mcp_dummy");
        var response = await client.SendAsync(JsonRpc("tools/call", new
        {
            name = "get_version",
            arguments = new { },
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = ReadJsonFromResponse(response);
        var text = body.RootElement.GetProperty("result").GetProperty("content").EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
        // HTTP mode always verifies first (Python parity) → DB failure yields 500 tool result.
        using var payload = JsonDocument.Parse(text!);
        Assert.Equal(500, payload.RootElement.GetProperty("code").GetInt32());
    }
}
