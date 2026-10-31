using Pim.Infrastructure.Ai.Asr.Mimo;
using Xunit;

namespace Pim.UnitTests.Ai.Asr.Mimo;

public sealed class MimoTranscriptParserTests
{
    [Fact]
    public void ParsesSegments_AndReportsCoverageWithoutGaps()
    {
        var parsed = MimoTranscriptParser.Parse("0-2 | 甲: 第一句\n2-4 | 乙: 第二句");
        Assert.True(parsed.FormatDetected);
        Assert.False(parsed.TimestampFallback);
        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal(4, parsed.CoverageSeconds);
        Assert.False(parsed.HasGaps);
        Assert.False(parsed.HasOverlaps);
    }

    [Fact]
    public void ReportsGapAndOverlap()
    {
        var gap = MimoTranscriptParser.Parse("0-1 | A: x\n2-3 | B: y");
        Assert.True(gap.HasGaps);
        var overlap = MimoTranscriptParser.Parse("0-2 | A: x\n1-3 | B: y");
        Assert.True(overlap.HasOverlaps);
    }

    [Fact]
    public void PlainTextWithoutFormat_DoesNotFailAndKeepsNoSegments()
    {
        var parsed = MimoTranscriptParser.Parse("普通转写文本，没有分段。");
        Assert.False(parsed.FormatDetected);
        Assert.False(parsed.TimestampFallback);
        Assert.Empty(parsed.Segments);
        Assert.Equal("普通转写文本，没有分段。", parsed.Text);
    }

    [Fact]
    public void MalformedTimestampLine_FallsBackAndKeepsValidSegments()
    {
        var parsed = MimoTranscriptParser.Parse("0-1 | A: x\n[bad] y");
        Assert.True(parsed.TimestampFallback);
        Assert.Single(parsed.Segments);
        Assert.NotEmpty(parsed.Issues);
    }
}
