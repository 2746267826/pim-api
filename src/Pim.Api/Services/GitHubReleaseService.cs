using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pim.Api.Services;

public record GitHubReleaseSnapshot(string? LatestVersion, string? WindowsUrl, string? AndroidUrl, DateTimeOffset? CheckedAt, string? Error, string? ETag);

public class GitHubReleaseService : IHostedService, IDisposable
{
    private readonly HttpClient _http;
    private readonly GitHubReleaseOptions _opts;
    private readonly ILogger<GitHubReleaseService> _log;
    private volatile GitHubReleaseSnapshot _snapshot = new(null, null, null, null, null, null);
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubReleaseSnapshot Snapshot => _snapshot;

    public GitHubReleaseService(HttpClient http, IOptions<GitHubReleaseOptions> opts, ILogger<GitHubReleaseService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    // Backward-compatible overload for existing tests that pass IMemoryCache (now unused)
    public GitHubReleaseService(HttpClient http, IOptions<GitHubReleaseOptions> opts, Microsoft.Extensions.Caching.Memory.IMemoryCache _, ILogger<GitHubReleaseService> log)
        : this(http, opts, log) { }

    public Task StartAsync(CancellationToken ct)
    {
        _ = RefreshAsync(ct);
        _timer = new Timer(async _ =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GitHub release timer refresh failed");
            }
        }, null, _opts.PollInterval, _opts.PollInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Dispose();
        _timer = null;
        try { _gate.Dispose(); } catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        try { _gate.Dispose(); } catch (ObjectDisposedException) { }
    }

    public async Task<GitHubReleaseSnapshot> RefreshAsync(CancellationToken ct)
    {
        // Prevent overlapping executions; handle disposal race during shutdown
        bool entered = false;
        try
        {
            try
            {
                entered = await _gate.WaitAsync(0, ct);
            }
            catch (ObjectDisposedException)
            {
                return _snapshot;
            }
            if (!entered)
            {
                _log.LogInformation("GitHub release refresh skipped due to overlapping execution");
                return _snapshot;
            }
            return await RefreshCoreAsync(ct);
        }
        finally
        {
            if (entered)
            {
                try { _gate.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task<GitHubReleaseSnapshot> RefreshCoreAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_opts.Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("pim-platform");
            if (!string.IsNullOrEmpty(_snapshot.ETag)) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_snapshot.ETag));
            if (!string.IsNullOrEmpty(_opts.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.Token);
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                _snapshot = _snapshot with { CheckedAt = DateTimeOffset.UtcNow };
                _log.LogInformation("GitHub release 304 not modified etag={ETag} duration={Ms}ms", _snapshot.ETag, sw.ElapsedMilliseconds);
                return _snapshot;
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');
            string? win = null, and = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (url != null && !url.StartsWith("https://github.com/2746267826/pim-platform/releases/download/")) continue;
                if (name?.StartsWith("pim-windows-") == true && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) win = url;
                if (name?.StartsWith("pim-android-") == true && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) and = url;
            }
            var etag = resp.Headers.ETag?.Tag;
            _snapshot = new(tag, win, and, DateTimeOffset.UtcNow, null, etag);
            _log.LogInformation("GitHub release refreshed latest={Latest} checkedAt={CheckedAt} duration={Ms}ms", tag, _snapshot.CheckedAt, sw.ElapsedMilliseconds);
            return _snapshot;
        }
        catch (ObjectDisposedException)
        {
            return _snapshot;
        }
        catch (Exception ex)
        {
            _snapshot = _snapshot with { Error = ex.Message, CheckedAt = DateTimeOffset.UtcNow };
            _log.LogWarning(ex, "GitHub release fetch failed checkedAt={CheckedAt}", _snapshot.CheckedAt);
            return _snapshot;
        }
    }
}
