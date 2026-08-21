using System.Text.Json;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly OpsRateLimiter _limiter;
    private readonly ILogger<OpsRateLimitMiddleware> _logger;

    public OpsRateLimitMiddleware(RequestDelegate next, OpsRateLimiter limiter, ILogger<OpsRateLimitMiddleware> logger)
    {
        _next = next;
        _limiter = limiter;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/ops"))
        {
            await _next(ctx);
            return;
        }

        var ip = OpsIpHelper.GetClientIp(ctx);

        if (!_limiter.TryAcquire(ip, 0, out var retryAfter))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = retryAfter.ToString();
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { code = 42901, message = "RateLimited" }));
            return;
        }

        // Wrap response body to count bytes
        var originalBody = ctx.Response.Body;
        var counting = new CountingStream(originalBody);
        ctx.Response.Body = counting;

        try
        {
            await _next(ctx);
        }
        finally
        {
            ctx.Response.Body = originalBody;
            var bytes = counting.BytesWritten;
            // Also include ContentLength if counting is 0 but header set
            if (bytes == 0 && ctx.Response.Headers.TryGetValue("Content-Length", out var clVals) && long.TryParse(clVals.ToString(), out var cl))
                bytes = cl;
            if (bytes > 0)
            {
                _limiter.AddBytes(ip, bytes);
            }
            // ensure counting stream flushes to original already done via writes
        }
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesWritten { get; private set; }

        public CountingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            BytesWritten += count;
            _inner.Write(buffer, offset, count);
        }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            BytesWritten += count;
            await _inner.WriteAsync(buffer, offset, count, ct);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten += buffer.Length;
            _inner.Write(buffer);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            BytesWritten += buffer.Length;
            await _inner.WriteAsync(buffer, ct);
        }
    }
}
