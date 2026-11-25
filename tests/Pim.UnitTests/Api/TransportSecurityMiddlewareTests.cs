using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Infrastructure.Http;
using Xunit;

namespace Pim.UnitTests.Api;

public class TransportSecurityMiddlewareTests
{
    [Fact]
    public async Task RemoteCleartextRequestIsRejectedBeforeNextMiddleware()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.Scheme = Uri.UriSchemeHttp;
        context.Request.Path = "/api/v1/auth/login";
        context.Response.Body = new MemoryStream();
        var nextCalled = false;

        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
        Assert.Equal(
            TransportSecurityMiddleware.HttpsRequiredValue,
            context.Response.Headers[TransportSecurityMiddleware.TransportHeaderName]);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("42600", body);
        Assert.Contains("HTTPS", body);
    }

    [Fact]
    public async Task LoopbackHealthAndVersionRemainUsableOverHttp()
    {
        foreach (var path in new[] { "/health", "/health/ready", "/api/version" })
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Scheme = Uri.UriSchemeHttp;
            context.Request.Path = path;
            var nextCalled = false;

            var middleware = CreateMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled, path);
            Assert.Equal(
                TransportSecurityMiddleware.CleartextValue,
                context.Response.Headers[TransportSecurityMiddleware.TransportHeaderName]);
        }
    }

    [Fact]
    public async Task HttpsResponseGetsHstsHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = Uri.UriSchemeHttps;
        context.Request.Path = "/api/v1/notes";
        var nextCalled = false;

        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(
            $"max-age={TransportSecurityOptions.DefaultHstsMaxAgeSeconds}",
            context.Response.Headers["Strict-Transport-Security"]);
        Assert.False(context.Response.Headers.ContainsKey(
            TransportSecurityMiddleware.TransportHeaderName));
    }

    [Fact]
    public async Task EnforcementCanBeDisabledForLocalDevelopment()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Request.Scheme = Uri.UriSchemeHttp;
        context.Request.Path = "/api/v1/notes";
        var nextCalled = false;

        var middleware = CreateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new TransportSecurityOptions
            {
                EnforceHttps = false,
                AllowLoopbackCleartext = true,
                HstsMaxAgeSeconds = 0,
            });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.False(context.Response.Headers.ContainsKey(
            TransportSecurityMiddleware.TransportHeaderName));
    }

    [Fact]
    public async Task LoopbackCleartextIsRejectedWhenDevelopmentExceptionIsOff()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = Uri.UriSchemeHttp;
        context.Request.Path = "/api/v1/auth/login";
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = CreateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new TransportSecurityOptions
            {
                EnforceHttps = true,
                AllowLoopbackCleartext = false,
                HstsMaxAgeSeconds = TransportSecurityOptions.DefaultHstsMaxAgeSeconds,
            });

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, context.Response.StatusCode);
        Assert.Equal(
            TransportSecurityMiddleware.HttpsRequiredValue,
            context.Response.Headers[TransportSecurityMiddleware.TransportHeaderName]);
    }

    private static TransportSecurityMiddleware CreateMiddleware(
        RequestDelegate next,
        TransportSecurityOptions? options = null)
        => new(
            next,
            options ?? new TransportSecurityOptions
            {
                EnforceHttps = true,
                AllowLoopbackCleartext = true,
                HstsMaxAgeSeconds = TransportSecurityOptions.DefaultHstsMaxAgeSeconds,
            },
            NullLogger<TransportSecurityMiddleware>.Instance);
}
