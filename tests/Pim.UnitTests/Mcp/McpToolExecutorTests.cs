using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Module.Mcp.Entities;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Executor behavior tests (Python reference parity): param validation, response
/// shaping, redaction, truncation, local pagination, auth outcomes, special tools.
/// Uses a scripted in-process pipeline plus a real McpClientService over InMemory EF.
/// </summary>
public sealed class McpToolExecutorTests : IDisposable
{
    private sealed record ScriptedResponse(int Status, string Body, string ContentType = "application/json");

    private readonly PimDbContext _db;
    private readonly JwtService _jwt;
    private readonly McpClientService _verifyService;
    private readonly Guid _owner;
    private string _clientToken = string.Empty;

    private readonly List<(string Method, string Path, string? Query, string? Body, string? Auth)> _requests = new();
    private readonly Dictionary<string, ScriptedResponse> _routes = new();
    private McpToolExecutor _executor = null!;
    private HttpContext? _httpContext;

    public McpToolExecutorTests()
    {
        PimDbContext.RegisterModuleAssembly(typeof(McpClientEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("mcp-executor-" + Guid.NewGuid())
            .Options;
        _db = new PimDbContext(options);
        var env = new StubHostEnvironment();
        _jwt = new JwtService(new ConfigurationBuilder().Build(), env, NullLogger<JwtService>.Instance);
        _verifyService = new McpClientService(_db, new AuditLogService(_db), _jwt);
        _owner = Guid.NewGuid();
        _db.Users.Add(new UserEntity { Id = _owner, Username = "alice", Email = "a@b.com", PasswordHash = "x", Role = "admin" });
        _db.SaveChanges();
    }

    private string? _tokenDir;

    public void Dispose()
    {
        _jwt.Dispose();
        _db.Dispose();
        if (_tokenDir is not null)
            Directory.Delete(_tokenDir, recursive: true);
    }

    private string TokenFileDir(string? token)
    {
        _tokenDir = Path.Combine(Path.GetTempPath(), "mcp-executor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tokenDir);
        if (token is not null)
            File.WriteAllText(Path.Combine(_tokenDir, ".token"), token);
        return _tokenDir;
    }

    private void CreateExecutor(bool http = true, bool withWritePermission = false, bool withReadPermission = true, string? stdioToken = null, string? stdioTokenDir = null)
    {
        var client = _verifyService.CreateAsync("executor-test", _owner).GetAwaiter().GetResult();
        _clientToken = client.Token;

        if (withWritePermission)
        {
            var perms = Pim.Module.Mcp.Services.McpToolCatalog.DefaultPermissions();
            perms["write"] = perms["write"].ToDictionary(kv => kv.Key, kv => true);
            if (!withReadPermission)
                perms["read"] = perms["read"].ToDictionary(kv => kv.Key, kv => false);
            _verifyService.UpdateAsync(client.Client.Id, null, perms, _owner).GetAwaiter().GetResult();
        }
        else if (!withReadPermission)
        {
            var perms = Pim.Module.Mcp.Services.McpToolCatalog.DefaultPermissions();
            perms["read"] = perms["read"].ToDictionary(kv => kv.Key, kv => false);
            _verifyService.UpdateAsync(client.Client.Id, null, perms, _owner).GetAwaiter().GetResult();
        }

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var dispatcher = new McpInProcessDispatcher();
        dispatcher.Initialize(async context =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            _requests.Add((context.Request.Method, path, context.Request.QueryString.Value, ReadBody(context.Request), context.Request.Headers.Authorization.ToString()));
            if (_routes.TryGetValue(context.Request.Method + " " + path, out var scripted))
            {
                context.Response.StatusCode = scripted.Status;
                context.Response.ContentType = scripted.ContentType;
                var bytes = Encoding.UTF8.GetBytes(scripted.Body);
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes);
                return;
            }
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"code\":404,\"error\":\"not found\"}");
        }, services);
        var client2 = new McpInProcessClient(dispatcher);
        var tokenSource = new McpStdioTokenSource(stdioTokenDir ?? TokenFileDir(stdioToken));

        if (!http)
        {
            _executor = new McpToolExecutor(_verifyService, client2, tokenSource, httpContext: null);
            return;
        }

        _httpContext = new DefaultHttpContext();
        _httpContext.Request.Headers.Authorization = $"Bearer {_clientToken}";
        _executor = new McpToolExecutor(_verifyService, client2, tokenSource, _httpContext);
    }

    private static string ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task<JsonNode?> CallTool(string name, Dictionary<string, JsonElement>? args = null)
    {
        var result = await _executor.ExecuteAsync(new CallToolRequestParams
        {
            Name = name,
            Arguments = args ?? new Dictionary<string, JsonElement>(),
        }, CancellationToken.None);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Console.WriteLine("EXECUTOR DEBUG [" + name + "]: " + text);
        return JsonNode.Parse(text);
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ---------- validation ----------

    [Fact]
    public async Task Pagination_PageBelowOne_Returns400()
    {
        CreateExecutor();
        var result = await CallTool("get_events", new Dictionary<string, JsonElement> { ["page"] = Json("0") });
        Assert.Equal(400, result!["code"]!.GetValue<int>());
        Assert.Equal("page must be >=1", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pagination_PageSizeAbove100_Returns400()
    {
        CreateExecutor();
        var result = await CallTool("get_events", new Dictionary<string, JsonElement> { ["pageSize"] = Json("101") });
        Assert.Equal("pageSize must be between 1 and 100", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Limit_OutOfRange_Returns400()
    {
        CreateExecutor();
        var result = await CallTool("search_pim", new Dictionary<string, JsonElement> { ["q"] = Json("\"x\""), ["limit"] = Json("0") });
        Assert.Equal("limit must be 1..100", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task BlockMinutes_Invalid_Returns400()
    {
        CreateExecutor();
        var result = await CallTool("get_pc_activity_analysis", new Dictionary<string, JsonElement>
        {
            ["date"] = Json("\"2026-09-01\""),
            ["blockMinutes"] = Json("20"),
        });
        Assert.Equal("blockMinutes must be 15, 30 or 60", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Date_Invalid_Returns400()
    {
        CreateExecutor();
        var result = await CallTool("get_pc_summary", new Dictionary<string, JsonElement> { ["date"] = Json("\"2026/09/01\"") });
        Assert.Equal("date must be YYYY-MM-DD", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TimeRange_StartAfterEnd_Returns400()
    {
        CreateExecutor();
        var result = await CallTool("get_calendar_layers", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2026-09-02T00:00:00Z\""),
            ["end"] = Json("\"2026-09-01T00:00:00Z\""),
        });
        Assert.Equal("invalid time range: start must be <= end", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TimeRange_SpanTooLarge_Returns400()
    {
        CreateExecutor();
        var result = await CallTool("get_events", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2024-01-01T00:00:00Z\""),
            ["end"] = Json("\"2026-09-01T00:00:00Z\""),
        });
        Assert.Equal("time range too large: max span 366 days", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task WriteTool_MissingRequired_Returns400()
    {
        CreateExecutor(withWritePermission: true);
        var result = await CallTool("create_task", new Dictionary<string, JsonElement>());
        Assert.Equal("title is required", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task BatchUpdateTasks_NoUpdateField_Returns400()
    {
        CreateExecutor(withWritePermission: true);
        var result = await CallTool("batch_update_tasks", new Dictionary<string, JsonElement> { ["ids"] = Json("[\"a\",\"b\"]") });
        Assert.Equal("at least one of status/priority/calendarId is required", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Base64_Invalid_Returns400()
    {
        CreateExecutor(withWritePermission: true);
        var result = await CallTool("upload_quick_note_attachment", new Dictionary<string, JsonElement>
        {
            ["fileContentBase64"] = Json("\"not!!base64\""),
            ["fileName"] = Json("\"x.txt\""),
        });
        Assert.Equal("fileContentBase64 must be valid base64", result!["error"]!.GetValue<string>());
    }

    // ---------- permission model ----------

    [Fact]
    public async Task WriteTool_WithoutWritePermission_Returns403PermissionDenied()
    {
        CreateExecutor(); // read-only token by default
        var result = await CallTool("create_task", new Dictionary<string, JsonElement> { ["title"] = Json("\"x\"") });
        Assert.Equal(403, result!["code"]!.GetValue<int>());
        Assert.Equal("permission denied: create_task", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadTool_WithoutReadPermission_Returns403()
    {
        CreateExecutor(withReadPermission: false);
        var result = await CallTool("get_calendars");
        Assert.Equal(403, result!["code"]!.GetValue<int>());
        Assert.Equal("permission denied: get_calendars", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidToken_Returns401()
    {
        CreateExecutor();
        _httpContext!.Request.Headers.Authorization = "Bearer pim_mcp_bogus";
        var result = await CallTool("get_calendars");
        Assert.Equal(401, result!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MissingToken_HttpMode_Returns401Guidance()
    {
        CreateExecutor();
        _httpContext!.Request.Headers.Remove("Authorization");
        var result = await CallTool("get_calendars");
        Assert.Equal(401, result!["code"]!.GetValue<int>());
        Assert.Contains("missing bearer token", result!["error"]!.GetValue<string>());
        Assert.Contains("pim_mcp_", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task MissingToken_StdioMode_Returns401Guidance()
    {
        CreateExecutor(http: false, stdioTokenDir: TokenFileDir(null));
        var result = await CallTool("get_calendars");
        Assert.Equal(401, result!["code"]!.GetValue<int>());
        Assert.Contains("/api/v1/auth/login", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task StdioMode_SendsEnvTokenAsBearer()
    {
        CreateExecutor(http: false, stdioTokenDir: TokenFileDir("stdio-jwt-abc"));
        _routes["GET /api/v1/calendar/calendars"] = new(200, "{\"code\":0,\"data\":[]}");
        await CallTool("get_calendars");
        Assert.Equal("Bearer stdio-jwt-abc", Assert.Single(_requests).Auth);
    }

    // ---------- dispatch shape ----------

    [Fact]
    public async Task GetEvents_BuildsQueryWithRenamesAndBearer()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/events"] = new(200, "{\"code\":0,\"data\":[]}");
        await CallTool("get_events", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2026-09-01T00:00:00Z\""),
            ["end"] = Json("\"2026-09-02T00:00:00Z\""),
            ["calendarId"] = Json("\"cal-1\""),
            ["page"] = Json("2"),
            ["pageSize"] = Json("50"),
        });

        var request = Assert.Single(_requests);
        Assert.Equal("GET", request.Method);
        Assert.Contains("start=2026-09-01T00%3A00%3A00Z", request.Query);
        Assert.Contains("end=2026-09-02T00%3A00%3A00Z", request.Query);
        Assert.Contains("calendarId=cal-1", request.Query);
        Assert.Contains("page=2", request.Query);
        Assert.Contains("pageSize=50", request.Query);
        Assert.StartsWith("Bearer ", request.Auth ?? string.Empty);
        Assert.NotEqual("Bearer " + _clientToken, request.Auth); // scoped JWT, not the raw mcp token
    }

    [Fact]
    public async Task CreateEvent_PostBody()
    {
        CreateExecutor(withWritePermission: true);
        _routes["POST /api/v1/calendar/events"] = new(200, "{\"code\":0,\"data\":{\"id\":\"e1\"}}");
        var result = await CallTool("create_event", new Dictionary<string, JsonElement>
        {
            ["calendarId"] = Json("\"cal-1\""),
            ["title"] = Json("\"Demo\""),
            ["dtStart"] = Json("\"2026-09-01T09:00:00Z\""),
            ["dtEnd"] = Json("\"2026-09-01T10:00:00Z\""),
            ["isAllDay"] = Json("false"),
        });

        Assert.Equal(0, result!["code"]!.GetValue<int>());
        var request = Assert.Single(_requests);
        Assert.Equal("POST", request.Method);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("cal-1", body["calendarId"]!.GetValue<string>());
        Assert.Equal("Demo", body["title"]!.GetValue<string>());
        Assert.False(body["isAllDay"]!.GetValue<bool>());
        Assert.Equal("Bearer ", request.Auth![..7]);
    }

    [Fact]
    public async Task UpdateEvent_ScopeGoesToQuery_BodyToJson()
    {
        CreateExecutor(withWritePermission: true);
        _routes["PUT /api/v1/calendar/events/e1"] = new(200, "{\"code\":0,\"data\":{\"id\":\"e1\"}}");
        await CallTool("update_event", new Dictionary<string, JsonElement>
        {
            ["eventId"] = Json("\"e1\""),
            ["calendarId"] = Json("\"cal-1\""),
            ["title"] = Json("\"Renamed\""),
            ["dtStart"] = Json("\"2026-09-01T09:00:00Z\""),
            ["dtEnd"] = Json("\"2026-09-01T10:00:00Z\""),
            ["scope"] = Json("\"This\""),
        });

        var request = Assert.Single(_requests);
        Assert.Contains("scope=This", request.Query);
        Assert.DoesNotContain("scope", request.Body);
        Assert.Contains("\"title\":\"Renamed\"", request.Body);
    }

    [Fact]
    public async Task GetRecycleBin_MapsStartEndToDeletedRange()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/recycle-bin"] = new(200, "{\"code\":0,\"data\":[]}");
        await CallTool("get_recycle_bin", new Dictionary<string, JsonElement>
        {
            ["type"] = Json("\"event\""),
            ["start"] = Json("\"2026-08-01\""),
            ["end"] = Json("\"2026-08-31\""),
        });

        var request = Assert.Single(_requests);
        Assert.Contains("type=event", request.Query);
        Assert.Contains("deletedFrom=2026-08-01", request.Query);
        Assert.Contains("deletedTo=2026-08-31", request.Query);
        Assert.DoesNotContain("start=", request.Query);
        Assert.DoesNotContain("end=", request.Query);
    }

    [Fact]
    public async Task MobileLocationTracks_MapsStartEndToRangeStartUtc()
    {
        CreateExecutor();
        _routes["GET /api/v1/mobile/location/analytics/tracks"] = new(200, "{\"code\":0,\"data\":[]}");
        await CallTool("get_mobile_location_tracks", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2026-09-01\""),
            ["end"] = Json("\"2026-09-02\""),
        });

        var request = Assert.Single(_requests);
        Assert.Contains("rangeStartUtc=2026-09-01", request.Query);
        Assert.Contains("rangeEndUtc=2026-09-02", request.Query);
    }

    [Fact]
    public async Task ImportIcs_MultipartUpload()
    {
        CreateExecutor(withWritePermission: true);
        _routes["POST /api/v1/calendar/import-ics"] = new(200, "{\"code\":0,\"data\":{\"imported\":1}}");
        var result = await CallTool("import_ics", new Dictionary<string, JsonElement>
        {
            ["icsContent"] = Json("\"BEGIN:VCALENDAR\\nEND:VCALENDAR\\n\""),
        });

        Assert.Equal(0, result!["code"]!.GetValue<int>());
        var request = Assert.Single(_requests);
        Assert.Contains("Content-Disposition: form-data; name=file", request.Body);
        Assert.Contains("import.ics", request.Body);
        Assert.Contains("BEGIN:VCALENDAR", request.Body);
    }

    // ---------- response shaping ----------

    [Fact]
    public async Task ErrorPassthrough_WithErrorKey_PreservesBody()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/calendars"] = new(500, "{\"code\":50001,\"error\":\"boom\",\"message\":\"x\"}");
        var result = await CallTool("get_calendars");
        Assert.Equal("boom", result!["error"]!.GetValue<string>());
        Assert.Equal(50001, result!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ErrorWithoutErrorKey_WrapsWithHttpStatus()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/calendars"] = new(502, "{\"code\":0,\"data\":null}");
        var result = await CallTool("get_calendars");
        Assert.StartsWith("HTTP 502:", result!["error"]!.GetValue<string>());
        Assert.Equal(502, result!["code"]!.GetValue<int>());
        Assert.NotNull(result!["details"]);
    }

    [Fact]
    public async Task NonJsonResponse_ReturnsTextFallback()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/calendars"] = new(200, "just text", "text/plain");
        var result = await CallTool("get_calendars");
        Assert.Equal("just text", result!["data"]!.GetValue<string>());
        Assert.Equal(0, result!["code"]!.GetValue<int>());
        Assert.Equal("text/plain", result!["contentType"]!.GetValue<string>());
    }

    // ---------- redaction ----------

    [Fact]
    public async Task RedactUrls_HashesUrlFields_KeepsTitle()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/events"] = new(200, """{"code":0,"data":{"items":[{"id":"e1","title":"Full title","url":"https://example.com/a","openLink":"https://example.com/b","href":"https://example.com/c","downloadUrl":"https://example.com/d"}]}}""");
        var result = await CallTool("get_events", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2026-09-01\""),
            ["end"] = Json("\"2026-09-02\""),
        });

        var item = result!["data"]!["items"]![0]!;
        Assert.Equal("Full title", item["title"]!.GetValue<string>());
        Assert.Equal(12, item["urlHash"]!.GetValue<string>().Length);
        Assert.Null(item["url"]);
        Assert.Equal(12, item["openLinkHash"]!.GetValue<string>().Length);
        Assert.Equal(12, item["hrefHash"]!.GetValue<string>().Length);
        Assert.Equal(12, item["downloadUrlHash"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task RedactUrls_False_ReturnsRaw()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/events"] = new(200, """{"code":0,"data":{"items":[{"id":"e1","url":"https://example.com/a"}]}}""");
        var result = await CallTool("get_events", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2026-09-01\""),
            ["end"] = Json("\"2026-09-02\""),
            ["redactUrls"] = Json("false"),
        });

        Assert.Equal("https://example.com/a", result!["data"]!["items"]![0]!["url"]!.GetValue<string>());
        Assert.Null(result!["data"]!["items"]![0]!["urlHash"]);
    }

    [Fact]
    public async Task FileOpenLink_AlwaysRedacts()
    {
        CreateExecutor();
        _routes["GET /api/v1/files/items/f1/open-link"] = new(200, """{"code":0,"data":{"openLink":"https://files.example.com/x"}}""");
        var result = await CallTool("get_file_open_link", new Dictionary<string, JsonElement> { ["file_id"] = Json("\"f1\"") });
        Assert.Equal(12, result!["data"]!["openLinkHash"]!.GetValue<string>().Length);
    }

    // ---------- truncation ----------

    [Fact]
    public async Task Truncation_Over50Kb_AddsHints()
    {
        CreateExecutor();
        var big = new string('a', 55 * 1024);
        _routes["GET /api/v1/calendar/events"] = new(200, $"{{\"code\":0,\"data\":{{\"items\":[{{\"id\":\"e1\",\"blob\":\"{big}\"}}]}},\"page\":1}}");
        var result = await CallTool("get_events", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2026-09-01\""),
            ["end"] = Json("\"2026-09-02\""),
            ["page"] = Json("1"),
        });

        Assert.True(result!["truncated"]!.GetValue<bool>());
        Assert.Equal(2, result!["nextPage"]!.GetValue<int>());
        Assert.NotNull(result!["_note"]);
    }

    [Fact]
    public async Task Truncation_SmallResponse_Untouched()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/calendars"] = new(200, "{\"code\":0,\"data\":[]}");
        var result = await CallTool("get_calendars");
        Assert.Null(result!["truncated"]);
    }

    // ---------- local pagination ----------

    [Fact]
    public async Task LocalPagination_SlicesBareList()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/projects"] = new(200, """{"code":0,"data":["p1","p2","p3","p4","p5"]}""");
        var result = await CallTool("get_projects", new Dictionary<string, JsonElement>
        {
            ["page"] = Json("2"),
            ["pageSize"] = Json("2"),
        });

        var data = result!["data"]!.AsArray();
        Assert.Equal(2, data.Count);
        Assert.Equal("p3", data[0]!.GetValue<string>());
        Assert.Equal("p4", data[1]!.GetValue<string>());
        Assert.Equal(2, result["page"]!.GetValue<int>());
        Assert.Equal(2, result["pageSize"]!.GetValue<int>());
        Assert.Equal(5, result["total"]!.GetValue<int>());
    }

    // ---------- special tools ----------

    [Fact]
    public async Task GetSystemHealth_PassesStdioToken()
    {
        CreateExecutor(http: false, stdioTokenDir: TokenFileDir("stdio-jwt-abc"));
        _routes["GET /health"] = new(200, "{\"status\":\"healthy\"}");
        var result = await CallTool("get_system_health");
        Assert.Equal("healthy", result!["status"]!.GetValue<string>());
        Assert.Equal("Bearer stdio-jwt-abc", Assert.Single(_requests).Auth);
    }

    [Fact]
    public async Task GetSystemHealth_NoToken_AnonymousCall()
    {
        CreateExecutor(http: false, stdioTokenDir: TokenFileDir(null));
        _routes["GET /health"] = new(200, "{\"status\":\"healthy\"}");
        var result = await CallTool("get_system_health");
        Assert.Equal("healthy", result!["status"]!.GetValue<string>());
        Assert.Empty(_requests.Single().Auth ?? string.Empty);
    }

    [Fact]
    public async Task GetMobileLocationLatest_ExtractsLatestPoint()
    {
        CreateExecutor();
        _routes["GET /api/v1/mobile/location/history"] = new(200, """{"code":0,"data":{"points":[{"lat":1},{"lat":2},{"lat":3}]}}""");
        var result = await CallTool("get_mobile_location_latest");
        Assert.Equal(3, result!["data"]!["lat"]!.GetValue<int>());
        Assert.Equal(3, result!["meta"]!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetCalendarById_FiltersList()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/calendars"] = new(200, """{"code":0,"data":[{"id":"cal-1","name":"Work"},{"id":"cal-2","name":"Life"}]}""");
        var result = await CallTool("get_calendar_by_id", new Dictionary<string, JsonElement> { ["calendar_id"] = Json("\"cal-2\"") });
        Assert.Equal("Life", result!["data"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetCalendarById_NotFound_Returns404()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/calendars"] = new(200, """{"code":0,"data":[{"id":"cal-1"}]}""");
        var result = await CallTool("get_calendar_by_id", new Dictionary<string, JsonElement> { ["calendar_id"] = Json("\"cal-zzz\"") });
        Assert.Equal(404, result!["code"]!.GetValue<int>());
        Assert.Contains("calendar cal-zzz not found", result!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task SchedulePreview_DefaultsTaskIdsToEmpty()
    {
        CreateExecutor();
        _routes["POST /api/v1/calendar/schedule"] = new(200, "{\"code\":0,\"data\":{}}");
        await CallTool("get_schedule_preview");
        var body = JsonNode.Parse(Assert.Single(_requests).Body!)!.AsObject();
        Assert.Equal(0, body["taskIds"]!.AsArray().Count);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Pim.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task ExportIcs_IdsFallsBackToCalendarId()
    {
        CreateExecutor();
        _routes["GET /api/v1/calendar/export-ics"] = new(200, "{\"code\":0,\"data\":\"ICS\"}");
        await CallTool("get_export_ics", new Dictionary<string, JsonElement>
        {
            ["start"] = Json("\"2026-09-01\""),
            ["end"] = Json("\"2026-09-02\""),
            ["calendarId"] = Json("\"cal-1\""),
        });
        var request = Assert.Single(_requests);
        Assert.Contains("ids=cal-1", request.Query);
        Assert.Contains("calendarId=cal-1", request.Query);
    }
}