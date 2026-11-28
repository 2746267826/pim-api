using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

public class UploadUrlSecurityTests
{
    [Fact]
    public void AcceptsAbsoluteHttpsWithoutEchoingQuery()
    {
        var validation = UploadUrlSecurity.Validate(
            "https://upload.example.com/session?sig=SECRET-SIGNATURE",
            allowRelative: false);

        Assert.True(validation.IsSecure);
        Assert.Equal("upload.example.com", validation.Host);
        Assert.DoesNotContain("SECRET", validation.Host);
    }

    [Theory]
    [InlineData("http://upload.example.com/session?sig=SECRET-SIGNATURE")]
    [InlineData("not-a-url?sig=SECRET-SIGNATURE")]
    [InlineData("javascript:alert(1)")]
    public void RejectsUnsafeAbsoluteUrls(string url)
    {
        var validation = UploadUrlSecurity.Validate(url, allowRelative: false);

        Assert.False(validation.IsSecure);
        Assert.False(string.IsNullOrWhiteSpace(validation.Reason));
        Assert.DoesNotContain("SECRET", validation.Reason ?? string.Empty);
    }

    [Fact]
    public void RelativeUrlIsOnlyAllowedForBuiltinLocalStorage()
    {
        Assert.True(UploadUrlSecurity.Validate("/api/v1/files/local/uploads/x?ticket=t", allowRelative: true).IsSecure);
        Assert.False(UploadUrlSecurity.Validate("/api/v1/files/local/uploads/x?ticket=t", allowRelative: false).IsSecure);
    }

    [Fact]
    public void RejectionLogsProviderFilePhaseAndHostButNotSignedParameters()
    {
        var logger = new CapturingLogger();

        var error = Assert.Throws<DomainException>(() => UploadUrlSecurity.EnsureSecure(
            "http://upload.example.com/session?sig=SECRET-SIGNATURE",
            "onedrive",
            "大视频.mp4",
            allowRelative: false,
            logger));

        Assert.Equal(UploadUrlSecurity.InsecureUrlCode, error.ErrorCode);
        Assert.Contains("HTTPS", error.Message);
        Assert.DoesNotContain("SECRET-SIGNATURE", error.Message);
        var log = Assert.Single(logger.Messages);
        Assert.Contains("onedrive", log);
        Assert.Contains("大视频.mp4", log);
        Assert.Contains(UploadUrlSecurity.LogPhase, log);
        Assert.Contains("upload.example.com", log);
        Assert.DoesNotContain("SECRET-SIGNATURE", log);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
