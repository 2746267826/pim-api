using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Real Kestrel boundary test for the Streamable HTTP transport. The default host ceiling is
/// about 30 MB, which cannot carry a 64 MiB ZIP encoded as base64 inside JSON-RPC.
/// </summary>
public sealed class McpRequestBodyLimitTests
{
    [Fact]
    public async Task Mcp_AcceptsNinetyMiB_AndRejectsNinetySevenMiB_AtKestrelBoundary()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (string.Equals(context.Request.Path, "/mcp", StringComparison.OrdinalIgnoreCase))
                McpServerBootstrap.ApplyRequestBodyLimit(context);
            await next();
        });
        app.MapPost("/mcp", async (HttpContext context) =>
        {
            await using var sink = Stream.Null;
            await context.Request.Body.CopyToAsync(sink, context.RequestAborted);
            return Results.Text("accepted");
        });
        await app.StartAsync();

        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single()),
            Timeout = TimeSpan.FromMinutes(3),
        };

        var ninetyMiB = 90L * 1024 * 1024;
        using (var content = new RepeatedByteContent(ninetyMiB))
        {
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync("/mcp", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("accepted", await response.Content.ReadAsStringAsync());
        }

        // Kestrel rejects an oversized request from its declared Content-Length before reading
        // the body. Send only the headers: streaming 97 MiB while the server aborts can surface
        // as a client-side broken pipe instead of the real 413.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            await DeclaredContentLengthProbeAsync(new Uri(app.Urls.Single()), 97L * 1024 * 1024));
    }

    private static async Task<HttpStatusCode> DeclaredContentLengthProbeAsync(Uri baseAddress,
        long contentLength)
    {
        using var socket = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(baseAddress.Host, baseAddress.Port, timeout.Token);
        await using var stream = socket.GetStream();
        var headers = string.Join("\r\n",
            "POST /mcp HTTP/1.1",
            $"Host: {baseAddress.Host}:{baseAddress.Port}",
            "Content-Type: application/json",
            $"Content-Length: {contentLength}",
            "Connection: close",
            "",
            "");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);

        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
            if (read == 0) break;
            total += read;
        }
        var response = Encoding.ASCII.GetString(buffer, 0, total);
        var statusLine = response.Split("\r\n", StringSplitOptions.None)[0].Split(' ');
        if (statusLine.Length < 2 || !int.TryParse(statusLine[1], out var status))
            throw new InvalidOperationException(
                $"Unexpected HTTP response: {response[..Math.Min(response.Length, 512)]}");
        return (HttpStatusCode)status;
    }

    private sealed class RepeatedByteContent(long length) : HttpContent
    {
        private static readonly byte[] Chunk = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }

        protected override async Task SerializeToStreamAsync(Stream stream,
            TransportContext? context)
        {
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(Chunk.Length, remaining);
                await stream.WriteAsync(Chunk.AsMemory(0, count));
                remaining -= count;
            }
        }
    }
}
