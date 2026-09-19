using System.IO.Compression;
using System.Text;
using Pim.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

public sealed record OneDriveExtractedText(
    string Content,
    long SourceBytes,
    bool Truncated,
    string Extractor);

/// <summary>
/// read_file_text 的文本抽取（设计文档 §12）：
/// 文本类型直读 UTF-8；docx/pptx 走 BCL zip+XML；其余（pdf 等）在 Tika 已配置时
/// 交给 IFileTextExtractionService，未配置则返回明确的「不支持」领域错误。
/// 内容瞬态经过，不落盘、不入库。抽取上限 maxBytes（默认 64KB，封顶 1MB）。
/// </summary>
public sealed partial class OneDriveTextExtractor(
    IFileTextExtractionService? tikaExtraction = null,
    ILogger<OneDriveTextExtractor>? logger = null)
{
    public const long DefaultMaxBytes = 64 * 1024;
    public const long HardMaxBytes = 1024 * 1024;

    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".log", ".yml", ".yaml", ".xml", ".html", ".htm", ".tsv",
    };

    public async Task<OneDriveExtractedText> ExtractAsync(
        byte[] bytes,
        string fileName,
        string? mimeType,
        long maxBytes,
        CancellationToken ct = default)
    {
        if (maxBytes > HardMaxBytes)
        {
            maxBytes = HardMaxBytes;
        }

        var extension = Path.GetExtension(fileName);
        if (PlainTextExtensions.Contains(extension)
            || (mimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return Truncate(bytes, maxBytes, "utf8");
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return Truncate(ExtractDocx(bytes), maxBytes, "docx");
        }

        if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            return Truncate(ExtractPptx(bytes), maxBytes, "pptx");
        }

        if (tikaExtraction is not null)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                var text = await tikaExtraction.ExtractTextAsync(stream, fileName, ct);
                return Truncate(Encoding.UTF8.GetBytes(text), maxBytes, "tika");
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Tika extraction failed for {FileName}; returning unsupported", fileName);
            }
        }

        throw new DomainException(5336, $"暂不支持从 {extension} 文件抽取文本");
    }

    private static OneDriveExtractedText Truncate(byte[] bytes, long maxBytes, string extractor)
    {
        var truncated = bytes.LongLength > maxBytes;
        var content = Encoding.UTF8.GetString(bytes, 0, truncated ? (int)maxBytes : bytes.Length);
        return new OneDriveExtractedText(content, bytes.LongLength, truncated, extractor);
    }

    /// <summary>docx：word/document.xml 的 w:t 节点串联。</summary>
    private static byte[] ExtractDocx(byte[] bytes)
    {
        return ExtractZipEntryText(bytes, "word/document.xml", entryXml =>
        {
            var paragraphs = RegexParagraph().Split(entryXml);
            var lines = paragraphs
                .Select(paragraph => string.Concat(RegexTextRun().Matches(paragraph).Select(m => m.Groups["t"].Value)))
                .Where(line => line.Length > 0);
            return string.Join("\n", lines);
        });
    }

    /// <summary>pptx：ppt/slides/slideN.xml 的 a:t 节点按页串联。</summary>
    private static byte[] ExtractPptx(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var slideEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = new List<string>();
        foreach (var slide in slideEntries)
        {
            using var reader = new StreamReader(slide.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            lines.Add(string.Concat(RegexTextRun().Matches(xml).Select(m => m.Groups["t"].Value)));
        }
        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    private static byte[] ExtractZipEntryText(byte[] bytes, string entryName, Func<string, string> transform)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName)
            ?? throw new DomainException(5336, "文件结构异常，无法抽取文本");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        return Encoding.UTF8.GetBytes(transform(xml));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<w:p[ >]")]
    private static partial System.Text.RegularExpressions.Regex RegexParagraph();

    [System.Text.RegularExpressions.GeneratedRegex(@"<(?:w|a):t[^>]*>(?<t>[^<]*)</(?:w|a):t>")]
    private static partial System.Text.RegularExpressions.Regex RegexTextRun();
}
