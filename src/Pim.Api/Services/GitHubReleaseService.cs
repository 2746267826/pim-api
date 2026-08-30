using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pim.Api.Services;

public record GitHubReleaseSnapshot(
    string? LatestVersion,
    string? WindowsVersion,
    string? WindowsUrl,
    string? AndroidVersion,
    string? AndroidUrl,
    string? ShellWindowsVersion,
    string? ShellWindowsUrl,
    string? ShellAndroidVersion,
    string? ShellAndroidUrl,
    DateTimeOffset? CheckedAt,
    string? Error,
    string? ETag);

public class GitHubReleaseService : IHostedService, IDisposable
{
    private readonly HttpClient _http;
    private readonly GitHubReleaseOptions _opts;
    private readonly ILogger<GitHubReleaseService> _log;
    private volatile GitHubReleaseSnapshot _snapshot = new(null, null, null, null, null, null, null, null, null, null, null, null);
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubReleaseSnapshot Snapshot => _snapshot;

    public GitHubReleaseService(HttpClient http, IOptions<GitHubReleaseOptions> opts, ILogger<GitHubReleaseService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

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
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            string? win = null, and = null, shellWin = null, shellAnd = null;
            string? winVer = null, andVer = null, shellWinVer = null, shellAndVer = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (string.IsNullOrEmpty(name)) continue;
                if (string.IsNullOrEmpty(url)) continue;
                if (!url.StartsWith($"https://github.com/{_opts.Repo}/releases/download/", StringComparison.Ordinal)) continue;
                // shell variants must be checked before generic windows/android to avoid confusion
                if (name.StartsWith("pim-shell-windows-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    shellWin = url;
                    shellWinVer = ExtractVersion(name, "pim-shell-windows-v", ".zip");
                }
                else if (name.StartsWith("pim-shell-android-", StringComparison.Ordinal) && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    shellAnd = url;
                    shellAndVer = ExtractVersion(name, "pim-shell-android-v", ".apk");
                }
                else if (name.StartsWith("pim-windows-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    win = url;
                    winVer = ExtractVersion(name, "pim-windows-v", ".zip");
                }
                else if (name.StartsWith("pim-android-", StringComparison.Ordinal) && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    and = url;
                    andVer = ExtractVersion(name, "pim-android-v", ".apk");
                }
            }
            // fallback to tag when version not parsed but url exists (keep tag to avoid null version with valid url)
            if (win != null && winVer == null) winVer = tag;
            if (and != null && andVer == null) andVer = tag;
            if (shellWin != null && shellWinVer == null) shellWinVer = tag;
            if (shellAnd != null && shellAndVer == null) shellAndVer = tag;
            // if no component assets found at all, keep versions null so fallback logic can use config
            var etag = resp.Headers.ETag?.Tag;
            _snapshot = new(tag, winVer, win, andVer, and, shellWinVer, shellWin, shellAndVer, shellAnd, DateTimeOffset.UtcNow, null, etag);
            _log.LogInformation("GitHub release refreshed latest={Latest} windows={WindowsVersion} android={AndroidVersion} shellWin={ShellWin} shellAnd={ShellAnd} checkedAt={CheckedAt} duration={Ms}ms", tag, winVer, andVer, shellWinVer, shellAndVer, _snapshot.CheckedAt, sw.ElapsedMilliseconds);
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

    private static string? ExtractVersion(string name, string prefix, string suffix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var mid = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
        if (string.IsNullOrWhiteSpace(mid)) return null;
        // android variants have optional "-vc<digits>" suffix after version
        var vcIdx = mid.IndexOf("-vc", StringComparison.OrdinalIgnoreCase);
        if (vcIdx >= 0) mid = mid.Substring(0, vcIdx);
        mid = mid.Trim();
        return string.IsNullOrEmpty(mid) ? null : mid;
    }
}
