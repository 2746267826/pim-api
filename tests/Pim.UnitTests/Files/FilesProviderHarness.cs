using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Providers.Google;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// 单元测试里装配**真实的** provider 管线（令牌服务 → 会话工厂 → 具体 provider 实现），
/// 只把最外层的远端客户端换成测试替身。
///
/// 为什么不用手搓替身把服务直接拼起来：文件模块的通用服务现在依赖
/// <see cref="IFileProviderSessionFactory"/> 与注册表，若测试各自拼一套，
/// 装配本身（DI 注册、provider key 解析、适配器翻译）就完全没有被测到 ——
/// 那恰恰是工单 05 新增的核心。这里复用与 <c>FilesModule.RegisterServices</c>
/// 相同的注册形状，装配缺陷会在单测里直接暴露。
/// </summary>
internal sealed class FilesProviderHarness : IDisposable
{
    private readonly ServiceProvider _provider;

    public FilesProviderHarness(
        PimDbContext db,
        IOneDriveGraphClient? graph = null,
        ISecretProtector? protector = null,
        TimeProvider? clock = null,
        FileProviderSyncGate? gate = null,
        FileProviderTokenCache? cache = null,
        GoogleDriveClient? google = null,
        IConfiguration? configuration = null,
        IFileProvider? extraProvider = null)
    {
        Graph = graph as FakeOneDriveGraphClient;
        var services = new ServiceCollection();
        Configure(services, db, graph, protector, clock, gate, cache, google, configuration, extraProvider);
        _provider = services.BuildServiceProvider();
    }

    /// <summary>
    /// 与 <c>FilesModule.RegisterServices</c> 同形状的注册。抽成静态方法是为了让
    /// 「后台任务作用域」之类的测试桩复用同一套装配，避免两处漂移。
    /// </summary>
    public static void Configure(
        IServiceCollection services,
        PimDbContext db,
        IOneDriveGraphClient? graph = null,
        ISecretProtector? protector = null,
        TimeProvider? clock = null,
        FileProviderSyncGate? gate = null,
        FileProviderTokenCache? cache = null,
        GoogleDriveClient? google = null,
        IConfiguration? configuration = null,
        IFileProvider? extraProvider = null)
    {
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(db);
        services.AddSingleton(configuration ?? new ConfigurationBuilder().Build());
        services.AddSingleton(protector ?? new HarnessSecretProtector());
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(cache ?? new FileProviderTokenCache());
        services.AddSingleton(gate ?? new FileProviderSyncGate());
        services.AddSingleton<FileProviderCredentialSource>();
        services.AddSingleton<GoogleRootIdCache>();

        if (graph is not null)
        {
            services.AddSingleton(graph);
        }

        // Google 的注册始终存在：注册表要如实反映「本仓支持哪些 provider」。
        // 没有传入真实客户端时用一个只会返回空响应的桩工厂兜底 ——
        // 需要真跑 Google 的用例必须显式传入自己的客户端，否则会被这里的桩挡下（不会假绿）。
        if (google is not null)
        {
            services.AddSingleton(google);
        }
        else
        {
            services.AddSingleton(new GoogleDriveClient(new StubHttpClientFactory(
                new StubHttpHandler { Responder = _ => new HttpResponseMessage() })));
        }

        services.AddSingleton<IFileProvider, OneDriveFileProvider>();
        services.AddSingleton<IFileProvider, GoogleFileProvider>();
        if (extraProvider is not null)
        {
            services.AddSingleton(extraProvider);
        }

        services.AddSingleton<IFileProviderOAuthClient, GoogleOAuthClient>();
        services.AddSingleton<IFileProviderAuthorizer, GoogleAuthorizer>();

        // OneDrive 的 OAuth/授权实现依赖 IOneDriveGraphClient，因此只在提供了替身时注册 ——
        // 否则枚举 IEnumerable<IFileProviderOAuthClient> 会因缺依赖抛
        // 「Unable to resolve service ...」，把纯 Google 用例变成装配错误。
        if (graph is not null)
        {
            services.AddSingleton<IFileProviderOAuthClient, OneDriveOAuthClient>();
            services.AddSingleton<IFileProviderAuthorizer, OneDriveAuthorizer>();
        }
        services.AddSingleton<IFileProviderFactory, FileProviderFactory>();
        services.AddSingleton<FileProviderTokenService>();
        services.AddSingleton<FileProviderSessionFactory>();
        services.AddSingleton<FileTreeSyncEngine>();
    }

    /// <summary>被测的假 Graph 客户端（构造函数传入的那个）。</summary>
    public FakeOneDriveGraphClient? Graph { get; }

    public IFileProviderFactory Providers => _provider.GetRequiredService<IFileProviderFactory>();

    public FileProviderTokenService Tokens => _provider.GetRequiredService<FileProviderTokenService>();

    public FileProviderSessionFactory Sessions => _provider.GetRequiredService<FileProviderSessionFactory>();

    public FileProviderCredentialSource Credentials => _provider.GetRequiredService<FileProviderCredentialSource>();

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    public void Dispose() => _provider.Dispose();
}

/// <summary>测试用的对称加密「保护器」：只做可逆包装，不做真实加密。</summary>
internal sealed class HarnessSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "protected:" + plaintext;

    public string Unprotect(string ciphertext)
        => ciphertext.StartsWith("protected:", StringComparison.Ordinal)
            ? ciphertext["protected:".Length..]
            : throw new InvalidOperationException($"unexpected ciphertext: {ciphertext}");
}

/// <summary>可注入的固定时钟（B1：测试不得依赖真实时间）。</summary>
internal sealed class HarnessClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
