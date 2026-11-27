using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// R02 的真实 PostgreSQL + 真实 HTTP 宿主回归：显式偏移经过 Npgsql 后仍是同一个
/// 绝对时刻，DST 回拨的两个 fold 不会被折叠，全天事件只按独立日期返回，旧数据
/// 不会被猜测成某个来源时区。
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class CalendarEventTimezoneRealDbE2ETests
{
    private static WebApplicationFactory<Program> CreateFactory(string connectionString)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("Auth:AllowPublicRegistration", "true");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting(
                "DataProtection:KeysPath",
                Path.Combine(Path.GetTempPath(), $"pim-r02-dp-{Guid.NewGuid():N}"));
        });

    private static async Task<Guid> CreateCalendarAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/calendar/calendars", new
        {
            name,
            color = "#3B82F6",
        });
        var data = await Success(response);
        return data.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Success(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static string Query(string start, string end, string search)
        => $"/api/v1/calendar/events?start={Uri.EscapeDataString(start)}"
           + $"&end={Uri.EscapeDataString(end)}"
           + $"&search={Uri.EscapeDataString(search)}&pageSize=100";

    private static DateTimeOffset Instant(JsonElement value)
        => DateTimeOffset.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    [SkippableFact]
    public async Task FoldAllDayAndEquivalentWindows_AreDeterministicAcrossRealPostgres()
    {
        var connectionString = RealDbTestConnection.Require();
        using var factory = CreateFactory(connectionString);
        var owner = await AuthRegistrationTestHost.RegisterOkAsync(
            factory, $"r02tz_{Guid.NewGuid():N}"[..20]);
        using var client = AuthRegistrationTestHost.Authed(factory, owner.AccessToken);
        var calendarId = await CreateCalendarAsync(client, "R02 timezone real db");
        var prefix = $"r02tz-{Guid.NewGuid():N}";

        var firstFold = await Success(await client.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-fold-first",
            dtStart = "2026-11-01T01:30:00-04:00",
            dtEnd = "2026-11-01T03:30:00-05:00",
            timeZoneId = "America/New_York",
        }));
        var secondFold = await Success(await client.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-fold-second",
            dtStart = "2026-11-01T01:30:00-05:00",
            dtEnd = "2026-11-01T02:30:00-05:00",
            timeZoneId = "America/New_York",
        }));

        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            Instant(firstFold.GetProperty("dtStart")));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero),
            Instant(secondFold.GetProperty("dtStart")));
        Assert.Equal("2026-11-01T01:30:00", firstFold.GetProperty("localStart").GetString());
        Assert.Equal("2026-11-01T01:30:00", secondFold.GetProperty("localStart").GetString());
        Assert.Equal("2026-11-01T13:30:00", firstFold.GetProperty("displayLocalStart").GetString());
        Assert.Equal("America/New_York", firstFold.GetProperty("timeZoneId").GetString());
        Assert.Equal("resolved", firstFold.GetProperty("timeZoneResolution").GetString());

        var allDay = await Success(await client.PostAsJsonAsync("/api/v1/calendar/events", new
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
        Assert.Null(allDay.GetProperty("displayLocalStart").GetString());

        var unknownZone = await Success(await client.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-unknown-zone",
            dtStart = "2026-11-10T05:30:00Z",
            dtEnd = "2026-11-10T06:30:00Z",
        }));
        Assert.Equal("unknown", unknownZone.GetProperty("timeZoneResolution").GetString());
        Assert.Equal("unknown", unknownZone.GetProperty("timeZoneSourceType").GetString());
        Assert.Null(unknownZone.GetProperty("localStart").GetString());
        Assert.Equal("2026-11-10T13:30:00", unknownZone.GetProperty("displayLocalStart").GetString());

        var expectedFoldStarts = new[] { "2026-11-01T05:30:00Z", "2026-11-01T06:30:00Z" };
        foreach (var (start, end) in new[]
                 {
                     ("2026-11-01", "2026-11-02"),
                     ("2026-11-01T00:00:00Z", "2026-11-02T00:00:00Z"),
                     ("2026-11-01T08:00:00+08:00", "2026-11-02T08:00:00+08:00"),
                 })
        {
            var page = await Success(await client.GetAsync(Query(start, end, prefix)));
            Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(
                expectedFoldStarts,
                page.GetProperty("items").EnumerateArray()
                    .Select(item => Instant(item.GetProperty("dtStart")).UtcDateTime)
                    .OrderBy(value => value)
                    .Select(value => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
                    .ToArray());
        }

        var allDayPage = await Success(await client.GetAsync(Query("2026-11-06", "2026-11-07", prefix)));
        var allDayItem = Assert.Single(
            allDayPage.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("title").GetString() == $"{prefix}-all-day");
        Assert.Equal("2026-11-06", allDayItem.GetProperty("allDayStartDate").GetString());
        Assert.Equal("2026-11-07", allDayItem.GetProperty("allDayEndDateExclusive").GetString());

        // end 是排他边界：05:30Z 开始的 fold 不能在“恰好到 05:30Z”的窗口中出现。
        var boundaryPage = await Success(
            await client.GetAsync(Query("2026-11-01T00:00:00Z", "2026-11-01T05:30:00Z", prefix)));
        Assert.Equal(0, boundaryPage.GetProperty("totalCount").GetInt32());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            Assert.Equal(4, await db.Set<EventEntity>()
                .CountAsync(item => item.CalendarId == calendarId && item.Title.StartsWith(prefix)));
        }

        var other = await AuthRegistrationTestHost.RegisterOkAsync(
            factory, $"r02tz_{Guid.NewGuid():N}"[..20]);
        using var otherClient = AuthRegistrationTestHost.Authed(factory, other.AccessToken);
        var otherPage = await Success(await otherClient.GetAsync(Query("2026-11-01", "2026-11-12", prefix)));
        Assert.Equal(0, otherPage.GetProperty("totalCount").GetInt32());
    }

    [SkippableFact]
    public async Task InvalidWritesAndWindows_Return400WithoutPersistence_AndLegacyRowsStayUnknown()
    {
        var connectionString = RealDbTestConnection.Require();
        using var factory = CreateFactory(connectionString);
        var owner = await AuthRegistrationTestHost.RegisterOkAsync(
            factory, $"r02tz_{Guid.NewGuid():N}"[..20]);
        using var client = AuthRegistrationTestHost.Authed(factory, owner.AccessToken);
        var calendarId = await CreateCalendarAsync(client, "R02 timezone validation real db");
        var prefix = $"r02tz-bad-{Guid.NewGuid():N}";

        foreach (var body in new object[]
                 {
                     new
                     {
                         calendarId,
                         title = $"{prefix}-missing-offset",
                         dtStart = "2026-11-01T01:30:00",
                         dtEnd = "2026-11-01T02:30:00Z",
                     },
                     new
                     {
                         calendarId,
                         title = $"{prefix}-invalid-zone",
                         dtStart = "2026-11-01T01:30:00Z",
                         dtEnd = "2026-11-01T02:30:00Z",
                         timeZoneId = "Mars/Olympus",
                     },
                     new
                     {
                         calendarId,
                         title = $"{prefix}-bad-date",
                         dtStart = "2026-02-31T01:30:00Z",
                         dtEnd = "2026-02-31T02:30:00Z",
                     },
                 })
        {
            var rejected = await client.PostAsJsonAsync("/api/v1/calendar/events", body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        // 失败后的同一客户端必须能恢复，而不是被上一次请求污染。
        var valid = await Success(await client.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-valid",
            dtStart = "2026-11-20T09:00:00+08:00",
            dtEnd = "2026-11-20T10:00:00+08:00",
            timeZoneId = "Asia/Shanghai",
        }));
        Assert.Equal("2026-11-20T01:00:00+00:00", valid.GetProperty("dtStart").GetString());
        Assert.Equal("2026-11-20T09:00:00", valid.GetProperty("localStart").GetString());

        foreach (var (start, end) in new[]
                 {
                     ("2026-11-01T00:00:00Z", (string?)null),
                     ("2026-11-02T00:00:00Z", "2026-11-01T00:00:00Z"),
                     ("2026-01-01T00:00:00Z", "2027-01-02T00:00:01Z"),
                 })
        {
            var query = $"/api/v1/calendar/events?start={Uri.EscapeDataString(start)}&pageSize=100";
            if (end is not null)
                query += $"&end={Uri.EscapeDataString(end)}";
            var rejected = await client.GetAsync(query);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal(02009, json.RootElement.GetProperty("code").GetInt32());
        }

        // 366 天整边界允许；超过一秒由上一个循环覆盖。
        var exactWindow = await client.GetAsync(
            "/api/v1/calendar/events?start=2026-01-01T00:00:00Z&end=2027-01-02T00:00:00Z&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, exactWindow.StatusCode);

        var legacySourceOnly = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = $"{prefix}-legacy-source-only",
            Title = $"{prefix}-legacy-source-only",
            DtStart = new DateTimeOffset(2026, 11, 10, 5, 30, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 11, 10, 6, 30, 0, TimeSpan.Zero),
            SourceTimeZoneId = "Outlook Custom Zone",
        };
        var legacyAllDayWithoutDates = new EventEntity
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Uid = $"{prefix}-legacy-all-day",
            Title = $"{prefix}-legacy-all-day",
            DtStart = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero),
            IsAllDay = true,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Set<EventEntity>().AddRange(legacySourceOnly, legacyAllDayWithoutDates);
            await db.SaveChangesAsync();

            Assert.Equal(0, await db.Set<EventEntity>()
                .CountAsync(item => item.Title.StartsWith($"{prefix}-missing-offset")
                    || item.Title.StartsWith($"{prefix}-invalid-zone")
                    || item.Title.StartsWith($"{prefix}-bad-date")));
        }

        var legacyPage = await Success(await client.GetAsync(Query("2026-11-10", "2026-11-13", prefix)));
        Assert.Equal(2, legacyPage.GetProperty("totalCount").GetInt32());

        var sourceOnly = Assert.Single(
            legacyPage.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("title").GetString() == $"{prefix}-legacy-source-only");
        Assert.Equal(
            new DateTimeOffset(2026, 11, 10, 5, 30, 0, TimeSpan.Zero),
            Instant(sourceOnly.GetProperty("dtStart")));
        Assert.Equal("sourceOnly", sourceOnly.GetProperty("timeZoneResolution").GetString());
        Assert.Null(sourceOnly.GetProperty("timeZoneId").GetString());
        Assert.Equal("Outlook Custom Zone", sourceOnly.GetProperty("sourceTimeZoneId").GetString());
        Assert.Equal("unknown", sourceOnly.GetProperty("timeZoneSourceType").GetString());
        Assert.Null(sourceOnly.GetProperty("localStart").GetString());
        Assert.Equal("2026-11-10T13:30:00", sourceOnly.GetProperty("displayLocalStart").GetString());

        var legacyAllDay = Assert.Single(
            legacyPage.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("title").GetString() == $"{prefix}-legacy-all-day");
        Assert.Equal("unknown", legacyAllDay.GetProperty("timeZoneResolution").GetString());
        Assert.Null(legacyAllDay.GetProperty("allDayStartDate").GetString());
        Assert.Null(legacyAllDay.GetProperty("allDayEndDateExclusive").GetString());
        Assert.Null(legacyAllDay.GetProperty("localStart").GetString());
        Assert.Null(legacyAllDay.GetProperty("displayLocalStart").GetString());

        var other = await AuthRegistrationTestHost.RegisterOkAsync(
            factory, $"r02tz_{Guid.NewGuid():N}"[..20]);
        using var otherClient = AuthRegistrationTestHost.Authed(factory, other.AccessToken);
        var otherPage = await Success(await otherClient.GetAsync(Query("2026-11-10", "2026-11-13", prefix)));
        Assert.Equal(0, otherPage.GetProperty("totalCount").GetInt32());
    }

    [SkippableFact]
    public async Task MissingStartsAreRejectedBeforePersistenceAndExplicitNullTimezoneClears()
    {
        var connectionString = RealDbTestConnection.Require();
        using var factory = CreateFactory(connectionString);
        var owner = await AuthRegistrationTestHost.RegisterOkAsync(
            factory, $"r02tz_{Guid.NewGuid():N}"[..20]);
        using var client = AuthRegistrationTestHost.Authed(factory, owner.AccessToken);
        var calendarId = await CreateCalendarAsync(client, "R02 missing start real db");
        var prefix = $"r02tz-null-{Guid.NewGuid():N}";

        foreach (var body in new object[]
                 {
                     new
                     {
                         calendarId,
                         title = $"{prefix}-missing-start",
                         dtEnd = "2026-11-20T10:00:00Z",
                     },
                     new
                     {
                         calendarId,
                         title = $"{prefix}-missing-end",
                         dtStart = "2026-11-20T09:00:00Z",
                     },
                 })
        {
            var rejected = await client.PostAsJsonAsync("/api/v1/calendar/events", body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            Assert.Equal(0, await db.Set<EventEntity>()
                .CountAsync(item => item.Title.StartsWith(prefix)));
        }

        var created = await Success(await client.PostAsJsonAsync("/api/v1/calendar/events", new
        {
            calendarId,
            title = $"{prefix}-original",
            dtStart = "2026-11-20T09:00:00+08:00",
            dtEnd = "2026-11-20T10:00:00+08:00",
            timeZoneId = "China Standard Time",
        }));
        var eventId = created.GetProperty("id").GetGuid();

        var rejectedPut = await client.PutAsJsonAsync($"/api/v1/calendar/events/{eventId}", new
        {
            calendarId,
            title = $"{prefix}-changed",
            dtEnd = "2026-11-20T10:00:00+08:00",
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedPut.StatusCode);

        var unchanged = await Success(await client.GetAsync($"/api/v1/calendar/events/{eventId}"));
        Assert.Equal($"{prefix}-original", unchanged.GetProperty("title").GetString());
        Assert.Equal("Asia/Shanghai", unchanged.GetProperty("timeZoneId").GetString());

        var kept = await Success(await client.PutAsJsonAsync($"/api/v1/calendar/events/{eventId}", new
        {
            calendarId,
            title = $"{prefix}-omitted",
            dtStart = "2026-11-20T09:00:00+08:00",
            dtEnd = "2026-11-20T10:00:00+08:00",
        }));
        Assert.Equal("Asia/Shanghai", kept.GetProperty("timeZoneId").GetString());
        Assert.Equal("China Standard Time", kept.GetProperty("sourceTimeZoneId").GetString());

        var cleared = await Success(await client.PutAsJsonAsync($"/api/v1/calendar/events/{eventId}", new
        {
            calendarId,
            title = $"{prefix}-cleared",
            dtStart = "2026-11-20T09:00:00+08:00",
            dtEnd = "2026-11-20T10:00:00+08:00",
            timeZoneId = (string?)null,
        }));
        Assert.Null(cleared.GetProperty("timeZoneId").GetString());
        Assert.Null(cleared.GetProperty("sourceTimeZoneId").GetString());
        Assert.Equal("unknown", cleared.GetProperty("timeZoneResolution").GetString());
        Assert.Null(cleared.GetProperty("localStart").GetString());
    }
}
