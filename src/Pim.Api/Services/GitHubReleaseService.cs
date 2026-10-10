using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pim.Api.Services;

/// <summary>
/// 客户端版本快照。字段含义与改造前一致，客户端接口的输出格式不变。
/// </summary>
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

/// <summary>
/// 客户端更新检查数据源：轮询各客户端仓发版时上传的 version.json。
///
/// 拆仓前这里读 GitHub releases API 并靠资产文件名猜版本，客户端安装包一分散到
/// 各仓（且资产名带 -vc 后缀）就永远解析不到。现在改为读发版方显式写入的
/// version.json，通过 release 的稳定直链获取：
///   https://github.com/{repo}/releases/latest/download/version.json
///
/// 该直链无需 token、不受 API 限流；版本号与下载地址都由文件本身给出，
/// 不再依赖资产命名约定。
/// </summary>
public class GitHubReleaseService : IHostedService, IDisposable
{
    private readonly HttpClient _http;
    private readonly GitHubReleaseOptions _opts;
    private readonly ILogger<GitHubReleaseService> _log;
    private volatile GitHubReleaseSnapshot _snapshot = new(null, null, null, null, null, null, null, null, null, null, null, null);
    private Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _winETag;
    private string? _andETag;
    private VersionFile? _winFile;
    private VersionFile? _andFile;

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
                _log.LogWarning(ex, "Client version timer refresh failed");
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
                _log.LogInformation("Client version refresh skipped due to overlapping execution");
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

    private sealed record ComponentVersion(string? Version, string? Url);

    private sealed record VersionFile(string? Version, Dictionary<string, ComponentVersion> Components);

    private async Task<GitHubReleaseSnapshot> RefreshCoreAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // 两个客户端仓并发拉取，互不阻塞：一个仓没发版（404）不影响另一个。
            var winTask = FetchVersionFileAsync(_opts.WindowsRepo, _winETag, ct);
            var andTask = FetchVersionFileAsync(_opts.AndroidRepo, _andETag, ct);
            await Task.WhenAll(winTask, andTask);

            var win = winTask.Result;
            var and = andTask.Result;
            if (win.ETag != null) _winETag = win.ETag;
            if (and.ETag != null) _andETag = and.ETag;

            // 200 → 用新内容刷新缓存；304/读取失败 → 沿用上次成功解析的结果；
            // 404（该仓尚无 release）→ 明确清空，避免仓库删除发布后仍报旧版本。
            if (win.File != null) _winFile = win.File;
            else if (win.NotFound) _winFile = null;
            if (and.File != null) _andFile = and.File;
            else if (and.NotFound) _andFile = null;

            var winFile = _winFile;
            var andFile = _andFile;

            // 组件必须同时给出 url 才生效：只有版本号没有下载地址，会让客户端提示
            // 「发现新版」却无从下载（外部链接也已在解析阶段被安全校验拒掉）。
            var winComp = OnlyWithUrl(Get(winFile, "windows"));
            var shellWinComp = OnlyWithUrl(Get(winFile, "shellWindows"));
            var andComp = OnlyWithUrl(Get(andFile, "android"));
            var shellAndComp = OnlyWithUrl(Get(andFile, "shellAndroid"));

            // 只有「拿不到任何组件」才算整体失败；部分仓没发版是正常状态
            // 404（尚未发版）不算故障：客户端第一次装机时仓库本来就还没有 release。
            var errs = new List<string>();
            if (win.Error != null && !win.NotFound) errs.Add($"windows: {win.Error}");
            if (and.Error != null && !and.NotFound) errs.Add($"android: {and.Error}");
            string? error = errs.Count > 0 ? string.Join("; ", errs) : null;

            _snapshot = new GitHubReleaseSnapshot(
                LatestVersion: null, // API 自身版本由 /api/version 取程序集版本，不再依赖网络
                WindowsVersion: winComp?.Version,
                WindowsUrl: winComp?.Url,
                AndroidVersion: andComp?.Version,
                AndroidUrl: andComp?.Url,
                ShellWindowsVersion: shellWinComp?.Version,
                ShellWindowsUrl: shellWinComp?.Url,
                ShellAndroidVersion: shellAndComp?.Version,
                ShellAndroidUrl: shellAndComp?.Url,
                CheckedAt: DateTimeOffset.UtcNow,
                Error: error,
                ETag: CombineETag(win.ETag, and.ETag));

            _log.LogInformation(
                "Client version refreshed windows={Windows} android={Android} shellWin={ShellWin} shellAnd={ShellAnd} error={Error} duration={Ms}ms",
                _snapshot.WindowsVersion, _snapshot.AndroidVersion,
                _snapshot.ShellWindowsVersion, _snapshot.ShellAndroidVersion,
                error, sw.ElapsedMilliseconds);
            return _snapshot;
        }
        catch (ObjectDisposedException)
        {
            return _snapshot;
        }
        catch (Exception ex)
        {
            _snapshot = _snapshot with { Error = ex.Message, CheckedAt = DateTimeOffset.UtcNow };
            _log.LogWarning(ex, "Client version fetch failed checkedAt={CheckedAt}", _snapshot.CheckedAt);
            return _snapshot;
        }
    }

    /// <summary>缺少下载地址的组件不生效（避免「有版本、无地址」的假更新提示）。</summary>
    private static ComponentVersion? OnlyWithUrl(ComponentVersion? c)
        => c != null && !string.IsNullOrEmpty(c.Url) ? c : null;

    private static ComponentVersion? Get(VersionFile? f, string key)
        => f != null && f.Components.TryGetValue(key, out var v) ? v : null;

    private static string? CombineETag(string? a, string? b)
        => (a, b) switch
        {
            (null, null) => null,
            (null, var y) => y,
            (var x, null) => x,
            var (x, y) => x + "|" + y
        };

    private sealed record FetchResult(VersionFile? File, string? Error, string? ETag, bool NotFound = false);

    /// <summary>
    /// 读取某个仓的 version.json。使用 releases/latest/download 稳定直链：
    /// 该地址在仓库还没有 release 时返回 404 —— 视为「该仓尚未发版」，不是错误。
    /// </summary>
    private async Task<FetchResult> FetchVersionFileAsync(string repo, string? etag, CancellationToken ct)
    {
        var url = $"https://github.com/{repo}/releases/latest/download/{_opts.VersionFileName}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("pim-api");
            req.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrEmpty(etag)) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
            if (!string.IsNullOrEmpty(_opts.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.Token);

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchResult(null, null, etag);
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return new FetchResult(null, $"{repo} 尚无 release（未发版）", resp.Headers.ETag?.Tag, NotFound: true);
            }
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString()
                : null;

            var components = new Dictionary<string, ComponentVersion>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in assets.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    string? cv = prop.Value.TryGetProperty("version", out var cvEl) && cvEl.ValueKind == JsonValueKind.String
                        ? cvEl.GetString() : null;
                    string? cu = prop.Value.TryGetProperty("url", out var cuEl) && cuEl.ValueKind == JsonValueKind.String
                        ? cuEl.GetString() : null;
                    if (cv == null && cu == null) continue;
                    // 只接受来自本仓 release 的下载地址，避免被版本文件里的外部链接带偏
                    if (cu != null && !cu.StartsWith($"https://github.com/{repo}/releases/download/", StringComparison.Ordinal))
                    {
                        _log.LogWarning("Ignoring {Repo} version.json asset {Key}: url outside this repository", repo, prop.Name);
                        continue;
                    }
                    components[prop.Name] = new ComponentVersion(cv, cu);
                }
            }

            return new FetchResult(new VersionFile(version, components), null, resp.Headers.ETag?.Tag);
        }
        catch (Exception ex)
        {
            return new FetchResult(null, $"{repo}: {ex.Message}", etag);
        }
    }
}
