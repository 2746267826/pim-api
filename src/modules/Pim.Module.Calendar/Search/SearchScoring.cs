using System.Globalization;
using System.Text.RegularExpressions;
using Pim.Core.Modules;

namespace Pim.Module.Calendar.Search;

internal sealed record SearchField(
    string Name,
    string Label,
    string Text,
    double BaseScore,
    int? Index = null);

internal static partial class SearchScoring
{
    internal static bool Contains(string? text, string term)
        => !string.IsNullOrEmpty(text) &&
           text.Contains(term, StringComparison.OrdinalIgnoreCase);

    internal static (double Score, IReadOnlyList<string> MatchedFields,
        IReadOnlyList<SearchFieldLocation> FieldLocations, bool Matches)
        Match(IReadOnlyList<SearchField> fields, SearchProviderQuery query)
    {
        var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedLabels = new List<string>();
        var locations = new List<SearchFieldLocation>();
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        double score = 0;

        foreach (var field in fields)
        {
            var fieldMatches = query.Terms
                .Where(term => Contains(field.Text, term))
                .ToArray();
            if (fieldMatches.Length == 0) continue;

            foreach (var term in fieldMatches)
            {
                matchedTerms.Add(term);
                score += field.Name == "title"
                    ? TitleScore(field.Text, term)
                    : field.BaseScore;
            }

            if (seenLabels.Add(field.Label))
            {
                matchedLabels.Add(field.Label);
                locations.Add(new SearchFieldLocation(field.Name, field.Label, field.Index));
            }
        }

        var matches = query.Match == SearchMatchMode.All
            ? query.Terms.All(term => matchedTerms.Contains(term))
            : matchedTerms.Count > 0;
        return (score, matchedLabels, locations, matches);
    }

    internal static string Snippet(
        IReadOnlyList<SearchField> fields, IReadOnlyList<string> terms, int maxLength = 180)
    {
        var searchable = fields
            .Where(field => field.Name != "title" && !string.IsNullOrWhiteSpace(field.Text))
            .ToArray();
        var source = searchable.FirstOrDefault(field =>
            terms.Any(term => Contains(field.Text, term)))?.Text
            ?? fields.FirstOrDefault(field => !string.IsNullOrWhiteSpace(field.Text))?.Text
            ?? string.Empty;
        var plain = StripMarkdown(source);
        if (plain.Length == 0) return string.Empty;

        var positions = terms
            .Select(term => plain.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .ToArray();
        var first = positions.Length == 0 ? 0 : positions.Min();
        var start = Math.Max(0, first - maxLength / 4);
        var end = Math.Min(plain.Length, start + maxLength);
        if (end - start < maxLength && start > 0)
        {
            start = Math.Max(0, end - maxLength);
        }

        var value = plain[start..end].Trim();
        return (start > 0 ? "…" : string.Empty) + value + (end < plain.Length ? "…" : string.Empty);
    }

    internal static string DateText(params DateTimeOffset?[] values)
        => string.Join(' ', values
            .Where(value => value.HasValue)
            .SelectMany(value => new[]
            {
                value!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                value.Value.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)
            })
            .Distinct(StringComparer.Ordinal));

    private static double TitleScore(string title, string term)
    {
        if (string.Equals(title.Trim(), term, StringComparison.OrdinalIgnoreCase)) return 120;
        if (title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 90;
        return 70;
    }

    private static string StripMarkdown(string value)
    {
        var withoutImages = ImagePattern().Replace(value, " ");
        var withoutLinks = LinkPattern().Replace(withoutImages, "$1");
        var withoutMarkup = MarkupPattern().Replace(withoutLinks, " ");
        return WhitespacePattern().Replace(withoutMarkup, " ").Trim();
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"[#*_>`~]+", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
