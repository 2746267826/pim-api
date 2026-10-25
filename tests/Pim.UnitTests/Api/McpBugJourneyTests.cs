using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.Module.Mcp.Services;
using Pim.Module.Mobile.Entities;
using Pim.Module.PcTracker.Entities;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Api;

public sealed class McpBugJourneyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LoginJwt_IsRejectedBeforeMcpHandshakeOrDiscovery_WhileClientTokenWorks()
    {
        using var setup = await Setup.CreateAsync();
        foreach (var method in new[] { "initialize", "tools/list", "tools/call" })
        {
            using var request = Rpc(method, new { });
            var response = await setup.Rest.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("pim_mcp_*", text);
            Assert.Contains("not a login JWT", text);
            Assert.DoesNotContain(setup.Rest.DefaultRequestHeaders.Authorization!.Parameter!, text);
        }
        using var valid = Rpc("initialize", new
        {
            protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "isolated-test", version = "1" }
        });
        var initialized = ReadRpc(await setup.Mcp.SendAsync(valid));
        Assert.Equal("pim-mcp-server", initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        var calendars = await setup.Call("get_calendars", new { });
        Assert.Equal(0, calendars.GetProperty("code").GetInt32());
        output.WriteLine("A real login JWT failed at initialize/list/call with actionable type guidance; a generated MCP client token initialized and called a read tool.");
    }

    [Fact]
    public async Task Tasks_InvalidMultiStatusIsExplicitError_ValidStatusAndDefaultPaginationReadRealRows()
    {
        using var setup = await Setup.CreateAsync();
        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            for (var i = 0; i < 25; i++)
                db.Add(new TaskEntity { UserId = setup.UserId, Uid = $"mcp-task-{i}", Title = $"Task {i}", Status = i < 13 ? "NEEDS-ACTION" : "IN-PROCESS" });
            db.Add(new TaskEntity { UserId = Guid.NewGuid(), Uid = "other-owner", Title = "Not mine" });
            await db.SaveChangesAsync();
        }
        var bad = await setup.Call("get_tasks", new { status = "NEEDS-ACTION,IN-PROCESS" }, error: true);
        Assert.Equal(400, bad.GetProperty("code").GetInt32());
        Assert.Contains("one value only", bad.GetProperty("error").GetString());
        var first = (await setup.Call("get_tasks", new { })).GetProperty("data");
        Assert.Equal(25, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        var second = (await setup.Call("get_tasks", new { page = 2 })).GetProperty("data");
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        var selected = (await setup.Call("get_tasks", new { status = "NEEDS-ACTION" })).GetProperty("data");
        Assert.Equal(13, selected.GetProperty("totalCount").GetInt32());
        Assert.Equal(13, selected.GetProperty("items").GetArrayLength());
        output.WriteLine("25 own tasks read as 20 + 5, another owner's task excluded. Single status read 13; comma-separated status returned an MCP error, not a false totalCount=0.");
    }

    [Fact]
    public async Task Events_RequireBoundedWindow_AlwaysPage_AndKeepRestLegacyList()
    {
        using var setup = await Setup.CreateAsync();
        var start = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var calendar = new CalendarEntity { UserId = setup.UserId, Name = "Local" };
            db.Add(calendar);
            for (var i = 0; i < 25; i++)
                db.Add(new EventEntity { CalendarId = calendar.Id, Calendar = calendar, Uid = $"mcp-event-{i}", Title = $"Event {i}", DtStart = start, DtEnd = start.AddMinutes(1), TimeZoneId = i % 3 == 0 ? null : i % 3 == 1 ? "Asia/Shanghai" : "UTC" });
            await db.SaveChangesAsync();
        }
        var bad = await setup.Call("get_events", new { }, error: true);
        Assert.Equal(400, bad.GetProperty("code").GetInt32());
        var args = new { start = "2026-10-20T00:00:00Z", end = "2026-10-21T00:00:00Z" };
        var first = (await setup.Call("get_events", args)).GetProperty("data");
        var second = (await setup.Call("get_events", new { args.start, args.end, page = 2 })).GetProperty("data");
        Assert.Equal(25, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        Assert.Equal(25, first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(x => x.GetProperty("id").GetGuid()).Distinct().Count());
        var ids = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(x => x.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(ids.OrderBy(x => x), ids);
        var rest = await Success(await setup.Rest.GetAsync($"/api/v1/calendar/events?start={args.start}&end={args.end}"));
        Assert.Equal(JsonValueKind.Array, rest.ValueKind);
        Assert.Equal(25, rest.GetArrayLength());
        var times = rest.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("dtStart").GetDateTimeOffset());
        foreach (var item in first.GetProperty("items").EnumerateArray())
            Assert.Equal(times[item.GetProperty("id").GetGuid()], item.GetProperty("dtStart").GetDateTimeOffset());
        output.WriteLine("MCP rejected unbounded history, paged 25 same-start events in deterministic ID order as 20 + 5, and preserved timestamp instants with null/UTC/Asia-Shanghai metadata. REST's existing 25-item array remained unchanged.");
    }

    [Fact]
    public async Task CategoryRule_CreateReadDeleteDoesNotDeleteTreeNodeWithSameId()
    {
        using var setup = await Setup.CreateAsync(writes: true);
        var created = (await setup.Call("create_category", new { appPattern = "mcp-journey", categoryName = "Journey", color = "#112233" })).GetProperty("data");
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(0, created.GetProperty("priority").GetInt32());
        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Add(new PcCategoryEntity { Id = id, Name = "Keep this tree node" });
            await db.SaveChangesAsync();
        }
        var before = (await setup.Call("get_pc_categories", new { })).GetProperty("data");
        Assert.Contains(before.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        await setup.Call("delete_category", new { categoryId = id, resourceType = "appRule" });
        var after = (await setup.Call("get_pc_categories", new { })).GetProperty("data");
        Assert.DoesNotContain(after.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        var tree = (await setup.Call("get_pc_category_tree", new { })).GetProperty("data");
        Assert.Contains(tree.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        await setup.Call("delete_category", new { categoryId = id });
        using var reread = setup.Factory.Services.CreateScope();
        var finalDb = reread.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await finalDb.Set<AppCategoryEntity>().AnyAsync(x => x.Id == id));
        Assert.False(await finalDb.Set<PcCategoryEntity>().AnyAsync(x => x.Id == id));
        output.WriteLine("Created an app rule without optional priority, read it back, explicitly deleted that rule, preserved a same-ID tree node, then separately deleted the tree node using the unchanged legacy default.");
    }

    [Fact]
    public async Task MobileTimeline_OneCanonicalMixedStream_RestAliasesAndPagingStayIntact()
    {
        using var setup = await Setup.CreateAsync();
        var start = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Add(new MobileUsageSessionEntity { UserId = setup.UserId, DeviceId = "fixture-device", PackageName = "example.session", StartUtc = start, EndUtc = start.AddMinutes(1), DurationMs = 60000 });
            db.Add(new MobileUsageSummaryEntity { UserId = setup.UserId, DeviceId = "fixture-device", PackageName = "example.fallback", WindowStartUtc = start.AddMinutes(2), WindowEndUtc = start.AddMinutes(3), TotalTimeVisibleMs = 60000, SourceKind = "fallback" });
            await db.SaveChangesAsync();
        }
        var items = new List<JsonElement>();
        for (var page = 1; page <= 2; page++)
        {
            var result = (await setup.Call("get_mobile_timeline", new { date = "2026-10-20", page, pageSize = 1 })).GetProperty("data");
            Assert.False(result.TryGetProperty("sessions", out _));
            Assert.False(result.TryGetProperty("fallbackSummaries", out _));
            Assert.Equal(2, result.GetProperty("totalCount").GetInt32());
            Assert.Equal(page == 1, result.GetProperty("hasMore").GetBoolean());
            items.Add(Assert.Single(result.GetProperty("items").EnumerateArray()).Clone());
        }
        Assert.Equal(new[] { "session", "fallback" }, items.Select(x => x.GetProperty("kind").GetString()));
        var rest = await Success(await setup.Rest.GetAsync("/api/v1/mobile/timeline?date=2026-10-20&page=1&pageSize=100"));
        Assert.Single(rest.GetProperty("sessions").EnumerateArray());
        Assert.Single(rest.GetProperty("fallbackSummaries").EnumerateArray());
        Assert.Equal(2, rest.GetProperty("items").GetArrayLength());
        output.WriteLine("A real session and fallback summary survived two MCP pages in one canonical items stream. REST kept both legacy aliases and the merged stream.");
    }

    [Fact]
    public async Task EmptyPcGrid_IsCompactOnlyInMcp_RegistryMisuseIsActionable()
    {
        using var setup = await Setup.CreateAsync();
        var rest = await Success(await setup.Rest.GetAsync("/api/v1/pc/summary?date=2026-10-20"));
        Assert.Equal(24, rest.GetProperty("heatmap").GetArrayLength());
        var mcp = (await setup.Call("get_pc_summary", new { date = "2026-10-20" })).GetProperty("data");
        Assert.Empty(mcp.GetProperty("heatmap").EnumerateArray());
        foreach (var field in new[] { "keystats", "appRanking", "timeline", "sessions", "metrics", "categories" })
            Assert.Equal(rest.GetProperty(field).GetRawText(), mcp.GetProperty(field).GetRawText());
        Assert.False(mcp.TryGetProperty("hasData", out _));
        var bad = await setup.Call("get_today_sections", new { sectionId = "pc.activity" }, error: true);
        Assert.Contains("get_today_section(", bad.GetProperty("error").GetString());
        var wrongRest = await setup.Rest.GetAsync("/api/v1/today/sections?sectionId=pc.activity");
        Assert.Equal(HttpStatusCode.BadRequest, wrongRest.StatusCode);
        var registry = await setup.Call("get_today_sections", new { date = "2026-10-20" });
        Assert.Equal(0, registry.GetProperty("code").GetInt32());
        var section = await setup.Call("get_today_section", new { date = "2026-10-20", sectionId = "pc.activity" });
        Assert.Equal(0, section.GetProperty("code").GetInt32());
        output.WriteLine("REST retained the 24-hour grid; MCP returned no empty buckets and preserved every other PC signal. Registry misuse returned guidance, while registry/detail calls still succeeded.");
    }

    [Fact]
    public async Task LocalEventWriteWorks_OutlookBoundaryStaysEnforcedAndAdvertised()
    {
        using var setup = await Setup.CreateAsync(writes: true);
        var localId = Guid.NewGuid();
        var outlookId = Guid.NewGuid();
        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Add(new CalendarEntity { Id = localId, UserId = setup.UserId, Name = "Local" });
            db.Add(new CalendarEntity { Id = outlookId, UserId = setup.UserId, Name = "Bound", Source = "outlook" });
            db.Add(new OutlookCalendarBindingEntity { PimCalendarId = outlookId, ConnectionId = Guid.NewGuid(), GraphCalendarId = "test-only", CanEdit = true });
            await db.SaveChangesAsync();
        }
        var local = await setup.Call("create_event", new { calendarId = localId, title = "Local event", dtStart = "2026-10-20T01:00:00Z", dtEnd = "2026-10-20T02:00:00Z" });
        Assert.Equal(0, local.GetProperty("code").GetInt32());
        var refused = await setup.Call("create_event", new { calendarId = outlookId, title = "Must not be written", dtStart = "2026-10-20T01:00:00Z", dtEnd = "2026-10-20T02:00:00Z" }, error: true);
        Assert.Equal(400, refused.GetProperty("code").GetInt32());
        Assert.Contains("确认写回", refused.GetProperty("details").GetProperty("message").GetString());
        using var list = Rpc("tools/list", new { });
        var wire = ReadRpc(await setup.Mcp.SendAsync(list));
        var advertised = wire.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "create_event");
        Assert.Contains("does NOT expose", advertised.GetProperty("description").GetString());
        using var final = setup.Factory.Services.CreateScope();
        var rows = await final.ServiceProvider.GetRequiredService<PimDbContext>().Set<EventEntity>().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(localId, rows[0].CalendarId);
        output.WriteLine("Local create succeeded. A synthetic Outlook binding with canEdit=true was still rejected, tool discovery explained why, and no Outlook event was written. No real Outlook account was contacted.");
    }

    private static HttpRequestMessage Rpc(string method, object args)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = args }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    private static JsonElement ReadRpc(HttpResponseMessage response)
    {
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}");
        var data = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("data:", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(data is null ? text : data[5..].Trim());
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> Success(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private sealed class Setup : IDisposable
    {
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required HttpClient Rest { get; init; }
        public required HttpClient Mcp { get; init; }
        public required Guid UserId { get; init; }

        public static async Task<Setup> CreateAsync(bool writes = false)
        {
            var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("mcp-bug-journey"));
            var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "mcp-journey-user");
            var rest = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
            var created = await Success(await rest.PostAsJsonAsync("/api/v1/mcp/clients", new { name = "isolated-mcp-journey" }));
            if (writes)
            {
                var permissions = McpToolCatalog.DefaultPermissions();
                permissions["write"] = permissions["write"].ToDictionary(x => x.Key, _ => true);
                await Success(await rest.PutAsJsonAsync($"/api/v1/mcp/clients/{created.GetProperty("client").GetProperty("id").GetGuid()}", new { permissions }));
            }
            var mcp = factory.CreateClient();
            mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.GetProperty("token").GetString());
            return new Setup { Factory = factory, Rest = rest, Mcp = mcp, UserId = session.UserId };
        }

        public async Task<JsonElement> Call(string name, object args, bool error = false)
        {
            using var request = Rpc("tools/call", new { name, arguments = args });
            var wire = ReadRpc(await Mcp.SendAsync(request));
            Assert.False(wire.TryGetProperty("error", out _), wire.GetRawText());
            var result = wire.GetProperty("result");
            Assert.Equal(error, result.TryGetProperty("isError", out var flag) && flag.GetBoolean());
            using var content = JsonDocument.Parse(result.GetProperty("content").EnumerateArray().First(x => x.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            return content.RootElement.Clone();
        }

        public void Dispose() { Mcp.Dispose(); Rest.Dispose(); Factory.Dispose(); }
    }
}
