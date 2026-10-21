using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Middleware;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 契约语义 §2（docs/client-contract-semantics.md）：写操作幂等。
///   1. 同 key 重放 → 返回首次的成功响应，**不重复执行副作用**；
///   2. 不同 key → 正常执行（两次两个任务）；
///   3. 首次失败（4xx）→ 不缓存，同 key 重试会重新执行；
///   4. 同 key 换端点 → 互不影响；
///   5. 同 key 换用户 → 互不影响（不泄露另一个用户的响应）；
///   6. 首次仍在执行中 → 409 / code=40902；
///   7. 读操作忽略该请求头。
/// </summary>
public sealed class IdempotencyMiddlewareE2ETests
{
    private const string Tasks = "/api/v1/calendar/tasks";
    private const string KeyHeader = "Idempotency-Key";

    private static HttpClient Authed(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, string Token)> ArrangeAsync(
        string username)
    {
        var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("idem-" + username));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, username);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return (factory, client, session.AccessToken);
    }

    private static HttpRequestMessage Post(string path, object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add(KeyHeader, key);
        return request;
    }

    private static async Task<int> CountTasksAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        return await db.Set<Pim.Module.Calendar.Entities.TaskEntity>().IgnoreQueryFilters().CountAsync();
    }

    [Fact]
    public async Task SameKey_ReplaysFirstResponse_WithoutDuplicateSideEffect()
    {
        var (factory, client, _) = await ArrangeAsync("idem-a");
        using var factoryScope = factory;
        var key = Guid.NewGuid().ToString();
        var body = new { title = "幂等任务", priority = 0 };

        var first = await client.SendAsync(Post(Tasks, body, key));
        var second = await client.SendAsync(Post(Tasks, body, key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var a = await AuthRegistrationTestHost.ReadJsonAsync(first);
        var b = await AuthRegistrationTestHost.ReadJsonAsync(second);
        Assert.Equal(a.GetProperty("data").GetProperty("id").GetString(),
                     b.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal(1, await CountTasksAsync(factory));

        // 客户端靠这个头判断「本次没有重复执行」（例如不要重放提交动画）；
        // 只断言正文相同的话，删掉 ReplayAsync 里设置该头的那行也不会有测试失败。
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.True(second.Headers.TryGetValues("Idempotent-Replayed", out var replayed));
        Assert.Equal("true", Assert.Single(replayed!));
    }

    [Fact]
    public async Task DifferentKeys_ExecuteTwice()
    {
        var (factory, client, _) = await ArrangeAsync("idem-b");
        using var factoryScope = factory;
        var body = new { title = "两次", priority = 0 };

        await client.SendAsync(Post(Tasks, body, Guid.NewGuid().ToString()));
        await client.SendAsync(Post(Tasks, body, Guid.NewGuid().ToString()));

        Assert.Equal(2, await CountTasksAsync(factory));
    }

    [Fact]
    public async Task NoKey_BehavesAsBefore()
    {
        var (factory, client, _) = await ArrangeAsync("idem-c");
        using var factoryScope = factory;
        var body = new { title = "无键", priority = 0 };

        await client.SendAsync(Post(Tasks, body, null));
        await client.SendAsync(Post(Tasks, body, null));

        Assert.Equal(2, await CountTasksAsync(factory));
    }

    [Fact]
    public async Task FailedRequest_IsNotCached_AndKeyCanBeRetried()
    {
        var (factory, client, _) = await ArrangeAsync("idem-d");
        using var factoryScope = factory;
        var key = Guid.NewGuid().ToString();

        // 首次：calendarId 指向不存在的日历 → 400 / 02003（不缓存）
        var bad = await client.SendAsync(Post(Tasks, new { title = "先失败", priority = 0, calendarId = Guid.NewGuid() }, key));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // 同 key 重试一个合法请求 → 必须真的执行
        var good = await client.SendAsync(Post(Tasks, new { title = "重试", priority = 0 }, key));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal(1, await CountTasksAsync(factory));
    }

    [Fact]
    public async Task SameKey_DifferentUser_DoesNotLeakOtherUsersResponse()
    {
        var (factoryA, clientA, _) = await ArrangeAsync("idem-user-a");
        using var _fa = factoryA;
        var key = Guid.NewGuid().ToString();
        var created = await clientA.SendAsync(Post(Tasks, new { title = "A 的任务", priority = 0 }, key));
        var aId = (await AuthRegistrationTestHost.ReadJsonAsync(created)).GetProperty("data").GetProperty("id").GetString();

        // 同一个 factory（同一库）里再建一个用户
        var sessionB = await AuthRegistrationTestHost.SeedAndLoginAsync(factoryA, "idem-user-b");
        var clientB = Authed(factoryA, sessionB.AccessToken);
        var createdB = await clientB.SendAsync(Post(Tasks, new { title = "B 的任务", priority = 0 }, key));
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);
        var bId = (await AuthRegistrationTestHost.ReadJsonAsync(createdB)).GetProperty("data").GetProperty("id").GetString();
        Assert.NotEqual(aId, bId);
    }

    [Fact]
    public async Task InProgress_Returns409WithCode40902()
    {
        var (factory, client, _) = await ArrangeAsync("idem-e");
        using var factoryScope = factory;
        var key = Guid.NewGuid().ToString();

        // 直接种一条「执行中」记录，模拟并发中的首次请求
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var session = await AuthRegistrationTestHost.LoginOkAsync(factory, "idem-e");
            db.IdempotencyRecords.Add(new IdempotencyRecordEntity
            {
                UserId = session.UserId,
                Method = "POST",
                Route = Tasks,
                Key = key,
                State = "in_progress",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Post(Tasks, new { title = "并发", priority = 0 }, key));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(40902, (await AuthRegistrationTestHost.ReadJsonAsync(response)).GetProperty("code").GetInt32());
        Assert.Equal(0, await CountTasksAsync(factory));
    }

    /// <summary>
    /// 进程在「占位已落库、响应还没写回」时被杀掉，会留下一条过期时间停在过去的 in_progress 行。
    /// 这种行**不能**让同一个 key 一直 409 —— 过期占位不再算「正在执行」，重试必须真正执行。
    /// </summary>
    [Fact]
    public async Task ExpiredInProgressPlaceholder_DoesNotBlockRetry()
    {
        var (factory, client, _) = await ArrangeAsync("idem-expired");
        using var factoryScope = factory;
        var key = Guid.NewGuid().ToString();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var session = await AuthRegistrationTestHost.LoginOkAsync(factory, "idem-expired");
            db.IdempotencyRecords.Add(new IdempotencyRecordEntity
            {
                UserId = session.UserId,
                Method = "POST",
                Route = Tasks,
                Key = key,
                State = "in_progress",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-11),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Post(Tasks, new { title = "崩溃后重试", priority = 0 }, key));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await CountTasksAsync(factory));
    }

    /// <summary>
    /// review P1-1：设备码轮询返回同一个 user code，且是**有状态**轮询。
    /// 一旦参与幂等缓存，同 key 的第二次轮询会永远拿到首次的 waiting 状态，
    /// 凭据还会明文落库 24h。这里验证：成功轮询两次都不重放，也没有任何缓存行。
    /// </summary>
    [Fact]
    public async Task DeviceCodePoll_IsNeverReplayedOrCached()
    {
        var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("idem-poll"));
        using var factoryScope = factory;
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "idem-poll");
        var client = Authed(factory, session.AccessToken);
        using var clientScope = client;

        var connection = new Pim.Module.Calendar.Entities.OutlookConnectionEntity
        {
            UserId = session.UserId,
            Status = "not-connected",
        };
        var authSession = new Pim.Module.Calendar.Entities.OutlookAuthorizationSessionEntity
        {
            UserId = session.UserId,
            ConnectionId = connection.Id,
            Status = "waiting-for-user",
            UserCode = "SECRET-USER-CODE",
            VerificationUri = "https://example.test/device",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.Set<Pim.Module.Calendar.Entities.OutlookConnectionEntity>().Add(connection);
            db.Set<Pim.Module.Calendar.Entities.OutlookAuthorizationSessionEntity>().Add(authSession);
            await db.SaveChangesAsync();
        }

        var key = Guid.NewGuid().ToString();
        var body = new { sessionId = authSession.Id };
        var first = await client.SendAsync(Post("/api/v1/calendar/outlook/device-code/poll", body, key));
        var second = await client.SendAsync(Post("/api/v1/calendar/outlook/device-code/poll", body, key));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains(IdempotencyMiddleware.ReplayedHeader),
            "设备码轮询被重放了：客户端会永远看到首次的 waiting 状态");
        Assert.Contains("SECRET-USER-CODE", await second.Content.ReadAsStringAsync());

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<PimDbContext>();
        var rows = await verifyDb.IdempotencyRecords
            .Where(r => r.UserId == session.UserId && r.Route == "/api/v1/calendar/outlook/device-code/poll")
            .ToListAsync();
        Assert.Empty(rows);
    }

    /// <summary>
    /// review P3-1：豁免端点的契约承诺是「忽略该请求头」。超长 key 的 400 不能出现在豁免判断之前，
    /// 否则客户端多发一个长 key 就会打断设备码/登录这类端点。
    /// </summary>
    [Fact]
    public async Task OverlongKey_IsRejectedOnlyWhereIdempotencyApplies()
    {
        var (factory, client, _) = await ArrangeAsync("idem-longkey");
        using var factoryScope = factory;
        var overlong = new string('k', 200);

        // 非豁免端点：仍然正常校验（400 / 40002）。
        var tasks = await client.SendAsync(Post(Tasks, new { title = "长 key", priority = 0 }, overlong));
        Assert.Equal(HttpStatusCode.BadRequest, tasks.StatusCode);
        Assert.Contains("40002", await tasks.Content.ReadAsStringAsync());

        // 豁免端点：忽略该头，按业务结果返回（不存在的会话 → 404），而不是 40002。
        var poll = await client.SendAsync(Post("/api/v1/calendar/outlook/device-code/poll",
            new { sessionId = Guid.NewGuid() }, overlong));
        Assert.Equal(HttpStatusCode.NotFound, poll.StatusCode);
    }

    [Fact]
    public async Task ReadRequest_IgnoresKey()
    {
        var (factory, client, _) = await ArrangeAsync("idem-f");
        using var factoryScope = factory;
        var request = new HttpRequestMessage(HttpMethod.Get, Tasks + "?page=1&pageSize=1");
        request.Headers.Add(KeyHeader, "ignored");
        request.Headers.Add(KeyHeader, "ignored");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
