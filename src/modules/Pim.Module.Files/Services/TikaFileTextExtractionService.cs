using Pim.Infrastructure.TextExtraction;

namespace Pim.Module.Files.Services;

/// <summary>
/// <see cref="IFileTextExtractionService"/> 的 Tika 实现：把流交给 <see cref="TikaClient"/>。
///
/// 文件模块 v2 里它只服务 <c>read_file_text</c> 的「按类型抽取」分支（pdf 等），
/// 不是强制依赖——未配置 Tika 时构造函数仍可解析，抽取时返回明确的 5336。
/// </summary>
public sealed class TikaFileTextExtractionService(TikaClient tikaClient) : IFileTextExtractionService
{
    public Task<string> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken ct = default)
        => tikaClient.ExtractTextAsync(fileStream, fileName, ct);
}
