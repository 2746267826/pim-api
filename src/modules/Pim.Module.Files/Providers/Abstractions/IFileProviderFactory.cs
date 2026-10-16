using Microsoft.Extensions.DependencyInjection;

namespace Pim.Module.Files.Providers.Abstractions;

/// <summary>
/// 通用 provider 解析入口（工单 05 / F1）。
///
/// 存在意义：<c>FilesModule</c> 与各服务里此前有 <c>provider.Provider != "onedrive"</c>
/// 这类硬判（工单 §三 已点名两处，实际散落在写/读/同步/令牌四个服务里）。
/// 全部改走本接口后，新增网盘只需注册一个新的 <see cref="IFileProvider"/>，
/// 消费方一行都不用改。
/// </summary>
public interface IFileProviderFactory
{
    /// <summary>已注册的 provider key。</summary>
    IReadOnlyList<string> SupportedProviderKeys { get; }

    /// <summary>该 key 是否有实现。用于把「未支持的来源」翻译成明确的领域错误。</summary>
    bool IsSupported(string? providerKey);

    /// <summary>按 key 取实现；未注册时抛 <see cref="ProviderCapabilityNotSupportedException"/>。</summary>
    IFileProvider Get(string providerKey);
}

/// <summary>按 DI 注册的实现集合解析。</summary>
public sealed class FileProviderFactory(IServiceProvider services) : IFileProviderFactory
{
    private readonly IServiceProvider _services = services;

    public IReadOnlyList<string> SupportedProviderKeys =>
        _services.GetServices<IFileProvider>().Select(provider => provider.ProviderKey).ToList();

    public bool IsSupported(string? providerKey)
        => !string.IsNullOrWhiteSpace(providerKey)
            && _services.GetServices<IFileProvider>()
                .Any(provider => string.Equals(provider.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));

    public IFileProvider Get(string providerKey)
        => _services.GetServices<IFileProvider>()
               .FirstOrDefault(provider =>
                   string.Equals(provider.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
           ?? throw new ProviderCapabilityNotSupportedException($"provider '{providerKey}'", providerKey ?? "(null)");
}
