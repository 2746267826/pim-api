using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;

namespace Pim.Module.Calendar.Services;

public static class EventDescriptionSanitizer
{
    private static readonly HtmlSanitizer Sanitizer;
    private static readonly Regex ScriptRegex = new(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex StyleRegex = new(@"<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex CommentRegex = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex TagStripRegex = new(@"<[^>]+>", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    static EventDescriptionSanitizer()
    {
        Sanitizer = new HtmlSanitizer();
        Sanitizer.KeepChildNodes = true;
        Sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "strong", "b", "em", "i", "u", "s",
            "ul", "ol", "li", "blockquote", "pre", "code", "h2", "h3", "a"
        })
        {
            Sanitizer.AllowedTags.Add(tag);
        }

        Sanitizer.AllowedAttributes.Clear();
        Sanitizer.AllowedAttributes.Add("href");
        Sanitizer.AllowedAttributes.Add("target");
        Sanitizer.AllowedAttributes.Add("rel");

        Sanitizer.AllowedSchemes.Clear();
        Sanitizer.AllowedSchemes.Add("http");
        Sanitizer.AllowedSchemes.Add("https");
        Sanitizer.AllowedSchemes.Add("mailto");
    }

    public static string NormalizeHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var preprocessed = PreprocessHtml(html);
        return Sanitizer.Sanitize(preprocessed);
    }

    public static string? Normalize(string? description, string? descriptionFormat)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        if (string.Equals(descriptionFormat, "html", StringComparison.OrdinalIgnoreCase))
        {
            var sanitized = NormalizeHtml(description);
            if (IsEffectivelyEmpty(sanitized))
                return null;

            return sanitized;
        }

        return description;
    }

    public static bool IsEffectivelyEmptyHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return true;

        try
        {
            var preprocessed = PreprocessHtml(html);
            var text = TagStripRegex.Replace(preprocessed, string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace("\u00A0", " ", StringComparison.Ordinal);
            text = text.Replace("\u200B", string.Empty, StringComparison.Ordinal)
                       .Replace("\u200C", string.Empty, StringComparison.Ordinal)
                       .Replace("\u200D", string.Empty, StringComparison.Ordinal)
                       .Replace("\u2060", string.Empty, StringComparison.Ordinal)
                       .Replace("\uFEFF", string.Empty, StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return html.Length > 5000 ? false : string.IsNullOrWhiteSpace(html);
        }
    }

    private static bool IsEffectivelyEmpty(string sanitizedHtml)
    {
        return IsEffectivelyEmptyHtml(sanitizedHtml);
    }

    private static string PreprocessHtml(string html)
    {
        try
        {
            html = ScriptRegex.Replace(html, string.Empty);
            html = StyleRegex.Replace(html, string.Empty);
            html = CommentRegex.Replace(html, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return html;
    }
}
