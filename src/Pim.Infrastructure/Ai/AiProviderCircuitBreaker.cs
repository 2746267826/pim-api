using System.Collections.Concurrent;
using System.Net;

namespace Pim.Infrastructure.Ai;

/// <summary>
/// Process-local transport breaker shared by the AI text, vision, connection-test and ASR HTTP
/// paths. It only trips on failures that can be retried after the provider recovers; authentication
/// and request-shape failures are treated as reachable provider responses.
/// </summary>
public sealed class AiProviderCircuitBreaker
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public AiProviderCircuitBreaker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public AiProviderCircuitDecision Enter(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _clock.GetUtcNow();
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        lock (entry)
        {
            if (entry.OpenUntil is { } openUntil && openUntil > now)
                return AiProviderCircuitDecision.Block(openUntil - now, entry.Failures);
            if (entry.ProbeInFlight)
                return AiProviderCircuitDecision.Block(TimeSpan.FromSeconds(1), entry.Failures);
            if (entry.OpenUntil is not null)
            {
                entry.OpenUntil = null;
                entry.ProbeInFlight = true;
            }
            return AiProviderCircuitDecision.Allow(entry.Failures);
        }
    }

    public void Reachable(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        lock (entry)
        {
            entry.Failures = 0;
            entry.OpenUntil = null;
            entry.ProbeInFlight = false;
        }
    }

    public void TransientFailure(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        var now = _clock.GetUtcNow();
        lock (entry)
        {
            entry.Failures++;
            if (entry.ProbeInFlight || entry.Failures >= FailureThreshold)
            {
                entry.OpenUntil = now.Add(BreakDuration);
                entry.ProbeInFlight = false;
            }
        }
    }

    /// <summary>
    /// A caller cancellation is not evidence that the provider failed. Clearing only the half-open
    /// probe preserves an already-open breaker and avoids charging the cancellation as a failure.
    /// </summary>
    public void AbandonProbe(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        lock (entry) entry.ProbeInFlight = false;
    }

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset? OpenUntil;
        public bool ProbeInFlight;
    }
}

public sealed record AiProviderCircuitDecision(bool Allowed, TimeSpan RetryAfter, int FailureCount)
{
    public static AiProviderCircuitDecision Allow(int failures) => new(true, TimeSpan.Zero, failures);
    public static AiProviderCircuitDecision Block(TimeSpan retryAfter, int failures) =>
        new(false, retryAfter, failures);
}

public sealed class AiProviderCircuitOpenException : HttpRequestException
{
    public AiProviderCircuitOpenException(TimeSpan retryAfter)
        : base($"模型服务连续失败，已临时熔断；本次未发送服务商。请在约 {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} 秒后明确重试。")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}

/// <summary>Applies the transport breaker before the request body handler can mark an attempt.</summary>
public sealed class AiProviderCircuitBreakerHandler(AiProviderCircuitBreaker breaker) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var key = Key(request.RequestUri);
        if (key is null)
            return await base.SendAsync(request, cancellationToken);

        var decision = breaker.Enter(key);
        if (!decision.Allowed)
            throw new AiProviderCircuitOpenException(decision.RetryAfter);

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (IsTransient(response.StatusCode))
                breaker.TransientFailure(key);
            else
                breaker.Reachable(key);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            breaker.AbandonProbe(key);
            throw;
        }
        catch (OperationCanceledException)
        {
            breaker.TransientFailure(key);
            throw;
        }
        catch (HttpRequestException)
        {
            breaker.TransientFailure(key);
            throw;
        }
        catch
        {
            // A local serialization/configuration failure is not provider evidence.
            breaker.AbandonProbe(key);
            throw;
        }
    }

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
        || (int)status is 425 or 507 or 508 or 510;

    private static string? Key(Uri? uri) => uri is null || !uri.IsAbsoluteUri
        ? null
        : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
}
