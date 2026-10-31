using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai.Asr;
using Xunit;

namespace Pim.UnitTests.Ai.Asr;

public sealed class AiAsrPromptTemplateTests
{
    [Fact]
    public void Normalize_UsesRequiredDefaultWhenMissingOrBlank()
    {
        var missing = AiAsrPromptTemplate.Normalize();
        var blank = AiAsrPromptTemplate.Normalize(" \r\n\t ");
        Assert.Equal(AiAsrPromptTemplate.DefaultText, missing.Text);
        Assert.Equal(missing, blank);
        Assert.Contains("逐字转写", missing.Text, StringComparison.Ordinal);
        Assert.Contains("不要分析、解释或总结", missing.Text, StringComparison.Ordinal);
        Assert.Contains("起始秒-结束秒 | 说话人: 文本", missing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_TrimsNormalizesControlsAndCollapsesBlankLines()
    {
        var prompt = AiAsrPromptTemplate.Normalize(
            "\r\n  请保留\t空格\u0001控制字符  \r\n\r\n\r\n第二行  \r\n");
        Assert.Equal("请保留 空格 控制字符\n\n第二行", prompt.Text);
        Assert.Equal(1, prompt.Text.Split('\n').Count(line => line.Length == 0));
        Assert.DoesNotContain('\u0001', prompt.Text);
    }

    [Fact]
    public void Normalize_IsStableAndRevisionFreezesFullSha()
    {
        var first = AiAsrPromptTemplate.Normalize("固定提示词");
        var second = AiAsrPromptTemplate.Normalize("固定提示词");
        var changed = AiAsrPromptTemplate.Normalize("固定提示词。");
        Assert.Equal(first, second);
        Assert.Equal(64, first.Sha256.Length);
        Assert.StartsWith("asr-prompt-v1:", first.Revision);
        Assert.NotEqual(first.Sha256, changed.Sha256);
        Assert.NotEqual(first.Revision, changed.Revision);
        Assert.DoesNotContain("固定提示词", first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_RejectsOversizePrompt()
    {
        var error = Assert.Throws<DomainException>(() =>
            AiAsrPromptTemplate.Normalize(new string(' ', AiAsrPromptTemplate.MaxInputLength + 1)));
        Assert.Equal(40001, error.ErrorCode);
    }

    [Theory]
    [InlineData("auto", "language=auto")]
    [InlineData("zh", "language=zh")]
    [InlineData(" EN ", "language=en")]
    public void Snapshot_RendersDeterministicLanguageRevision(string language, string expected)
    {
        var template = AiAsrPromptTemplate.Normalize("自定义逐字提示");
        var snapshot = template.Snapshot(language);
        Assert.Contains("自定义逐字提示", snapshot.Text, StringComparison.Ordinal);
        Assert.Contains(expected, snapshot.Text, StringComparison.Ordinal);
        Assert.Equal(snapshot, template.Snapshot(language));
        Assert.Equal(template.Sha256, snapshot.TemplateSha256);
        Assert.Equal(template.Revision, snapshot.TemplateRevision);
        Assert.Equal(64, snapshot.Sha256.Length);
        Assert.StartsWith("asr-prompt-v1:", snapshot.Revision);
        Assert.DoesNotContain(snapshot.Text, snapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_RejectsUnsupportedLanguage()
    {
        var error = Assert.Throws<DomainException>(() =>
            AiAsrPromptTemplate.Normalize().Snapshot("fr"));
        Assert.Equal(40001, error.ErrorCode);
    }
}
