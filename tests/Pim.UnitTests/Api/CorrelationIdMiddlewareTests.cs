using Pim.Api.Infrastructure;
using Xunit;

namespace Pim.UnitTests.Api;

public class CorrelationIdMiddlewareTests
{
    [Theory]
    [InlineData(" request-123 ", "request-123")]
    [InlineData("abc.DEF-123_456:789", "abc.DEF-123_456:789")]
    public void ResolveCorrelationId_AcceptsValidIncomingId(string incoming, string expected)
    {
        var correlationId = CorrelationIdMiddleware.ResolveCorrelationId(incoming);

        Assert.Equal(expected, correlationId);
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("bad/id")]
    [InlineData("<script>")]
    public void ResolveCorrelationId_ReplacesInvalidIncomingId(string incoming)
    {
        var correlationId = CorrelationIdMiddleware.ResolveCorrelationId(incoming);

        Assert.NotEqual(incoming, correlationId);
        Assert.Equal(32, correlationId.Length);
        Assert.True(Guid.TryParseExact(correlationId, "N", out _));
    }

    [Fact]
    public void ResolveCorrelationId_ReplacesOversizedIncomingId()
    {
        var incoming = new string('a', 129);

        var correlationId = CorrelationIdMiddleware.ResolveCorrelationId(incoming);

        Assert.NotEqual(incoming, correlationId);
        Assert.Equal(32, correlationId.Length);
        Assert.True(Guid.TryParseExact(correlationId, "N", out _));
    }
}
