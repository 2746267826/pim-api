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

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Normalize_Whitespace_ReturnsNull(string? input)
    {
        var result = EventDescriptionSanitizer.Normalize(input, "html");
        Assert.Null(result);
        var plain = EventDescriptionSanitizer.Normalize(input, "plain");
        Assert.Null(plain);
    }

    [Theory]
    [InlineData("<p></p>")]
    [InlineData("<p>   </p>")]
    [InlineData("<p><br></p>")]
    [InlineData("<a></a>")]
    [InlineData("""<a name="BM_BEGIN"></a>""")]
    [InlineData("<br>")]
    public void Normalize_EmptyHtml_ReturnsNull(string input)
    {
        var result = EventDescriptionSanitizer.Normalize(input, "html");
        Assert.Null(result);
    }

    [Fact]
    public void Normalize_ExchangeEmptyWrapper_ReturnsNull()
    {
        var exchangeEmpty = """
            <html>
            <head>
            <meta http-equiv="Content-Type" content="text/html; charset=utf-8">
            <meta name="Generator" content="Microsoft Exchange Server">
            <!-- converted from rtf -->
            <style><!-- .EmailQuote { margin-left: 1pt; padding-left: 4pt; border-left: #800000 2px solid; } --></style>
            </head>
            <body>
            <font face="Times New Roman" size="3"><span style="font-size:12pt;"><a name="BM_BEGIN"></a></span></font>
            </body>
            </html>
            """;

        var result = EventDescriptionSanitizer.Normalize(exchangeEmpty, "html");
        Assert.Null(result);
        Assert.True(EventDescriptionSanitizer.IsEffectivelyEmptyHtml(exchangeEmpty) || result is null);
    }

    [Theory]
    [InlineData("<font>hello</font>", "hello")]
    [InlineData("<span>hello</span>", "hello")]
    [InlineData("""<div dir="ltr">Hello world</div>""", "Hello world")]
    [InlineData("<font>hello <span>world</span></font>", "hello world")]
    [InlineData("""<html><body><font face="Times New Roman" size="3"><span>Real meeting notes here</span></font></body></html>""", "Real meeting notes here")]
    [InlineData("<font>hello <b>bold</b> world</font>", "hello <b>bold</b> world")]
    public void NormalizeHtml_PreservesTextInsideStrippedTags(string input, string expected)
    {
        var result = EventDescriptionSanitizer.Normalize(input, "html");
        Assert.NotNull(result);
        Assert.Contains(expected, result);
        Assert.False(EventDescriptionSanitizer.IsEffectivelyEmptyHtml(result));
    }

    [Theory]
    [InlineData("<p>real content</p>", "<p>real content</p>")]
    [InlineData("<p>Hello<br>world</p>", "<p>Hello<br>world</p>")]
    [InlineData("""<a href="http://example.com">link</a>""", """<a href="http://example.com">link</a>""")]
    public void NormalizeHtml_PreservesAllowedFormatting(string input, string expected)
    {
        var result = EventDescriptionSanitizer.Normalize(input, "html");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("<p>&nbsp;</p>")]
    [InlineData("&#160;")]
    [InlineData("<p> \u00A0 </p>")]
    public void Normalize_NbspOnly_ReturnsNull(string input)
    {
        var result = EventDescriptionSanitizer.Normalize(input, "html");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeHtml_RemovesStyleAndScriptBlocks()
    {
        var input = "<style>body{color:red}</style><p>hi</p><script>alert(1)</script>";
        var result = EventDescriptionSanitizer.Normalize(input, "html");
        Assert.Equal("<p>hi</p>", result);
    }

    [Fact]
    public void IsEffectivelyEmptyHtml_DetectsEmpty()
    {
        Assert.True(EventDescriptionSanitizer.IsEffectivelyEmptyHtml(""));
        Assert.True(EventDescriptionSanitizer.IsEffectivelyEmptyHtml("   "));
        Assert.True(EventDescriptionSanitizer.IsEffectivelyEmptyHtml("<p></p>"));
        Assert.True(EventDescriptionSanitizer.IsEffectivelyEmptyHtml("<a></a>"));
        Assert.False(EventDescriptionSanitizer.IsEffectivelyEmptyHtml("<p>hello</p>"));
        Assert.False(EventDescriptionSanitizer.IsEffectivelyEmptyHtml("hello"));
    }
}
