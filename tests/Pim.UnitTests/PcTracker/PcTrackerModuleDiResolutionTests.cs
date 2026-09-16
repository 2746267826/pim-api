using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Caching;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// #283 回归护栏：PcTrackerModule 的路由处理器用 <c>[FromServices]</c> 声明的每个依赖，
/// 都必须在 RegisterServices 之后能从容器解析；否则请求会在 EndpointMiddleware 的依赖
/// 解析阶段抛 InvalidOperationException → 500（PcBrowserSiteService 就是这么漏注册的）。
///
/// 依赖清单直接从模块源码里扫（minimal API 的处理器编译成闭包，反射拿不到 lambda 形参），
/// 这样以后新增路由却忘了注册服务，本用例同样会失败。
/// </summary>
public sealed class PcTrackerModuleDiResolutionTests
{
    private static readonly Guid TestUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void EveryFromServicesDependencyOfPcTrackerRoutesIsRegistered()
    {
        var declaredTypes = CollectFromServicesTypes();

        Assert.NotEmpty(declaredTypes);
        // 抽查：确认扫描确实命中了已知的路由依赖（否则本用例会退化成恒真式）。
        Assert.Contains(
            typeof(Pim.Module.PcTracker.Services.PcBrowserSiteService),
            declaredTypes);

        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var unresolved = new List<string>();
        foreach (var type in declaredTypes)
        {
            try
            {
                // 解析失败有两种形态：未注册（返回 null）与构造链缺依赖（抛异常）。
                if (scope.ServiceProvider.GetService(type) is null)
                    unresolved.Add($"{type.FullName} (未注册)");
            }
            catch (Exception ex)
            {
                unresolved.Add($"{type.FullName} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        unresolved.Sort(StringComparer.Ordinal);

        Assert.True(
            unresolved.Count == 0,
            "以下路由依赖未注册进 DI，请求会 500："
            + Environment.NewLine
            + string.Join(Environment.NewLine, unresolved));
    }

    /// <summary>
    /// /api/v1/pc/browser-tt/* 五个通道必须存在，且读接口能真的走通（不再因 DI 缺项 500）。
    /// </summary>
    [Fact]
    public async Task BrowserSiteEndpoints_ResolveAndServeRequests()
    {
        var app = BuildPcTrackerApp();

        var browserSiteRoutes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(route => route.Contains("/browser-tt/", StringComparison.Ordinal))
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(5, browserSiteRoutes.Count);

        // DI 缺项时这些调用会抛 InvalidOperationException（即 #283 的 500）。
        Assert.Equal(200, (await InvokeAsync(app, "GET", "/api/v1/pc/browser-tt/summary", "?date=2026-09-16")).StatusCode);
        Assert.Equal(200, (await InvokeAsync(app, "GET", "/api/v1/pc/browser-tt/timeline", "?date=2026-09-16")).StatusCode);
        Assert.Equal(200, (await InvokeAsync(app, "GET", "/api/v1/pc/browser-tt/daily", "?date=2026-09-16")).StatusCode);
    }

    /// <summary>
    /// 扫描 PcTrackerModule.cs 里所有 <c>[FromServices]</c> 声明的依赖并解析成 Type。
    /// </summary>
    private static List<Type> CollectFromServicesTypes()
    {
        var source = File.ReadAllText(RepoPath("src", "modules", "Pim.Module.PcTracker", "PcTrackerModule.cs"));
        var moduleAssembly = typeof(PcTrackerModule).Assembly;

        var names = Regex
            .Matches(source, @"\[FromServices\]\s*([A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var resolved = new List<Type>();
        foreach (var name in names)
        {
            var type = ResolveType(moduleAssembly, name)
                ?? throw new InvalidOperationException(
                    $"[FromServices] {name} 无法解析成类型：请确认它在 PcTracker 模块程序集内。");
            resolved.Add(type);
        }

        return resolved;
    }

    private static Type? ResolveType(Assembly assembly, string simpleName)
    {
        foreach (var candidate in assembly.GetTypes())
        {
            if (string.Equals(candidate.Name, simpleName, StringComparison.Ordinal)
                || string.Equals(candidate.FullName, simpleName, StringComparison.Ordinal))
                return candidate;
        }

        // 少数依赖来自其它程序集（如 ICurrentUserService / IAggregateResultCache）。
        foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!candidate.FullName?.StartsWith("Pim", StringComparison.Ordinal) ?? true)
                continue;

            var match = candidate.GetTypes().FirstOrDefault(type =>
                string.Equals(type.Name, simpleName, StringComparison.Ordinal)
                || string.Equals(type.FullName, simpleName, StringComparison.Ordinal));
            if (match is not null)
                return match;
        }

        return null;
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddAggregateResultCaching();
        // 真实宿主由 WebApplicationBuilder 提供 IConfiguration（DefaultAppLookupProvider 依赖它）。
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<PimDbContext>(options =>
            options.UseInMemoryDatabase($"pctracker-di-{Guid.NewGuid()}"));
        services.AddScoped<ICurrentUserService>(_ => new StubCurrentUserService(TestUserId));
        new PcTrackerModule().RegisterServices(services, configuration);
        return services.BuildServiceProvider();
    }

    private static WebApplication BuildPcTrackerApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddAuthorization();
        builder.Services.AddAggregateResultCaching();
        builder.Services.AddDbContext<PimDbContext>(options =>
            options.UseInMemoryDatabase($"pctracker-browser-tt-{Guid.NewGuid()}"));
        builder.Services.AddScoped<ICurrentUserService>(_ => new StubCurrentUserService(TestUserId));

        var module = new PcTrackerModule();
        module.RegisterServices(builder.Services, builder.Configuration);

        var app = builder.Build();
        module.MapEndpoints(app);
        return app;
    }

    private static async Task<(int StatusCode, string Body)> InvokeAsync(
        WebApplication app,
        string method,
        string route,
        string queryString)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == route
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) is true);

        using var scope = app.Services.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = method;
        context.Request.Path = route;
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());

        await endpoint.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine([current, .. parts]);
            if (File.Exists(candidate)) return candidate;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException(Path.Combine(parts));
    }

    private sealed class StubCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "User";
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
