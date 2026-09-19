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

    /// <summary>
    /// zip 解包的单条目上限（防 zip 炸弹）：docx/pptx 是 zip，
    /// 恶意构造的条目可以解出远大于源文件的体积。压缩包本身已被 1MB 上限约束，
    /// 这里再对单条目设 64MB 上限，避免解压放大打爆内存。
    /// </summary>
    public const long MaxZipEntryBytes = 64 * 1024 * 1024;

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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogWarning(exception, "Tika extraction failed for {FileName}; returning unsupported", fileName);
            }
        }

        throw new DomainException(5336, $"暂不支持从 {extension} 文件抽取文本");
    }

    private static OneDriveExtractedText Truncate(byte[] bytes, long maxBytes, string extractor)
    {
        var truncated = bytes.LongLength > maxBytes;
        var limit = truncated ? (int)maxBytes : bytes.Length;
        // 按字节截断可能切断多字节字符：退回到最近的 UTF-8 字符边界，
        // 否则尾部会出现 U+FFFD 替换字符（复审 M-2）。
        if (truncated)
        {
            while (limit > 0 && (bytes[limit] & 0xC0) == 0x80)
            {
                limit--;
            }
        }

        var content = Encoding.UTF8.GetString(bytes, 0, limit);
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
                .Where(line => line.Length > 0)
                // 实体解码必须在**抽取之后**做：先解码会把 &lt;w:p&gt; 这类正文内容
                // 变成看起来像标签的文本，破坏后续正则匹配。
                .Select(DecodeXmlText);
            return string.Join("\n", lines);
        });
    }

    /// <summary>pptx：ppt/slides/slideN.xml 的 a:t 节点按页串联。</summary>
    private static byte[] ExtractPptx(byte[] bytes)
    {
        using var archive = OpenArchive(bytes);
        var slideEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            // 按幻灯片序号排序：字符串排序会把 slide10 排在 slide2 前面，
            // 导致 10 页以上的 PPT 内容顺序错乱（复审 I-10）。
            .OrderBy(entry => SlideNumber(entry.FullName))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lines = new List<string>();
        foreach (var slide in slideEntries)
        {
            var xml = ReadEntryText(slide);
            lines.Add(DecodeXmlText(
                string.Concat(RegexTextRun().Matches(xml).Select(m => m.Groups["t"].Value))));
        }
        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    private static byte[] ExtractZipEntryText(byte[] bytes, string entryName, Func<string, string> transform)
    {
        using var archive = OpenArchive(bytes);
        var entry = archive.GetEntry(entryName)
            ?? throw new DomainException(5336, "文件结构异常，无法抽取文本");
        return Encoding.UTF8.GetBytes(transform(ReadEntryText(entry)));
    }

    /// <summary>
    /// 打开 zip：源字节非法（不是 zip / 已损坏）时 <see cref="ZipArchive"/> 抛
    /// <see cref="InvalidDataException"/>，必须转成明确的领域错误，否则会冒泡成 500（复审 I-4）。
    /// </summary>
    private static ZipArchive OpenArchive(byte[] bytes)
    {
        try
        {
            return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new DomainException(5336, "文件结构异常，无法抽取文本");
        }
    }

    /// <summary>
    /// 读取 zip 条目文本，带解压体积上限：docx/pptx 的压缩比可以极高，
    /// 若不限制，几 KB 的恶意文件能解出 GB 级内容打爆内存（zip 炸弹，复审 I-4）。
    /// </summary>
    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxZipEntryBytes)
        {
            throw new DomainException(5336, "文件解压后过大，无法抽取文本");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxZipEntryBytes)
            {
                throw new DomainException(5336, "文件解压后过大，无法抽取文本");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// XML 文本节点里的实体（<c>&amp;amp;</c>、<c>&amp;lt;</c> 等）在正则抽取后仍是转义形态，
    /// 必须解码，否则用户看到的是 <c>&amp;amp;</c> 而不是 <c>&amp;</c>（复审 I-5）。
    /// </summary>
    private static string DecodeXmlText(string text)
        => System.Net.WebUtility.HtmlDecode(text);

    /// <summary>从 "ppt/slides/slide12.xml" 解析出 12；解析不出时排到最后。</summary>
    private static int SlideNumber(string entryName)
    {
        var name = Path.GetFileNameWithoutExtension(entryName);
        var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<w:p[ >]")]
    private static partial System.Text.RegularExpressions.Regex RegexParagraph();

    [System.Text.RegularExpressions.GeneratedRegex(@"<(?:w|a):t[^>]*>(?<t>[^<]*)</(?:w|a):t>")]
    private static partial System.Text.RegularExpressions.Regex RegexTextRun();
}
