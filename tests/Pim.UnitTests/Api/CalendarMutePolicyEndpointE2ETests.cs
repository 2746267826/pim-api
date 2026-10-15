using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.UnitTests.Harness.RealDb;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Api;

/// <summary>
/// 工单 04 E1.3：<c>PUT /api/v1/calendar/calendars/{id}/mute-policy</c> 的入口行为回归（真宿主 + 隔离真库）。
///
/// <para>
/// <b>为什么必须是真宿主 + 真库</b>：这条端点的风险不是「字段没存进去」，而是
/// <b>请求体缺字段时被 JSON 绑定成 false，于是整个日历本下的日程被静默改成「不静音」</b>
/// （独立 review 一轮 I-1）。这种缺陷只有在「真实路由 + 真实反序列化 + 真实 SQL 读回」下才看得出来：
/// 直接调服务层会绕开绑定，InMemory 也看不出批量写入的真实结果。
/// </para>
///
/// <para><b>隔离</b>：每个用例在 <c>PIM_TEST_CONN</c> 指向的服务器上建一次性库并跑真实迁移链，
/// 结束时 DROP；生产库与镜像库不会被触碰。</para>
///
/// <para><b>认证边界（如实说明）</b>：带 token 的用例从宿主 DI 取 <see cref="JwtService"/>
/// 直接签发 access token，因此证明的是「带本宿主认可签名的请求能进入受保护 handler」，
/// 不证明注册/登录链路。</para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class CalendarMutePolicyEndpointE2ETests
{
    private readonly ITestOutputHelper _output;

    public CalendarMutePolicyEndpointE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task<(TempMigrationDatabase Db, WebApplicationFactory<Program> Factory)> NewIsolatedHostAsync()
    {
        // 模块程序集必须先登记，否则宿主 context 里没有日历模块的实体
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);

        var temp = await TempMigrationDatabase.CreateAsync("test_calendar_mute_policy");
        await temp.MigrateAsync();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("ConnectionStrings:DefaultConnection", temp.ConnectionString);
            builder.UseSetting(
                "DataProtection:KeysPath",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pim-dp-{Guid.NewGuid():N}"));
        });

        return (temp, factory);
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory, Guid userId)
    {
        var jwt = factory.Services.GetRequiredService<JwtService>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.GenerateAccessToken(userId, $"mute-{userId:N}", "user"));
        return client;
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    /// <summary>建日历本 + 三条日程（静音 / 不静音 / 未指定），返回日历本 id。</summary>
    private static async Task<Guid> SeedAsync(HttpClient client)
    {
        var calendarResponse = await client.PostAsync(
            "/api/v1/calendar/calendars",
            Json("""{"name":"课程表(静音)","color":"#3B82F6"}"""));
        Assert.Equal(HttpStatusCode.Created, calendarResponse.StatusCode);
        var calendarId = (await ReadDataAsync(calendarResponse)).GetProperty("id").GetGuid();

        foreach (var (title, isMutedJson) in new[]
                 {
                     ("A-静音", ",\"isMuted\":true"),
                     ("B-不静音", ",\"isMuted\":false"),
                     ("C-未指定", string.Empty)
                 })
        {
            var body =
                $$"""
                {"calendarId":"{{calendarId}}","title":"{{title}}",
                 "dtStart":"2026-11-02T09:00:00+08:00","dtEnd":"2026-11-02T10:00:00+08:00"{{isMutedJson}}}
                """;
            var created = await client.PostAsync("/api/v1/calendar/events", Json(body));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        return calendarId;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<(bool DefaultMuted, string EventMutes)> ReadPersistedAsync(
        TempMigrationDatabase temp, Guid calendarId)
    {
        await using var command = temp.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT default_muted::text FROM calendars WHERE id = @id";
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "id";
        idParameter.Value = calendarId;
        command.Parameters.Add(idParameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        var defaultMuted = (string)(await command.ExecuteScalarAsync())! == "true";

        await using var eventCommand = temp.Db.Database.GetDbConnection().CreateCommand();
        eventCommand.CommandText =
            "SELECT title || '=' || COALESCE(is_muted::text, 'NULL') FROM events WHERE calendar_id = @id ORDER BY title";
        var eventParameter = eventCommand.CreateParameter();
        eventParameter.ParameterName = "id";
        eventParameter.Value = calendarId;
        eventCommand.Parameters.Add(eventParameter);
        if (eventCommand.Connection!.State != System.Data.ConnectionState.Open)
        {
            await eventCommand.Connection.OpenAsync();
        }

        var mutes = new List<string>();
        await using var reader = await eventCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            mutes.Add(reader.GetString(0));
        }

        // 用拼接字符串而不是 List：元组相等性对 List 是引用比较，断言会恒假（本文件第一版就踩了这个坑）
        return (defaultMuted, string.Join(", ", mutes));
    }

    /// <summary>
    /// I-1 的入口回归：请求体缺少 <c>isMuted</c>（<c>{}</c>）必须 400，且**一个字段都不许改**。
    ///
    /// 这是本工单最危险的一条：JSON 绑定会把缺失字段变成 <c>false</c>，
    /// 静默把整个日历本统一成「不静音」。
    /// </summary>
    [SkippableFact]
    public async Task MissingIsMuted_IsRejected_AndNothingChanges()
    {
        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var factoryScope = factory;
        var client = Authed(factory, Guid.NewGuid());
        var calendarId = await SeedAsync(client);
        var before = await ReadPersistedAsync(temp, calendarId);

        var response = await client.PutAsync(
            $"/api/v1/calendar/calendars/{calendarId}/mute-policy", Json("{}"));

        _output.WriteLine($"PUT {{}} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var after = await ReadPersistedAsync(temp, calendarId);
        Assert.Equal(before, after);
    }

    /// <summary>显式 null 也不许被当成 false（契约里 nullable 只是绑定形态，语义上必填）。</summary>
    [SkippableFact]
    public async Task ExplicitNullIsMuted_IsRejected_AndNothingChanges()
    {
        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var factoryScope = factory;
        var client = Authed(factory, Guid.NewGuid());
        var calendarId = await SeedAsync(client);
        var before = await ReadPersistedAsync(temp, calendarId);

        var response = await client.PutAsync(
            $"/api/v1/calendar/calendars/{calendarId}/mute-policy", Json("""{"isMuted":null}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ReadPersistedAsync(temp, calendarId));
    }

    /// <summary>合法请求：统一设为静音 → 默认值与既有日程（含显式 false 的那条）全部生效。</summary>
    [SkippableFact]
    public async Task ValidRequest_AppliesToCalendarDefaultAndExistingEvents()
    {
        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var factoryScope = factory;
        var client = Authed(factory, Guid.NewGuid());
        var calendarId = await SeedAsync(client);

        var response = await client.PutAsync(
            $"/api/v1/calendar/calendars/{calendarId}/mute-policy", Json("""{"isMuted":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.True(data.GetProperty("defaultMuted").GetBoolean());
        Assert.Equal(3, data.GetProperty("affectedEventCount").GetInt32());

        var (defaultMuted, mutes) = await ReadPersistedAsync(temp, calendarId);
        Assert.True(defaultMuted);
        Assert.Equal("A-静音=true, B-不静音=true, C-未指定=true", mutes);
        _output.WriteLine($"after: default_muted={defaultMuted}, events={mutes}");
    }

    /// <summary>只影响自己的日历本：另一个用户拿同一 id 调用必须被拒且数据不变。</summary>
    [SkippableFact]
    public async Task OtherUser_CannotChangePolicy()
    {
        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var factoryScope = factory;
        var owner = Authed(factory, Guid.NewGuid());
        var calendarId = await SeedAsync(owner);
        var before = await ReadPersistedAsync(temp, calendarId);

        var stranger = Authed(factory, Guid.NewGuid());
        var response = await stranger.PutAsync(
            $"/api/v1/calendar/calendars/{calendarId}/mute-policy", Json("""{"isMuted":true}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ReadPersistedAsync(temp, calendarId));
    }
}
