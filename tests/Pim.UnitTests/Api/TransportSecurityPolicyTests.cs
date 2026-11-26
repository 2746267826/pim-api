using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void ForwardedHeaders_DerivesComposeGatewayFromConfiguredSubnet()
    {
        var options = ForwardedHeadersConfigurator.Create(null, "172.31.240.0/24");

        Assert.Contains(IPAddress.Parse("172.31.240.1"), options.KnownProxies);
        Assert.Equal(2, options.KnownNetworks.Count);
    }

    [Fact]
    public void ForwardedHeaders_ChangingSubnetMovesTheDerivedGateway()
    {
        var options = ForwardedHeadersConfigurator.Create(null, "10.77.32.0/20");

        Assert.Contains(IPAddress.Parse("10.77.32.1"), options.KnownProxies);
        Assert.DoesNotContain(IPAddress.Parse("172.31.240.1"), options.KnownProxies);
    }

    [Fact]
    public void ForwardedHeaders_ExplicitConfigurationOverridesSubnetDerivation()
    {
        var options = ForwardedHeadersConfigurator.Create("10.0.0.5/32", "172.31.240.0/24");

        Assert.DoesNotContain(IPAddress.Parse("172.31.240.1"), options.KnownProxies);
        Assert.Contains(
            options.KnownNetworks,
            network => network.Prefix.Equals(IPAddress.Parse("10.0.0.5")) && network.PrefixLength == 32);
    }

    [Theory]
    [InlineData("172.31.240.0")]
    [InlineData("172.31.240.0/0")]
    [InlineData("172.31.240.0/33")]
    [InlineData("not-a-subnet")]
    public void ForwardedHeaders_RejectsInvalidNetworkSubnet(string subnet)
    {
        Assert.Throws<InvalidOperationException>(
            () => ForwardedHeadersConfigurator.Create(null, subnet));
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
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void ForwardedHeaders_RejectsInvalidTrustEntries(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersConfigurator.Create(raw));
    }

    [Fact]
    public async Task ForwardedHeaders_UntrustedSourceCannotSpoofHttps()
    {
        var options = ForwardedHeadersConfigurator.Create("10.0.0.1/32");
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.Scheme = Uri.UriSchemeHttp;
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));

        await middleware.Invoke(context);

        Assert.Equal(Uri.UriSchemeHttp, context.Request.Scheme);
    }

    [Fact]
    public async Task ForwardedHeaders_TrustedProxyCanSetHttps()
    {
        var options = ForwardedHeadersConfigurator.Create("10.0.0.1/32");
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));

        await middleware.Invoke(context);

        Assert.Equal(Uri.UriSchemeHttps, context.Request.Scheme);
    }

    [Fact]
    public void Cors_AllowsHttpsAndLoopbackHttpButRejectsRemoteHttp()
    {
        var result = CorsOriginPolicy.Filter(
            "https://notes.example.com,http://127.0.0.1:3000,http://10.0.2.2:5173,http://notes.example.com,https://notes.example.com/path");

        Assert.Equal(
            new[] { "https://notes.example.com", "http://127.0.0.1:3000", "http://10.0.2.2:5173" },
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

    [Theory]
    [InlineData("/health", false)]
    [InlineData("/health/live", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/api/version", false)]
    [InlineData("/.well-known/acme-challenge/token", false)]
    [InlineData("/api/v1/auth/login", true)]
    [InlineData("/api/v1/mcp/clients", true)]
    [InlineData("/mcp", true)]
    [InlineData("/mcp/", true)]
    [InlineData("/hangfire", true)]
    [InlineData("/metrics", false)]
    [InlineData("/today", true)]
    public void ProtectedPathClassification(string path, bool expected)
    {
        Assert.Equal(expected, !TransportSecurityPolicy.IsExemptPath(new PathString(path)));
    }

    [Fact]
    public void ShouldRejectCleartext_OnlyForRemoteProtectedPaths()
    {
        var options = new TransportSecurityOptions
        {
            EnforceHttps = true,
            AllowLoopbackCleartext = true,
            HstsMaxAgeSeconds = TransportSecurityOptions.DefaultHstsMaxAgeSeconds,
        };

        var remote = new DefaultHttpContext();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        remote.Request.Path = "/api/v1/auth/login";
        Assert.True(TransportSecurityPolicy.ShouldRejectCleartext(options, remote));

        remote.Request.Path = "/health";
        Assert.False(TransportSecurityPolicy.ShouldRejectCleartext(options, remote));

        var loopback = new DefaultHttpContext();
        loopback.Connection.RemoteIpAddress = IPAddress.Loopback;
        loopback.Request.Path = "/api/v1/auth/login";
        Assert.False(TransportSecurityPolicy.ShouldRejectCleartext(options, loopback));

        var https = new DefaultHttpContext();
        https.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        https.Request.Scheme = Uri.UriSchemeHttps;
        https.Request.Path = "/api/v1/auth/login";
        Assert.False(TransportSecurityPolicy.ShouldRejectCleartext(options, https));
    }

    [Fact]
    public void ShouldRejectCleartext_ProductionAlsoRejectsLoopbackProxyWithoutHttps()
    {
        var options = new TransportSecurityOptions
        {
            EnforceHttps = true,
            AllowLoopbackCleartext = false,
            HstsMaxAgeSeconds = TransportSecurityOptions.DefaultHstsMaxAgeSeconds,
        };
        var context = new DefaultHttpContext();
        // 宿主机上的反向代理连接 API 时来源也是回环；漏传 X-Forwarded-Proto
        // 不能因为“来自本机”而放行登录密码或笔记正文。
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Path = "/api/v1/auth/login";

        Assert.True(TransportSecurityPolicy.ShouldRejectCleartext(options, context));

        context.Request.Path = "/health";
        Assert.False(TransportSecurityPolicy.ShouldRejectCleartext(options, context));
    }

    [Fact]
    public void TransportSecurityOptions_ProductionDefaultsToEnforcedHttpsAndHsts()
    {
        var options = TransportSecurityOptions.FromConfiguration(
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment("Production"));

        Assert.True(options.EnforceHttps);
        Assert.False(options.AllowLoopbackCleartext);
        Assert.Equal(TransportSecurityOptions.DefaultHstsMaxAgeSeconds, options.HstsMaxAgeSeconds);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void TransportSecurityOptions_LocalEnvironmentsDefaultToCleartext(string environmentName)
    {
        var options = TransportSecurityOptions.FromConfiguration(
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(environmentName));

        Assert.False(options.EnforceHttps);
        Assert.True(options.AllowLoopbackCleartext);
        Assert.Equal(0, options.HstsMaxAgeSeconds);
    }

    [Fact]
    public void ProductionLoopbackCleartextCanOnlyBeEnabledExplicitly()
    {
        var options = TransportSecurityOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TransportSecurity:AllowLoopbackCleartext"] = "true",
                })
                .Build(),
            new FakeHostEnvironment("Production"));

        Assert.True(options.AllowLoopbackCleartext);
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Pim.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
