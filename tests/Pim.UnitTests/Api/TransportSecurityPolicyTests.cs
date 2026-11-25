using Microsoft.AspNetCore.Http;
using Pim.Api.Infrastructure.Http;
using Xunit;

namespace Pim.UnitTests.Api;

public class TransportSecurityPolicyTests
{
    [Fact]
    public void ForwardedHeaders_DefaultTrustsLoopbackOnly()
    {
        var options = ForwardedHeadersConfigurator.Create(null);

        Assert.Empty(options.KnownProxies);
        Assert.Equal(2, options.KnownNetworks.Count);
        Assert.Equal(1, options.ForwardLimit);
    }

    [Fact]
    public void ForwardedHeaders_AcceptsExplicitIpAndCidr()
    {
        var options = ForwardedHeadersConfigurator.Create("127.0.0.1, 172.31.240.0/24, ::1/128");

        Assert.Single(options.KnownProxies);
        Assert.Equal(2, options.KnownNetworks.Count);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("::1/129")]
    public void ForwardedHeaders_RejectsInvalidTrustEntries(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersConfigurator.Create(raw));
    }

    [Fact]
    public void Cors_AllowsHttpsAndLoopbackHttpButRejectsRemoteHttp()
    {
        var result = CorsOriginPolicy.Filter(
            "https://notes.example.com,http://127.0.0.1:3000,http://notes.example.com,https://notes.example.com/path");

        Assert.Equal(
            new[] { "https://notes.example.com", "http://127.0.0.1:3000" },
            result.AllowedOrigins);
        Assert.Equal(
            new[] { "http://notes.example.com", "https://notes.example.com/path" },
            result.RejectedOrigins);
    }

    [Fact]
    public async Task RequestSchemeHeader_ReflectsEffectiveScheme()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = Uri.UriSchemeHttps;
        var middleware = new RequestSchemeHeaderMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("https", context.Response.Headers[RequestSchemeHeaderMiddleware.HeaderName]);
    }
}
