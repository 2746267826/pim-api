using Ganss.Xss;

namespace Pim.Module.Calendar.Services;

public static class EventDescriptionSanitizer
{
    private static readonly HtmlSanitizer Sanitizer;

    static EventDescriptionSanitizer()
    {
        Sanitizer = new HtmlSanitizer();
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
        return Sanitizer.Sanitize(html);
    }

    public static string? Normalize(string? description, string? descriptionFormat)
    {
        if (string.IsNullOrEmpty(description))
            return null;

        if (string.Equals(descriptionFormat, "html", StringComparison.OrdinalIgnoreCase))
            return NormalizeHtml(description);

        return description;
    }
}
