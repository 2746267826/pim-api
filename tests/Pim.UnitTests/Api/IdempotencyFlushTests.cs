using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Middleware;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 幂等中间件在**下游已 Flush 正文**时的行为（review P2-1 的测试缺口）。
///
/// <para>
/// 正常请求下捕获流只缓冲、不写给客户端，中间件在 finally 里统一回写。
/// 但下游如果先 Flush（或 <c>StartAsync</c>），响应已经「开始」，回写路径更容易被误删。
/// 这里用真实的中间件 + InMemory 库验证：正文仍完整到达客户端，并且被缓存用于重放。
/// </para>
/// <para>
/// 同时覆盖 review P2-2 的判定依据：没有 <see cref="IAuthorizeData"/> 的端点（匿名 / OpsKey）
/// 即使带着「已认证用户」也不进入幂等链路。
/// </para>
/// </summary>
public sealed class IdempotencyFlushTests
{
    private const string Path = "/api/v1/test/flush";

    private sealed class FixedUser(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private static ServiceProvider CreateServices(Guid userId)
    {
        // 库名必须在 lambda 之外固定：AddDbContext 的配置委托按 scope 执行，
        // 写在里面会让每次 CreateScope 都换一个库，等于每次请求都看不到上一次的幂等记录。
        var databaseName = $"idem-flush-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddSingleton<ICurrentUserService>(new FixedUser(userId));
        return services.BuildServiceProvider();
    }

    private static Endpoint Authorized()
        => new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(Path),
            order: 0,
            new EndpointMetadataCollection(new AuthorizeAttribute()),
            displayName: "flush-test");

    private static Endpoint Anonymous()
        => new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(Path),
            order: 0,
            new EndpointMetadataCollection(new AllowAnonymousAttribute()),
            displayName: "anon-test");

    private static DefaultHttpContext NewContext(ServiceProvider services, Endpoint endpoint, string key)
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = Path;
        ctx.Request.Headers[IdempotencyMiddleware.HeaderName] = key;
        ctx.Response.Body = new MemoryStream();
        ctx.SetEndpoint(endpoint);
        return ctx;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static IdempotencyMiddleware Middleware(Action onCall)
        => new(async ctx =>
        {
            onCall();
            await ctx.Response.WriteAsync("{\"code\":0,\"data\":\"ok\"}");
            // 模拟「下游已经 flush / 响应已开始」：回写路径不能因此被跳过。
            await ctx.Response.Body.FlushAsync();
            ctx.Response.Body.Flush();
        }, NullLogger<IdempotencyMiddleware>.Instance);

    [Fact]
    public async Task DownstreamFlush_BodyReachesClient_AndIsCached()
    {
        var userId = Guid.NewGuid();
        using var services = CreateServices(userId);
        var key = Guid.NewGuid().ToString();
        var calls = 0;
        var middleware = Middleware(() => calls++);

        var first = NewContext(services, Authorized(), key);
        await middleware.InvokeAsync(first);
        Assert.Equal("{\"code\":0,\"data\":\"ok\"}", await ReadBodyAsync(first));
        Assert.False(first.Response.Headers.ContainsKey(IdempotencyMiddleware.ReplayedHeader));

        // 第二次同 key：不执行下游，返回同一正文 + 重放标记。
        var second = NewContext(services, Authorized(), key);
        await middleware.InvokeAsync(second);
        Assert.Equal("{\"code\":0,\"data\":\"ok\"}", await ReadBodyAsync(second));
        Assert.Equal("true", second.Response.Headers[IdempotencyMiddleware.ReplayedHeader].ToString());
        Assert.Equal(1, calls);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var record = await db.IdempotencyRecords.SingleAsync(r => r.Key == key);
        Assert.Equal("completed", record.State);
        Assert.Equal("{\"code\":0,\"data\":\"ok\"}", record.ResponseBody);
    }

    [Fact]
    public async Task AnonymousEndpoint_WithAuthenticatedUser_IsNotRecorded()
    {
        var userId = Guid.NewGuid();
        using var services = CreateServices(userId);
        var key = Guid.NewGuid().ToString();
        var calls = 0;
        var middleware = Middleware(() => calls++);

        await middleware.InvokeAsync(NewContext(services, Anonymous(), key));
        await middleware.InvokeAsync(NewContext(services, Anonymous(), key));

        Assert.Equal(2, calls);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Empty(await db.IdempotencyRecords.Where(r => r.Key == key).ToListAsync());
    }
}
