using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class AppNameNormalizerTests
{
    [Theory]
    [InlineData("msedge.exe", "msedge")]
    [InlineData("msedge", "msedge")]
    [InlineData("Codex.exe", "codex")]
    [InlineData("Google Chrome", "google chrome")]
    [InlineData(" PowerToys.Peek.UI.exe ", "powertoys.peek.ui")]
    public void Normalize_ReturnsStableLowercaseAppKey(string input, string expected)
    {
        Assert.Equal(expected, AppNameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_ReturnsUnknownForBlankInput()
    {
        Assert.Equal("unknown", AppNameNormalizer.Normalize(""));
        Assert.Equal("unknown", AppNameNormalizer.Normalize(null));
    }
}
