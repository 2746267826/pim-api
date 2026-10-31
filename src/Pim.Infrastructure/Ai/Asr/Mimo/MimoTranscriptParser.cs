using System.Globalization;
using System.Text.RegularExpressions;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

public sealed record MimoTranscriptSegment(double StartSeconds, double EndSeconds, string Speaker, string Text);

/// <summary>
/// Parses the product transcript format <c>start-end | speaker: text</c>. The raw text is always
/// preserved; malformed/partial timestamps fall back to text without discarding usable segments.
/// </summary>
public sealed record MimoTranscriptParseResult(string Text, IReadOnlyList<MimoTranscriptSegment> Segments,
    bool FormatDetected, bool TimestampFallback, bool HasGaps, bool HasOverlaps,
    double? CoverageSeconds, IReadOnlyList<string> Issues)
{
    public static MimoTranscriptParseResult Empty(string text) =>
        new(text, [], false, false, false, false, null, []);
}

public static class MimoTranscriptParser
{
    private static readonly Regex SegmentPattern = new(
        @"^(?<start>\d+(?:\.\d+)?)\s*[-–—]\s*(?<end>\d+(?:\.\d+)?)\s*\|\s*(?<speaker>[^:：]+?)\s*[:：]\s*(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static MimoTranscriptParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return MimoTranscriptParseResult.Empty(text ?? "");

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0) return MimoTranscriptParseResult.Empty(text);

        var segments = new List<MimoTranscriptSegment>();
        var issues = new List<string>();
        var invalid = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SegmentPattern.Match(lines[i]);
            if (!match.Success)
            {
                if (!LooksTimestampLike(lines[i])) continue;
                invalid++;
                issues.Add($"line {i + 1}: timestamp format not recognized");
                continue;
            }
            if (!double.TryParse(match.Groups["start"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
                !double.TryParse(match.Groups["end"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var end) ||
                !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
            {
                invalid++;
                issues.Add($"line {i + 1}: invalid start/end range");
                continue;
            }
            segments.Add(new(start, end, match.Groups["speaker"].Value.Trim(), match.Groups["text"].Value.Trim()));
        }

        if (segments.Count == 0 && invalid == 0) return MimoTranscriptParseResult.Empty(text);

        var ordered = segments.OrderBy(x => x.StartSeconds).ToArray();
        var gaps = false;
        var overlaps = false;
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].StartSeconds > ordered[i - 1].EndSeconds + 0.05) gaps = true;
            if (ordered[i].StartSeconds < ordered[i - 1].EndSeconds) overlaps = true;
        }
        var coverage = ordered.Length > 0 ? Math.Round(ordered[^1].EndSeconds - ordered[0].StartSeconds, 3) : (double?)null;
        return new(text, ordered, ordered.Length > 0, invalid > 0, gaps, overlaps, coverage, issues);
    }

    private static bool LooksTimestampLike(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return false;
        return trimmed[0] is '[' or >= '0' and <= '9';
    }

    internal static MimoTranscriptParseResult Empty(string text) => MimoTranscriptParseResult.Empty(text);
}
