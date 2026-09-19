using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Files;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// FilesModule 路由依赖的 DI 解析护栏（与 <c>PcTrackerModuleDiResolutionTests</c> 同一模式）。
///
/// 背景（P4a 复审）：minimal API 的处理器编译成闭包，反射拿不到 lambda 形参，
/// 因此 <c>[FromServices] T</c> 漏注册不会被编译期拦住，只会在请求进入
/// EndpointMiddleware 依赖解析阶段时抛 <c>InvalidOperationException</c> → 500。
/// #328 就漏注册了 <c>OneDriveWriteService</c>（move/rename/delete/open-link 四个端点全 500）
/// 与 <c>OneDriveTransientRateLimiter</c>（限流退化成每次请求新建计数器，永不触发）。
///
/// 依赖清单直接从模块源码扫（和 PcTracker 的做法一致），这样以后新增路由却忘了注册服务，
/// 本用例同样会失败。
/// </summary>
public sealed class FilesModuleDiResolutionTests
{

    /// <summary>
    /// 扫描源码得到的 <c>[FromServices]</c> 依赖清单，必须在**真实宿主**容器里可解析。
    /// 扫描保证「新增路由忘了注册」这类漏项以后也会被抓到；
    /// 用真实宿主（而非手搭最小容器）是为了避免基础设施未注册导致的误报。
    /// </summary>
    [Fact]
    public void EveryFromServicesDependencyOfFilesRoutesIsRegistered()
    {
        var declaredTypes = CollectFromServicesTypes();

        Assert.NotEmpty(declaredTypes);
        // 抽查：确认扫描命中了本轮涉及的已知路由依赖（否则本用例会退化成恒真式）。
        Assert.Contains(typeof(OneDriveWriteService), declaredTypes);
        Assert.Contains(typeof(OneDriveContentService), declaredTypes);

        using var factory = CreateRealHostFactory();
        using var scope = factory.Services.CreateScope();

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
            "以下路由依赖在真实宿主里无法解析，请求会 500："
            + Environment.NewLine
            + string.Join(Environment.NewLine, unresolved));
    }

    private static WebApplicationFactory<Program> CreateRealHostFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase($"files-di-real-{Guid.NewGuid()}"));
            });
        });

    /// <summary>
    /// <c>OneDriveContentService</c> 是 Scoped，而「每用户每分钟 30 次」的限流窗口必须跨请求共享，
    /// 因此限流器必须是单例：若被 Scoped 化（或退化成服务内部 <c>new</c>），计数器每次请求归零，
    /// 限流形同不存在。这里断言真实宿主里跨作用域拿到的是同一实例。
    /// </summary>
    [Fact]
    public void TransientRateLimiter_IsRegisteredAsSingleton_SoTheWindowSpansRequests()
    {
        using var factory = CreateRealHostFactory();
        using var scope1 = factory.Services.CreateScope();
        using var scope2 = factory.Services.CreateScope();

        var first = scope1.ServiceProvider.GetService<OneDriveTransientRateLimiter>();
        var second = scope2.ServiceProvider.GetService<OneDriveTransientRateLimiter>();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    /// <summary>
    /// <c>OneDriveContentService</c> 注入的限流器必须就是容器里那个单例，
    /// 而不是构造函数里 <c>?? new</c> 出来的私有实例——否则配额计数随服务实例丢弃，
    /// 30 次/分钟的限流永远不会触发。
    /// </summary>
    [Fact]
    public void OneDriveContentService_UsesTheContainerRateLimiter_NotAPerInstanceOne()
    {
        using var factory = CreateRealHostFactory();
        using var scope = factory.Services.CreateScope();

        var limiter = scope.ServiceProvider.GetRequiredService<OneDriveTransientRateLimiter>();
        var service = scope.ServiceProvider.GetRequiredService<OneDriveContentService>();
        var injected = ReadPrivateField<OneDriveTransientRateLimiter>(service, "_rateLimiter");

        Assert.Same(limiter, injected);
    }

    /// <summary>
    /// <c>IOneDriveGraphClient</c> 本身必须能从真实宿主解析。<c>AddHttpClient&lt;T&gt;</c> 生成的
    /// 类型化客户端要求构造函数能接收 <c>HttpClient</c>；<c>OneDriveGraphClient</c> 的构造函数只收
    /// <c>IHttpClientFactory</c> + <c>IConfiguration</c>，显式传入的 <c>HttpClient</c> 无处可放，
    /// ActivatorUtilities 找不到适用构造函数 → 解析必抛。所有 OneDrive 服务都依赖它，
    /// 因此这是「全部 OneDrive 端点 500」的单点故障。
    /// </summary>
    [Fact]
    public void OneDriveGraphClient_IsResolvableFromTheRealHost()
    {
        using var factory = CreateRealHostFactory();
        using var scope = factory.Services.CreateScope();

        var client = scope.ServiceProvider.GetService<Pim.Module.Files.Providers.IOneDriveGraphClient>();
        Assert.NotNull(client);
    }

    /// <summary>
    /// 30 秒超时必须挂在<b>命名客户端</b>上，因为 OneDriveGraphClient 是
    /// <c>IHttpClientFactory.CreateClient("onedrive-graph")</c> 取客户端的。
    /// 若把超时配在 AddHttpClient&lt;T&gt; 上（复审前的写法），实际请求用的命名客户端
    /// 仍是默认 100 秒，M-11 的修复形同不存在。
    /// </summary>
    [Fact]
    public void NamedOneDriveGraphClient_CarriesTheThirtySecondTimeout()
    {
        using var factory = CreateRealHostFactory();
        var httpClientFactory = factory.Services.GetRequiredService<IHttpClientFactory>();

        using var named = httpClientFactory.CreateClient(
            Pim.Module.Files.Providers.OneDriveGraphClient.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(30), named.Timeout);
    }

    private static T ReadPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{instance.GetType().Name} 没有私有字段 {fieldName}");
        return (T)field.GetValue(instance)!;
    }

    /// <summary>扫描 FilesModule.cs 里所有 <c>[FromServices]</c> 声明的依赖并解析成 Type。</summary>
    private static List<Type> CollectFromServicesTypes()
    {
        var source = File.ReadAllText(RepoPath("src", "modules", "Pim.Module.Files", "FilesModule.cs"));
        var moduleAssembly = typeof(FilesModule).Assembly;

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
                    $"[FromServices] {name} 无法解析成类型：请确认它在 Files 模块程序集内。");
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

        // 少数依赖来自其它程序集（如 ICurrentUserService / PimDbContext）。
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
}
