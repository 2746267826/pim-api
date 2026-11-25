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
        Assert.Contains("statuses", bad.GetProperty("error").GetString());
        var first = (await setup.Call("get_tasks", new { })).GetProperty("data");
        Assert.Equal(25, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        var second = (await setup.Call("get_tasks", new { page = 2 })).GetProperty("data");
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        var selected = (await setup.Call("get_tasks", new { status = "NEEDS-ACTION" })).GetProperty("data");
        Assert.Equal(13, selected.GetProperty("totalCount").GetInt32());
        Assert.Equal(13, selected.GetProperty("items").GetArrayLength());
        var multi = (await setup.Call("get_tasks", new
        {
            statuses = new[] { "NEEDS-ACTION", "IN-PROCESS", "NEEDS-ACTION" },
            pageSize = 100,
        })).GetProperty("data");
        Assert.Equal(25, multi.GetProperty("totalCount").GetInt32());
        Assert.Equal(25, multi.GetProperty("items").GetArrayLength());
        var empty = (await setup.Call("get_tasks", new { statuses = Array.Empty<string>() })).GetProperty("data");
        Assert.Equal(25, empty.GetProperty("totalCount").GetInt32());
        foreach (var invalid in new[]
                 {
                     new { statuses = new[] { "NEEDS-ACTION", "UNKNOWN" } },
                     new { statuses = new[] { "NEEDS-ACTION", "" } },
                     new { statuses = new[] { "needs-action" } },
                     new { statuses = new[] { " NEEDS-ACTION " } },
                 })
        {
            var rejected = await setup.Call("get_tasks", invalid, error: true);
            Assert.Equal(400, rejected.GetProperty("code").GetInt32());
            Assert.DoesNotContain("totalCount", rejected.GetRawText());
        }
        var conflict = await setup.Call("get_tasks", new
        {
            status = "NEEDS-ACTION",
            statuses = new[] { "IN-PROCESS" },
        }, error: true);
        Assert.Equal(400, conflict.GetProperty("code").GetInt32());
        output.WriteLine("25 own tasks read as 20 + 5, another owner's task excluded. Single status read 13; statuses OR read 25 with duplicates deduplicated; empty array read 25; invalid arrays and status+statuses returned MCP 400 before dispatch.");
    }

    [Fact]
    public async Task Tasks_RestRejectsInvalidStatuses_AndPreservesValidAndUnfilteredReads()
    {
        using var setup = await Setup.CreateAsync();
        var statuses = new[] { "NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED" };
        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            foreach (var status in statuses)
                db.Add(new TaskEntity { UserId = setup.UserId, Uid = $"rest-{status}", Title = status, Status = status });
            db.Add(new TaskEntity { UserId = Guid.NewGuid(), Uid = "rest-other-owner", Title = "Not mine" });
            await db.SaveChangesAsync();
        }

        foreach (var status in new[] { "NEEDS-ACTION,IN-PROCESS", "UNKNOWN", "needs-action", " NEEDS-ACTION " })
        {
            using var response = await setup.Rest.GetAsync($"/api/v1/calendar/tasks?pageSize=100&status={Uri.EscapeDataString(status)}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await AuthRegistrationTestHost.ReadJsonAsync(response);
            Assert.Equal(2057, body.GetProperty("code").GetInt32());
            Assert.Contains("NEEDS-ACTION", body.GetProperty("message").GetString());
            Assert.DoesNotContain("totalCount", body.GetRawText());
        }

        foreach (var status in statuses)
        {
            var page = await Success(await setup.Rest.GetAsync($"/api/v1/calendar/tasks?pageSize=100&status={status}"));
            Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(status, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        }

        var multi = await Success(await setup.Rest.GetAsync(
            "/api/v1/calendar/tasks?pageSize=100&status=NEEDS-ACTION&status=COMPLETED&status=NEEDS-ACTION"));
        Assert.Equal(2, multi.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { "NEEDS-ACTION", "COMPLETED" },
            multi.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("status").GetString())
                .ToArray());

        using (var mixed = await setup.Rest.GetAsync(
                   "/api/v1/calendar/tasks?pageSize=100&status=NEEDS-ACTION&status="))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
            var body = await AuthRegistrationTestHost.ReadJsonAsync(mixed);
            Assert.Equal(2057, body.GetProperty("code").GetInt32());
        }

        foreach (var suffix in new[] { "", "&status=", "&status=%20%20" })
        {
            var page = await Success(await setup.Rest.GetAsync($"/api/v1/calendar/tasks?pageSize=100{suffix}"));
            Assert.Equal(4, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(4, page.GetProperty("items").GetArrayLength());
        }
        var legacy = await Success(await setup.Rest.GetAsync("/api/v1/calendar/tasks"));
        Assert.Equal(JsonValueKind.Array, legacy.ValueKind);
        Assert.Equal(4, legacy.GetArrayLength());
        using var reread = setup.Factory.Services.CreateScope();
        Assert.Equal(5, await reread.ServiceProvider.GetRequiredService<PimDbContext>().Set<TaskEntity>().CountAsync());
        output.WriteLine("REST rejected comma-separated, unknown, lowercase, padded and empty-mixed statuses with HTTP 400/code 2057 instead of false emptiness. Repeated status produced an OR union with duplicates ignored; all four valid single statuses, unfiltered/blank paged reads, legacy arrays and user isolation remained intact; no rows were mutated.");
    }

    [Fact]
    public async Task Events_RequireBoundedWindow_AlwaysPage_AndRestMatchesMcp()
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
        var missingWindow = await setup.Rest.GetAsync("/api/v1/calendar/events");
        Assert.Equal(HttpStatusCode.BadRequest, missingWindow.StatusCode);

        var rest = await Success(await setup.Rest.GetAsync(
            $"/api/v1/calendar/events?start={args.start}&end={args.end}&page=1&pageSize=100"));
        Assert.Equal(25, rest.GetProperty("totalCount").GetInt32());
        Assert.Equal(25, rest.GetProperty("items").GetArrayLength());
        var times = rest.GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("dtStart").GetDateTimeOffset());
        foreach (var item in first.GetProperty("items").EnumerateArray())
            Assert.Equal(times[item.GetProperty("id").GetGuid()], item.GetProperty("dtStart").GetDateTimeOffset());

        var restFirst = await Success(await setup.Rest.GetAsync(
            $"/api/v1/calendar/events?start={args.start}&end={args.end}"));
        Assert.Equal(20, restFirst.GetProperty("items").GetArrayLength());
        Assert.Equal(25, restFirst.GetProperty("totalCount").GetInt32());
        var restSecond = await Success(await setup.Rest.GetAsync(
            $"/api/v1/calendar/events?start={args.start}&end={args.end}&page=2"));
        Assert.Equal(5, restSecond.GetProperty("items").GetArrayLength());

        output.WriteLine("MCP and REST both rejected unbounded history, returned PagedResult with default 20/page, and preserved all 25 timestamp instants across pages.");
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

    [Fact]
    public async Task EventTimezone_ExplicitOffsetsIanaUnknownAndIsolationHoldAcrossRestAndMcp()
    {
        using var setup = await Setup.CreateAsync(writes: true);
        var calendarId = Guid.NewGuid();
        var prefix = $"R02-timezone-{Guid.NewGuid():N}";
        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Add(new CalendarEntity { Id = calendarId, UserId = setup.UserId, Name = "R02 timezone" });
            await db.SaveChangesAsync();
        }

        var missingOffset = await setup.Rest.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-missing-offset",
            dtStart = "2026-11-01T01:30:00",
            dtEnd = "2026-11-01T02:30:00",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingOffset.StatusCode);

        var invalidZone = await setup.Rest.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-invalid-zone",
            dtStart = "2026-11-01T05:30:00Z",
            dtEnd = "2026-11-01T06:30:00Z",
            timeZoneId = "Mars/Olympus",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidZone.StatusCode);

        var firstFold = await Success(await setup.Rest.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-fold-a",
            dtStart = "2026-11-01T01:30:00-04:00",
            dtEnd = "2026-11-01T03:30:00-05:00",
            timeZoneId = "America/New_York",
        }));
        var secondFold = await Success(await setup.Rest.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-fold-b",
            dtStart = "2026-11-01T01:30:00-05:00",
            dtEnd = "2026-11-01T02:30:00-05:00",
            timeZoneId = "America/New_York",
        }));

        Assert.Equal("2026-11-01T05:30:00+00:00", firstFold.GetProperty("dtStart").GetString());
        Assert.Equal("2026-11-01T06:30:00+00:00", secondFold.GetProperty("dtStart").GetString());
        Assert.Equal("2026-11-01T01:30:00", firstFold.GetProperty("localStart").GetString());
        Assert.Equal(firstFold.GetProperty("localStart").GetString(), secondFold.GetProperty("localStart").GetString());
        Assert.Equal("resolved", firstFold.GetProperty("timeZoneResolution").GetString());
        Assert.Equal("America/New_York", firstFold.GetProperty("timeZoneId").GetString());

        var unknownZone = await Success(await setup.Rest.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-unknown-zone",
            dtStart = "2026-11-05T05:30:00Z",
            dtEnd = "2026-11-05T06:30:00Z",
        }));
        Assert.Equal("unknown", unknownZone.GetProperty("timeZoneResolution").GetString());
        Assert.Equal("unknown", unknownZone.GetProperty("timeZoneSourceType").GetString());
        Assert.Null(unknownZone.GetProperty("localStart").GetString());
        Assert.Equal("Asia/Shanghai", unknownZone.GetProperty("displayTimeZoneId").GetString());

        var allDay = await Success(await setup.Rest.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-all-day",
            dtStart = "2026-11-05T16:00:00Z",
            dtEnd = "2026-11-06T16:00:00Z",
            isAllDay = true,
            allDayStartDate = "2026-11-06",
            allDayEndDateExclusive = "2026-11-07",
            timeZoneId = "Asia/Shanghai",
        }));
        Assert.Equal("2026-11-06", allDay.GetProperty("allDayStartDate").GetString());
        Assert.Equal("2026-11-07", allDay.GetProperty("allDayEndDateExclusive").GetString());
        Assert.Null(allDay.GetProperty("localStart").GetString());

        var restPage = await Success(await setup.Rest.GetAsync(
            $"/api/v1/calendar/events?start=2026-11-01&end=2026-11-03&search={prefix}&pageSize=100"));
        Assert.Equal(2, restPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { "2026-11-01T05:30:00+00:00", "2026-11-01T06:30:00+00:00" },
            restPage.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("dtStart").GetString())
                .ToArray());

        var mcpMissingOffset = await setup.Call("create_event", new
        {
            calendarId,
            title = $"{prefix}-mcp-missing-offset",
            dtStart = "2026-11-02T01:30:00",
            dtEnd = "2026-11-02T02:30:00Z",
        }, error: true);
        Assert.Equal(400, mcpMissingOffset.GetProperty("code").GetInt32());
        Assert.Contains("explicit UTC offset", mcpMissingOffset.GetProperty("error").GetString());

        var mcpWindowsZone = await setup.Call("create_event", new
        {
            calendarId,
            title = $"{prefix}-mcp-windows-zone",
            dtStart = "2026-11-02T01:30:00Z",
            dtEnd = "2026-11-02T02:30:00Z",
            timeZoneId = "Eastern Standard Time",
        }, error: true);
        Assert.Equal(400, mcpWindowsZone.GetProperty("code").GetInt32());
        Assert.Contains("IANA", mcpWindowsZone.GetProperty("error").GetString());

        var mcpCreated = await setup.Call("create_event", new
        {
            calendarId,
            title = $"{prefix}-mcp-valid",
            dtStart = "2026-11-02T09:00:00+08:00",
            dtEnd = "2026-11-02T10:00:00+08:00",
            timeZoneId = "Asia/Shanghai",
        });
        var mcpEvent = mcpCreated.GetProperty("data");
        Assert.Equal("2026-11-02T01:00:00+00:00", mcpEvent.GetProperty("dtStart").GetString());
        Assert.Equal("2026-11-02T09:00:00", mcpEvent.GetProperty("localStart").GetString());
        Assert.Equal("Asia/Shanghai", mcpEvent.GetProperty("timeZoneId").GetString());

        var mcpPage = await setup.Call("get_events", new
        {
            start = "2026-11-02",
            end = "2026-11-03",
            pageSize = 100,
        });
        var mcpItems = mcpPage.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(mcpItems, item =>
            item.GetProperty("title").GetString() == $"{prefix}-mcp-valid"
            && item.GetProperty("timeZoneResolution").GetString() == "resolved"
            && item.GetProperty("localStart").GetString() == "2026-11-02T09:00:00");

        using (var scope = setup.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var rows = await db.Set<EventEntity>()
                .Where(e => e.CalendarId == calendarId)
                .ToListAsync();
            Assert.Equal(5, rows.Count);
            Assert.Equal(5, rows.Count(e => e.Title.StartsWith(prefix, StringComparison.Ordinal)));
            Assert.DoesNotContain(rows, e => e.Title.EndsWith("-missing-offset", StringComparison.Ordinal));
            Assert.DoesNotContain(rows, e => e.Title.EndsWith("-invalid-zone", StringComparison.Ordinal));
            Assert.DoesNotContain(rows, e => e.Title.EndsWith("-mcp-windows-zone", StringComparison.Ordinal));
        }

        var otherSession = await AuthRegistrationTestHost.SeedAndLoginAsync(
            setup.Factory,
            $"r02-other-{Guid.NewGuid():N}"[..20]);
        var otherRest = AuthRegistrationTestHost.Authed(setup.Factory, otherSession.AccessToken);
        var otherPage = await Success(await otherRest.GetAsync(
            $"/api/v1/calendar/events?start=2026-11-01&end=2026-11-08&search={prefix}&pageSize=100"));
        Assert.Equal(0, otherPage.GetProperty("totalCount").GetInt32());

        output.WriteLine("REST and MCP rejected offset-less/Windows-zone writes before persistence, kept two fold instants distinct, returned IANA/local/unknown/all-day semantics, normalized MCP writes to UTC, and isolated another account.");
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
