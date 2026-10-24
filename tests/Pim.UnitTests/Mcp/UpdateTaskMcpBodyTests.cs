using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// api#465：MCP update_task 的发送体回归。executor 沿用「null 参数直接省略」语义，
/// due-only / 部分字段调用发出的 body 不含默认 null 字段；服务端三态（缺失=保留）
/// 必须与之配合，不丢其他字段。
/// </summary>
public sealed class UpdateTaskMcpBodyTests : IDisposable
{
    private readonly List<(string Method, string Path, string? Body)> _requests = new();
    private readonly McpToolExecutor _executor;
    private readonly string _tokenDir;
    private readonly PimDbContext _db;
    private readonly JwtService _jwt;

    public UpdateTaskMcpBodyTests()
    {
        var tokenDir = Path.Combine(Path.GetTempPath(), "mcp-update-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tokenDir);
        _tokenDir = tokenDir;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var dispatcher = new McpInProcessDispatcher();
        dispatcher.Initialize(async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            _requests.Add((context.Request.Method, context.Request.Path.Value ?? string.Empty, body));
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            var payload = Encoding.UTF8.GetBytes("""{"code":0,"message":"ok","data":{"id":"t1"}}""");
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload);
        }, services);
        var client = new McpInProcessClient(dispatcher);
        var tokenSource = new McpStdioTokenSource(tokenDir);
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.Mcp.Entities.McpClientEntity).Assembly);
        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"mcp-update-body-{Guid.NewGuid():N}")
            .Options;
        _db = new PimDbContext(dbOptions);
        _db.Users.Add(new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = "update-body-user",
            Email = "update-body-user@example.com",
            PasswordHash = "x",
            Role = "user",
        });
        _db.SaveChanges();
        _jwt = new JwtService(new ConfigurationBuilder().Build(), new StubHostEnvironment(), NullLogger<JwtService>.Instance);
        var verification = new McpClientService(_db, new AuditLogService(_db), _jwt);
        var owner = _db.Users.Single().Id;
        var registered = verification.CreateAsync("task-update-test", owner).GetAwaiter().GetResult();
        var permissions = McpToolCatalog.DefaultPermissions();
        permissions["write"]["update_task"] = true;
        verification.UpdateAsync(registered.Client.Id, null, permissions, owner).GetAwaiter().GetResult();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {registered.Token}";
        _executor = new McpToolExecutor(
            verification,
            client,
            tokenSource,
            httpContext);
    }

    public void Dispose()
    {
        _jwt.Dispose();
        _db.Dispose();
        Directory.Delete(_tokenDir, recursive: true);
    }

    private async Task<JsonObject> CallAsync(Dictionary<string, JsonElement> args)
    {
        var result = await _executor.ExecuteAsync(
            new CallToolRequestParams { Name = "update_task", Arguments = args },
            CancellationToken.None);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return Assert.IsType<JsonObject>(JsonNode.Parse(text));
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public async Task DueOnly_SendsOnlyDue()
    {
        var result = await CallAsync(new Dictionary<string, JsonElement>
        {
            ["taskId"] = Json("\"11111111-1111-1111-1111-111111111111\""),
            ["due"] = Json("\"2026-11-05T08:00:00Z\""),
        });

        Assert.Equal(0, result["code"]!.GetValue<int>());
        var request = Assert.Single(_requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("/api/v1/calendar/tasks/11111111-1111-1111-1111-111111111111", request.Path);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Single(body);
        Assert.Equal("2026-11-05T08:00:00Z", body["due"]!.GetValue<string>());
    }

    [Fact]
    public async Task NullArguments_AreOmittedNotCleared()
    {
        await CallAsync(new Dictionary<string, JsonElement>
        {
            ["taskId"] = Json("\"22222222-2222-2222-2222-222222222222\""),
            ["title"] = Json("null"),
            ["priority"] = Json("null"),
            ["status"] = Json("null"),
            ["description"] = Json("null"),
        });

        var body = JsonNode.Parse(Assert.Single(_requests).Body!)!.AsObject();
        Assert.Empty(body);
    }

    [Fact]
    public async Task ExplicitZeroPriority_IsSentAsProvided()
    {
        await CallAsync(new Dictionary<string, JsonElement>
        {
            ["taskId"] = Json("\"33333333-3333-3333-3333-333333333333\""),
            ["priority"] = Json("0"),
        });

        var body = JsonNode.Parse(Assert.Single(_requests).Body!)!.AsObject();
        Assert.Single(body);
        Assert.Equal(0, body["priority"]!.GetValue<int>());
    }

    private sealed class StubHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Pim.UnitTests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Test";
    }
}
