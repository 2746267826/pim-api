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
    public void Sanitize_ReturnsNullForInvalidUrl()
    {
        var result = ActivityUrlSanitizer.Sanitize("not a url");

        Assert.Null(result);
    }
}
