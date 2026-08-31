using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;

namespace Pim.Module.Calendar.Services;

public static class EventDescriptionSanitizer
{
    private static readonly HtmlSanitizer Sanitizer;

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

        Sanitizer.RemovingTag += (_, e) =>
        {
            if (e.Tag.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                e.Cancel = false;
        };
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

        var text = Regex.Replace(html, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\u00A0", " ", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(text);
    }

    private static bool IsEffectivelyEmpty(string sanitizedHtml)
    {
        return IsEffectivelyEmptyHtml(sanitizedHtml);
    }

    private static string PreprocessHtml(string html)
    {
        // Remove script/style blocks entirely including content (XSS vectors and Exchange CSS)
        html = Regex.Replace(html, @"<script\b[^>]*>.*?</script\s*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<style\b[^>]*>.*?</style\s*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // Remove HTML comments (e.g. <!-- converted from rtf --> and <!-- .EmailQuote ... -->)
        html = Regex.Replace(html, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        return html;
    }
}
