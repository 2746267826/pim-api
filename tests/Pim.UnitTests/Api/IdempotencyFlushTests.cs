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

    /// <summary>OpsKey 端点：既没有 <see cref="IAuthorizeData"/>，也没有 AllowAnonymous。</summary>
    private static Endpoint NoAuthMetadata()
        => new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(Path),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: "ops-test");

    private static DefaultHttpContext NewContext(
        ServiceProvider services, Endpoint endpoint, string key, string path = Path)
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = path;
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
        => Middleware(async ctx =>
        {
            onCall();
            await ctx.Response.WriteAsync("{\"code\":0,\"data\":\"ok\"}");
            // 模拟「下游已经 flush / 响应已开始」：回写路径不能因此被跳过。
            await ctx.Response.Body.FlushAsync();
            ctx.Response.Body.Flush();
        });

    private static IdempotencyMiddleware Middleware(Func<HttpContext, Task> handler)
        => new(ctx => handler(ctx), NullLogger<IdempotencyMiddleware>.Instance);

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
    public async Task EndpointWithoutAuthMetadata_WithAuthenticatedUser_IsNotRecorded()
    {
        // 只判 AllowAnonymous 不够：OpsKey 端点（/api/v1/ops/*）没有任何授权元数据，
        // 但请求可能同时带着有效 JWT。删掉 IAuthorizeData 判定会让它们被缓存 24 小时（review P3-2）。
        var userId = Guid.NewGuid();
        using var services = CreateServices(userId);
        var key = Guid.NewGuid().ToString();
        var calls = 0;
        var middleware = Middleware(() => calls++);

        await middleware.InvokeAsync(NewContext(services, NoAuthMetadata(), key));
        await middleware.InvokeAsync(NewContext(services, NoAuthMetadata(), key));

        Assert.Equal(2, calls);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Empty(await db.IdempotencyRecords.Where(r => r.Key == key).ToListAsync());
    }

    [Fact]
    public async Task ShareRoute_WithInlineGuidConstraint_IsExemptFromIdempotency()
    {
        // 运行时 RoutePattern.RawText 保留 `{id:guid}`，而豁免清单按契约写法 `{id}` 维护。
        // 不做归一化时这里会命中不到，分享链接的 webUrl（bearer 能力链接）会被缓存 24 小时（review P1-1）。
        const string sharePath = "/api/v1/files/items/11111111-1111-1111-1111-111111111111/share";
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v1/files/items/{id:guid}/share"),
            order: 0,
            new EndpointMetadataCollection(new AuthorizeAttribute()),
            displayName: "share-test");

        var userId = Guid.NewGuid();
        using var services = CreateServices(userId);
        var key = Guid.NewGuid().ToString();
        var calls = 0;
        var middleware = Middleware(() => calls++);

        var first = NewContext(services, endpoint, key, sharePath);
        await middleware.InvokeAsync(first);
        var second = NewContext(services, endpoint, key, sharePath);
        await middleware.InvokeAsync(second);

        Assert.Equal(2, calls);
        Assert.False(second.Response.Headers.ContainsKey(IdempotencyMiddleware.ReplayedHeader));
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Empty(await db.IdempotencyRecords.Where(r => r.Key == key).ToListAsync());
    }

    [Fact]
    public async Task OversizedResponse_IsNotCached_AndRetryExecutesAgain()
    {
        // 超过捕获上限时不缓存（只缓存状态码会让重放返回「2xx + 空正文」），
        // 并且必须释放占位，让客户端用同一个 key 重试时真的重新执行（review P3-2）。
        var userId = Guid.NewGuid();
        using var services = CreateServices(userId);
        var key = Guid.NewGuid().ToString();
        var calls = 0;
        var big = new string('x', IdempotencyMiddleware.MaxResponseBodyBytes + 1);
        var middleware = Middleware(async ctx => { calls++; await ctx.Response.WriteAsync(big); });

        var first = NewContext(services, Authorized(), key);
        await middleware.InvokeAsync(first);
        Assert.False(first.Response.Headers.ContainsKey(IdempotencyMiddleware.ReplayedHeader));

        var second = NewContext(services, Authorized(), key);
        await middleware.InvokeAsync(second);
        Assert.False(second.Response.Headers.ContainsKey(IdempotencyMiddleware.ReplayedHeader));
        Assert.Equal(2, calls);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Empty(await db.IdempotencyRecords.Where(r => r.Key == key).ToListAsync());
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
