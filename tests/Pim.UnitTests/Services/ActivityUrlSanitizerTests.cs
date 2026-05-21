using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityUrlSanitizerTests
{
    [Fact]
    public void Sanitize_RemovesQueryFragmentAndUserInfo()
    {
        var result = ActivityUrlSanitizer.Sanitize("https://alice:secret@example.com/docs/page?token=abc&x=1#section");

        Assert.Equal("https://example.com/docs/page", result);
    }

    [Fact]
    public void Sanitize_RedactsOpaquePathSegments()
    {
        var result = ActivityUrlSanitizer.Sanitize("https://example.com/session/eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9/profile/12345");

        Assert.Equal("https://example.com/session/[redacted]/profile/12345", result);
    }

    [Fact]
    public void Sanitize_RedactsDottedOpaquePathSegments()
    {
        var result = ActivityUrlSanitizer.Sanitize("https://example.com/callback/eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhYmMifQ.signature/docs");

        Assert.Equal("https://example.com/callback/[redacted]/docs", result);
    }

    [Fact]
    public void Sanitize_PreservesNormalPathSegments()
    {
        var result = ActivityUrlSanitizer.Sanitize("https://example.com/en/latest/api/rest.html");

        Assert.Equal("https://example.com/en/latest/api/rest.html", result);
    }

    [Fact]
    public void Sanitize_PreservesLongNormalPathSlugs()
    {
        var result = ActivityUrlSanitizer.Sanitize("https://example.com/docs/release-notes-for-product-v2/rest.html");

        Assert.Equal("https://example.com/docs/release-notes-for-product-v2/rest.html", result);
    }

    [Fact]
    public void Sanitize_ReturnsNullForNullUrl()
    {
        var result = ActivityUrlSanitizer.Sanitize(null);

        Assert.Null(result);
    }

    [Fact]
    public void Sanitize_ReturnsNullForBlankUrl()
    {
        var result = ActivityUrlSanitizer.Sanitize("   ");

        Assert.Null(result);
    }

    [Fact]
    public void Sanitize_ReturnsNullForInvalidUrl()
    {
        var result = ActivityUrlSanitizer.Sanitize("not a url");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("file:///C:/Users/a/secret.txt")]
    [InlineData("data:text/plain,secret")]
    [InlineData("javascript:alert(1)")]
    public void Sanitize_ReturnsNullForNonWebUrls(string url)
    {
        var result = ActivityUrlSanitizer.Sanitize(url);

        Assert.Null(result);
    }

    [Fact]
    public void Sanitize_RedactsPercentEncodedOpaquePathSegments()
    {
        var result = ActivityUrlSanitizer.Sanitize("https://example.com/session/eyJhbGciOiJIUzI1NiJ9%2EeyJzdWIiOiJhYmMifQ%2Esignature/docs");

        Assert.Equal("https://example.com/session/[redacted]/docs", result);
    }
}
