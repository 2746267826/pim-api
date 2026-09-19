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
}
