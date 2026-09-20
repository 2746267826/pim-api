namespace Pim.Module.Files.Services;

/// <summary>
/// 文本抽取后端（可选）。文件模块 v2 只在 <c>read_file_text</c> 需要 pdf 等
/// 二进制格式时使用它；文本/docx/pptx 由 <see cref="OneDriveTextExtractor"/> 自行处理。
///
/// 实现是 Tika（<c>TikaClient</c>）；未配置时 <c>read_file_text</c> 对不支持的类型
/// 返回明确的「不支持」领域错误，而不是静默失败。
/// </summary>
public interface IFileTextExtractionService
{
    Task<string> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken ct = default);
}
