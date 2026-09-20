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
    /// 单个 zip 条目的解压上限（防 zip 炸弹）：docx/pptx 是 zip，
    /// 恶意构造的条目可以解出远大于源文件的体积。压缩包本身已被 1MB 上限约束，
    /// 这里再对单条目设 64MB 上限，避免解压放大打爆内存。
    /// </summary>
    public const long MaxZipEntryBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 整个压缩包的**累计**解压上限。只限制单条目是不够的：pptx 会遍历全部
    /// <c>ppt/slides/*.xml</c>，攻击者可以用一个 &lt;1MB 的包塞进大量「各自低于单条目上限」
    /// 的条目，累计解压到 GB 级，绕过单条目检查造成内存/CPU DoS（复审 NEW-1）。
    /// </summary>
    public const long MaxZipTotalBytes = 128 * 1024 * 1024;

    /// <summary>单个压缩包最多处理的条目数（同样是防「大量小条目」的 DoS）。</summary>
    public const int MaxZipEntries = 4096;

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
        using var budget = new ZipReadBudget();
        using var archive = OpenArchive(bytes);
        // 先用受保护的 Entries.Count 做整包条目数校验（含非 slide 条目），
        // 超限时直接拒绝，不分配完整条目列表；否则一个塞满无关条目的包可以绕过上限（复审 N3-4）
        budget.CountEntries(GuardZip(() => archive.Entries.Count));
        var slideEntries = GuardZip(() => archive.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            // 按幻灯片序号排序：字符串排序会把 slide10 排在 slide2 前面，
            // 导致 10 页以上的 PPT 内容顺序错乱（复审 I-10）。
            .OrderBy(entry => SlideNumber(entry.FullName))
            .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList());

        var lines = new List<string>();
        foreach (var slide in slideEntries)
        {
            var xml = ReadEntryText(slide, budget);
            lines.Add(DecodeXmlText(
                string.Concat(RegexTextRun().Matches(xml).Select(m => m.Groups["t"].Value))));
        }
        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    private static byte[] ExtractZipEntryText(byte[] bytes, string entryName, Func<string, string> transform)
    {
        using var budget = new ZipReadBudget();
        using var archive = OpenArchive(bytes);
        // 枚举/取条目同样可能因中央目录惰性解析而抛 InvalidDataException，必须并入统一映射（复审 N3-1）
        budget.CountEntries(GuardZip(() => archive.Entries.Count));
        var entry = GuardZip(() => archive.GetEntry(entryName))
            ?? throw new DomainException(5336, "文件结构异常，无法抽取文本");
        return Encoding.UTF8.GetBytes(transform(ReadEntryText(entry, budget)));
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
    /// 把 zip 相关的惰性解析异常（损坏的中央目录 / 条目元数据）统一映射为 5336。
    /// <see cref="ZipArchive"/> 的 <c>Entries</c>/<c>GetEntry</c>/<c>entry.Length</c> 都是惰性求值，
    /// 损坏数据会在**访问时**才抛 <see cref="InvalidDataException"/>，只包住构造函数是不够的（复审 N3-1）。
    /// 注意只捕获 <see cref="InvalidDataException"/>，<see cref="DomainException"/> 必须原样传播。
    /// </summary>
    private static T GuardZip<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (InvalidDataException)
        {
            throw new DomainException(5336, "文件结构异常，无法抽取文本");
        }
    }

    /// <summary>
    /// 读取 zip 条目文本。三重防护：
    /// 单条目声明体积、单条目实际读取量，以及**整个压缩包累计解压量**——
    /// 只查单条目会被「大量各自合规的小条目」绕过（复审 NEW-1）。
    /// 另外，压缩数据本身损坏时 <c>entry.Open()</c>/<c>Read</c> 也会抛
    /// <see cref="InvalidDataException"/>（不只是构造 ZipArchive 时），
    /// 必须一并转成 5336，否则仍是 500（复审 NEW-2）。
    /// </summary>
    private static string ReadEntryText(ZipArchiveEntry entry, ZipReadBudget budget)
    {
        // entry.Length 也是惰性读取的元数据，损坏条目会在此抛 InvalidDataException（复审 N3-1）
        if (GuardZip(() => entry.Length) > MaxZipEntryBytes)
        {
            throw new DomainException(5336, "文件解压后过大，无法抽取文本");
        }

        try
        {
            using var stream = GuardZip(entry.Open);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxZipEntryBytes)
                {
                    throw new DomainException(5336, "文件解压后过大，无法抽取文本");
                }

                budget.Consume(read);
                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (InvalidDataException)
        {
            throw new DomainException(5336, "文件结构异常，无法抽取文本");
        }
    }

    /// <summary>整个压缩包的累计解压预算与条目数预算（防多条目 zip 炸弹）。</summary>
    private sealed class ZipReadBudget : IDisposable
    {
        private long _totalBytes;

        public void Consume(int bytes)
        {
            _totalBytes += bytes;
            if (_totalBytes > MaxZipTotalBytes)
            {
                throw new DomainException(5336, "文件解压后过大，无法抽取文本");
            }
        }

        /// <summary>按**整包**条目数计一次账（含非目标条目），避免用无关条目绕过上限。</summary>
        public void CountEntries(int count)
        {
            if (count > MaxZipEntries)
            {
                throw new DomainException(5336, "压缩包条目过多，无法抽取文本");
            }
        }

        public void Dispose()
        {
        }
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
