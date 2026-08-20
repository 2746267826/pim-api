using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class EventDescriptionSanitizerTests
{
    [Theory]
    [InlineData("<p><strong>ok</strong><script>alert(1)</script></p>", "<p><strong>ok</strong></p>")]
    [InlineData("<p>safe</p>", "<p>safe</p>")]
    [InlineData("<script>evil()</script>", "")]
    public void NormalizeHtml_RemovesScriptTags(string input, string expected)
    {
        var result = EventDescriptionSanitizer.NormalizeHtml(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("""<a href="javascript:alert(1)">x</a>""", "<a>x</a>")]
    [InlineData("""<a href="JavaScript:void(0)">x</a>""", "<a>x</a>")]
    [InlineData("""<a href="http://safe.com">ok</a>""", """<a href="http://safe.com">ok</a>""")]
    public void NormalizeHtml_RemovesJavascriptHref(string input, string expected)
    {
        var result = EventDescriptionSanitizer.NormalizeHtml(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("plain text description")]
    [InlineData("a < b and c > d")]
    public void Normalize_PlainDescription_ReturnsAsIs(string input)
    {
        var result = EventDescriptionSanitizer.Normalize(input, "plain");
        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_NullOrEmpty_ReturnsNull(string? input)
    {
        var result = EventDescriptionSanitizer.Normalize(input, "html");
        Assert.Null(result);
    }
}
