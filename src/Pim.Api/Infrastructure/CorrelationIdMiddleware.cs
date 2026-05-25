using Serilog.Context;

namespace Pim.Api.Infrastructure;

public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxCorrelationIdLength = 128;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var values)
            ? ResolveCorrelationId(values.Count > 0 ? values[0] : null)
            : GenerateCorrelationId();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    public static string ResolveCorrelationId(string? value)
    {
        var incoming = value?.Trim();
        return IsValidCorrelationId(incoming) ? incoming! : GenerateCorrelationId();
    }

    private static bool IsValidCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxCorrelationIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character != '-'
                && character != '_'
                && character != '.'
                && character != ':')
            {
                return false;
            }
        }

        return true;
    }

    private static string GenerateCorrelationId() => Guid.NewGuid().ToString("N");
}
