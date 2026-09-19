using Xunit;
using System.IO.Compression;
using System.Text;
using Pim.Core.Exceptions;
using Pim.Module.Files.Services;

namespace Pim.UnitTests.Files;

/// <summary>
/// OneDriveTextExtractor 测试：文本直读、docx 解包、截断、不支持类型、Tika 兜底。
/// </summary>
public class OneDriveTextExtractorTests
{
    private readonly OneDriveTextExtractor _extractor = new();

    private static byte[] ZipWith(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] MinimalDocxWithText(params string[] paragraphs)
    {
        var body = string.Join(
            string.Empty,
            paragraphs.Select((text, index) =>
                $"<w:p><w:r><w:t{(index == 0 ? " xml:space=\"preserve\"" : "")}>{text}</w:t></w:r></w:p>"));
        return ZipWith(("word/document.xml", $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>"));
    }

    [Fact]
    public async Task PlainText_ByExtension_ReturnsUtf8()
    {
        var bytes = "会议纪要：项目周报"u8.ToArray();
        var result = await _extractor.ExtractAsync(bytes, "纪要.md", null, maxBytes: 1024);
        Assert.Equal("会议纪要：项目周报", result.Content);
        Assert.False(result.Truncated);
        Assert.Equal("utf8", result.Extractor);
    }

    [Fact]
    public async Task Docx_ConcatsRuns_ByParagraph()
    {
        var bytes = MinimalDocxWithText("第一段：", "合同金额一万");
        var result = await _extractor.ExtractAsync(bytes, "合同.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", maxBytes: 1024);

        Assert.Contains("第一段：", result.Content);
        Assert.Contains("合同金额一万", result.Content);
        Assert.Equal("docx", result.Extractor);
    }

    [Fact]
    public async Task Truncation_RespectsMaxBytes_AndFlags()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 500));
        var result = await _extractor.ExtractAsync(bytes, "big.txt", "text/plain", maxBytes: 100);

        Assert.True(result.Truncated);
        Assert.Equal(100, result.Content.Length);
        Assert.Equal(500, result.SourceBytes);
    }

    [Fact]
    public async Task UnsupportedType_WithoutTika_Throws5336()
    {
        var error = await Assert.ThrowsAsync<DomainException>(
            () => _extractor.ExtractAsync(new byte[] { 1, 2, 3 }, "图纸.dwg", null, maxBytes: 1024));
        Assert.Equal(5336, error.ErrorCode);
    }

    [Fact]
    public async Task Pdf_WithTikaConfigured_FallsBackToTika()
    {
        var tikaCalls = new List<string>();
        var tika = new StubTika(text =>
        {
            tikaCalls.Add("called");
            return text;
        });
        var extractor = new OneDriveTextExtractor(tika);
        var result = await extractor.ExtractAsync("%PDF-fake"u8.ToArray(), "a.pdf", "application/pdf", maxBytes: 1024);

        Assert.Equal("%PDF-fake", result.Content);
        Assert.Equal("tika", result.Extractor);
        Assert.Single(tikaCalls);
    }

    private sealed class StubTika(Func<string, string> transform) : IFileTextExtractionService
    {
        public Task<string> ExtractTextAsync(Stream fileStream, string fileName, CancellationToken ct = default)
        {
            using var reader = new StreamReader(fileStream, Encoding.UTF8);
            return Task.FromResult(transform(reader.ReadToEnd()));
        }
    }

    // ===================== P4a 复审加固 =====================

    /// <summary>
    /// 损坏的 docx（不是合法 zip）必须给明确的领域错误 5336，
    /// 而不是让 <see cref="InvalidDataException"/> 冒泡成 500。
    /// </summary>
    [Fact]
    public async Task CorruptDocx_ReturnsDomainError_NotInvalidDataException()
    {
        var error = await Assert.ThrowsAsync<DomainException>(
            () => _extractor.ExtractAsync("not-a-zip-at-all"u8.ToArray(), "broken.docx", null, maxBytes: 1024));
        Assert.Equal(5336, error.ErrorCode);
    }

    /// <summary>合法 zip 但缺少 word/document.xml（例如其实是 xlsx 改名）→ 5336。</summary>
    [Fact]
    public async Task Docx_WithoutDocumentXml_ReturnsDomainError()
    {
        var bytes = ZipWith(("xl/workbook.xml", "<workbook/>"));
        var error = await Assert.ThrowsAsync<DomainException>(
            () => _extractor.ExtractAsync(bytes, "actually-xlsx.docx", null, maxBytes: 1024));
        Assert.Equal(5336, error.ErrorCode);
    }

    /// <summary>
    /// zip 炸弹：条目声明的解压体积超过上限时，必须在解压前就拒绝，
    /// 而不是把内容全部读进内存（否则几 KB 的文件能打爆进程）。
    /// </summary>
    [Fact]
    public async Task Docx_ZipBombEntry_IsRejectedBeforeDecompression()
    {
        // 高压缩比条目：1 亿个 'a' 压成几十 KB。
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            var chunk = new string('a', 1024 * 1024);
            for (var i = 0; i < 100; i++)
            {
                writer.Write(chunk);
            }
        }

        var bytes = buffer.ToArray();
        Assert.True(bytes.Length < 5 * 1024 * 1024, $"测试前提：压缩后应远小于解压体积，实际 {bytes.Length}");

        var error = await Assert.ThrowsAsync<DomainException>(
            () => _extractor.ExtractAsync(bytes, "bomb.docx", null, maxBytes: 1024));
        Assert.Equal(5336, error.ErrorCode);
    }

    /// <summary>XML 实体的文本必须解码：&amp;amp; 应呈现为 &amp;，而不是原样的 &amp;amp;。</summary>
    [Fact]
    public async Task Docx_DecodesXmlEntities()
    {
        var bytes = MinimalDocxWithText("A &amp; B &lt;tag&gt;");
        var result = await _extractor.ExtractAsync(bytes, "entities.docx", null, maxBytes: 4096);

        Assert.Contains("A & B <tag>", result.Content);
        Assert.DoesNotContain("&amp;", result.Content);
    }

    /// <summary>
    /// 按字节截断不能切出半个多字节字符（否则尾部出现 U+FFFD 替换字符）。
    /// 用多字节中文构造：每个字符 3 字节，故意把上限设在字符中间。
    /// </summary>
    [Fact]
    public async Task Truncation_DoesNotSplitMultibyteCharacter()
    {
        var text = string.Concat(Enumerable.Repeat("中", 100)); // 300 字节
        var result = await _extractor.ExtractAsync(Encoding.UTF8.GetBytes(text), "cn.txt", "text/plain", maxBytes: 10);

        Assert.True(result.Truncated);
        Assert.DoesNotContain('\uFFFD', result.Content);
        // 10 字节的边界回退到 9（3 个完整中文字符）
        Assert.Equal("中中中", result.Content);
        Assert.Equal(300, result.SourceBytes);
    }
}
