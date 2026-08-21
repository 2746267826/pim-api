using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Pim.Core.Exceptions;

namespace Pim.Api.Services;

public record LogFileInfo(string Name, long Size, DateTimeOffset Mtime, int? RowsEstimate);
public record OpsLogsQuery
{
    public string? File { get; set; }
    public int Limit { get; set; } = 50;
    public string? Level { get; set; }
    public string? Keyword { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Cursor { get; set; }
}
public record OpsLogsResult(IReadOnlyList<string> Lines, bool Truncated, string? NextCursor = null);

public sealed class OpsLogsService
{
    private readonly string _logDir;
    private static readonly Regex FileNameRegex = new(@"^[a-zA-Z0-9_.-]+\.jsonl$", RegexOptions.Compiled);
    private const int MaxLimit = 500;
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(10);

    public OpsLogsService(IConfiguration cfg) : this(cfg["Logging:LogDir"] ?? "/data/pim/logs") { }
    public OpsLogsService(string dir) => _logDir = dir;

    public Task<IReadOnlyList<LogFileInfo>> ListFilesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_logDir))
            return Task.FromResult<IReadOnlyList<LogFileInfo>>(Array.Empty<LogFileInfo>());
        var files = Directory.GetFiles(_logDir, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new LogFileInfo(f.Name, f.Length, f.LastWriteTimeUtc, null))
            .ToList();
        return Task.FromResult<IReadOnlyList<LogFileInfo>>(files);
    }

    public async Task<OpsLogsResult> TailAsync(string file, int lines, string? level, string? keyword, CancellationToken ct = default)
    {
        if (!FileNameRegex.IsMatch(file)) throw new DomainException(40002, "InvalidFileName");
        if (lines < 1 || lines > MaxLimit) throw new DomainException(40003, "Limit must be 1-500");
        var path = Path.Combine(_logDir, file);
        if (!File.Exists(path)) throw new DomainException(40401, "LogFileNotFound");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        var result = await ReadTailAsync(fs, lines, level, keyword, ct);
        return result;
    }

    public async Task<OpsLogsResult> QueryAsync(OpsLogsQuery q, CancellationToken ct = default)
    {
        if (q.Limit < 1 || q.Limit > MaxLimit) throw new DomainException(40003, "Limit must be 1-500");
        if (q.File != null && !FileNameRegex.IsMatch(q.File)) throw new DomainException(40002, "InvalidFileName");

        DateTimeOffset? from = null, to = null;
        if (!string.IsNullOrWhiteSpace(q.From))
        {
            if (!DateTimeOffset.TryParse(q.From, out var f)) throw new DomainException(40002, "InvalidFrom");
            from = f;
        }
        if (!string.IsNullOrWhiteSpace(q.To))
        {
            if (!DateTimeOffset.TryParse(q.To, out var t)) throw new DomainException(40002, "InvalidTo");
            to = t;
        }

        // decode cursor base64(file:offset)
        string? cursorFile = null;
        long cursorOffset = 0;
        if (!string.IsNullOrWhiteSpace(q.Cursor))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(q.Cursor));
                var sep = decoded.IndexOf(':');
                if (sep > 0)
                {
                    cursorFile = decoded[..sep];
                    long.TryParse(decoded[(sep + 1)..], out cursorOffset);
                    if (!FileNameRegex.IsMatch(cursorFile)) throw new DomainException(40002, "InvalidCursor");
                }
                else throw new DomainException(40002, "InvalidCursor");
            }
            catch (DomainException) { throw; }
            catch { throw new DomainException(40002, "InvalidCursor"); }
        }

        // Determine files to scan
        List<string> filesToScan;
        if (q.File != null)
        {
            var p = Path.Combine(_logDir, q.File);
            if (!File.Exists(p)) throw new DomainException(40401, "LogFileNotFound");
            filesToScan = new() { p };
            // if cursor file differs from query file, ignore cursor
            if (cursorFile != null && cursorFile != q.File) cursorOffset = 0;
        }
        else
        {
            if (!Directory.Exists(_logDir)) return new OpsLogsResult(Array.Empty<string>(), false, null);
            var all = Directory.GetFiles(_logDir, "*.jsonl").OrderBy(f => f).ToList();
            filesToScan = all;
            // if cursor specified, skip files before cursor file
            if (cursorFile != null)
            {
                var idx = filesToScan.FindIndex(f => Path.GetFileName(f) == cursorFile);
                if (idx >= 0) filesToScan = filesToScan.Skip(idx).ToList();
                else cursorOffset = 0;
            }
        }

        var sw = Stopwatch.StartNew();
        long bytes = 0;
        var collected = new List<string>();
        string? nextCursor = null;
        bool truncated = false;

        foreach (var filePath in filesToScan)
        {
            if (collected.Count >= q.Limit) break;
            if (sw.Elapsed > MaxDuration) { truncated = true; break; }
            var fileName = Path.GetFileName(filePath);
            long startOffset = 0;
            if (cursorFile != null && fileName == cursorFile)
                startOffset = cursorOffset;
            // subsequent files start at 0, cursor consumed
            var (lines, fileTruncated, nextOffset, newBytes) = await ReadQueryFileAsync(filePath, startOffset, q, from, to, collected.Count, sw, bytes, ct);
            bytes = newBytes;
            foreach (var l in lines)
            {
                if (collected.Count >= q.Limit) break;
                collected.Add(l);
            }
            if (fileTruncated) { truncated = true; nextCursor = EncodeCursor(fileName, nextOffset); break; }
            // if we filled limit and there are more lines, generate cursor
            if (collected.Count >= q.Limit)
            {
                // check if file has more
                var remaining = await HasMoreLinesAsync(filePath, nextOffset, q, from, to, ct);
                if (remaining) nextCursor = EncodeCursor(fileName, nextOffset);
                else
                {
                    // next file
                    var idx = filesToScan.IndexOf(filePath);
                    if (idx + 1 < filesToScan.Count)
                        nextCursor = EncodeCursor(Path.GetFileName(filesToScan[idx + 1]), 0);
                }
                break;
            }
            // prepare for next file: reset cursorFile so next file starts at 0
            cursorFile = null;
            cursorOffset = 0;
            if (sw.Elapsed > MaxDuration) { truncated = true; if (nextCursor == null) nextCursor = EncodeCursor(fileName, nextOffset); break; }
            if (bytes >= MaxBytes) { truncated = true; if (nextCursor == null) nextCursor = EncodeCursor(fileName, nextOffset); break; }
        }

        return new OpsLogsResult(collected, truncated, nextCursor);
    }

    private async Task<(List<string> lines, bool truncated, long nextOffset, long newBytes)> ReadQueryFileAsync(
        string path, long startOffset, OpsLogsQuery q, DateTimeOffset? from, DateTimeOffset? to,
        int alreadyCollected, Stopwatch sw, long bytes, CancellationToken ct)
    {
        var result = new List<string>();
        long offset = startOffset;
        bool truncated = false;
        long nextOffset = startOffset;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        if (startOffset > 0) fs.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > MaxDuration) { truncated = true; break; }
            // track byte length including newline
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            // filters
            if (!MatchesFilters(line, q.Level, q.Keyword, from, to)) { offset += lineBytes; nextOffset = offset; continue; }
            if (bytes + lineBytes > MaxBytes) { truncated = true; break; }
            if (result.Count + alreadyCollected >= q.Limit) break;
            // check timeout before adding
            result.Add(line);
            bytes += lineBytes;
            offset += lineBytes;
            nextOffset = offset;
            if (bytes >= MaxBytes) { truncated = true; break; }
        }
        // if truncated due to limit, nextOffset already points after last returned line
        return (result, truncated, nextOffset, bytes);
    }

    private async Task<bool> HasMoreLinesAsync(string path, long offset, OpsLogsQuery q, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        fs.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (MatchesFilters(line, q.Level, q.Keyword, from, to)) return true;
        }
        return false;
    }

    private static string EncodeCursor(string file, long offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{file}:{offset}"));

    private async Task<OpsLogsResult> ReadTailAsync(FileStream fs, int lines, string? level, string? keyword, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        var allMatching = new List<string>();
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string? line;
        // Read all lines then filter tail - simple but respects truncation
        var rawLines = new List<string>();
        while ((line = await reader.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > MaxDuration) break;
            rawLines.Add(line);
        }
        // filter and take tail
        var filtered = new List<string>();
        foreach (var l in rawLines)
        {
            if (sw.Elapsed > MaxDuration) break;
            if (!MatchesFilters(l, level, keyword, null, null)) continue;
            filtered.Add(l);
        }
        // take last 'lines' entries
        var tail = filtered.Count > lines ? filtered.Skip(filtered.Count - lines).ToList() : filtered;

        // apply 5MB/10s truncation on output
        var output = new List<string>();
        bool truncated = false;
        foreach (var l in tail)
        {
            if (sw.Elapsed > MaxDuration) { truncated = true; break; }
            var lb = Encoding.UTF8.GetByteCount(l) + 1;
            if (bytes + lb > MaxBytes) { truncated = true; break; }
            output.Add(l);
            bytes += lb;
        }
        // Also if rawLines reading was interrupted by timeout, truncated
        if (sw.Elapsed > MaxDuration) truncated = true;
        return new OpsLogsResult(output, truncated);
    }

    private static bool MatchesFilters(string line, string? level, string? keyword, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!string.IsNullOrWhiteSpace(level))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("@l", out var lv))
                {
                    var lvStr = lv.GetString();
                    if (!string.Equals(lvStr, level, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else return false;
            }
            catch { return false; }
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            if (!line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (from != null || to != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("@t", out var tp))
                {
                    var ts = tp.GetString();
                    if (ts != null && DateTimeOffset.TryParse(ts, out var dto))
                    {
                        if (from != null && dto < from) return false;
                        if (to != null && dto > to) return false;
                    }
                    else return false;
                }
                else return false;
            }
            catch { return false; }
        }
        return true;
    }
}
