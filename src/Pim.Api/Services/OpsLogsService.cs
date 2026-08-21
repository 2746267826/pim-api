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

        List<string> filesToScan;
        if (q.File != null)
        {
            var p = Path.Combine(_logDir, q.File);
            if (!File.Exists(p)) throw new DomainException(40401, "LogFileNotFound");
            filesToScan = new() { p };
            if (cursorFile != null && cursorFile != q.File) cursorOffset = 0;
        }
        else
        {
            if (!Directory.Exists(_logDir)) return new OpsLogsResult(Array.Empty<string>(), false, null);
            var all = Directory.GetFiles(_logDir, "*.jsonl").OrderBy(f => f).ToList();
            filesToScan = all;
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
            var (lines, fileTruncated, nextOffset, newBytes) = await ReadQueryFileAsync(filePath, startOffset, q, from, to, collected.Count, sw, bytes, ct);
            bytes = newBytes;
            foreach (var l in lines)
            {
                if (collected.Count >= q.Limit) break;
                collected.Add(l);
            }
            if (fileTruncated) { truncated = true; nextCursor = EncodeCursor(fileName, nextOffset); break; }
            if (collected.Count >= q.Limit)
            {
                var remaining = await HasMoreLinesAsync(filePath, nextOffset, q, from, to, ct);
                if (remaining) nextCursor = EncodeCursor(fileName, nextOffset);
                else
                {
                    var idx = filesToScan.IndexOf(filePath);
                    if (idx + 1 < filesToScan.Count)
                        nextCursor = EncodeCursor(Path.GetFileName(filesToScan[idx + 1]), 0);
                }
                break;
            }
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
        bool truncated = false;
        long nextOffset;
        // Use accurate byte tracking via raw file reading
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        if (startOffset < 0) startOffset = 0;
        if (startOffset > fs.Length) startOffset = fs.Length;
        fs.Seek(startOffset, SeekOrigin.Begin);

        long currentOffset = startOffset;
        nextOffset = startOffset;

        // Buffered raw reader state - accurate byte tracking
        var buffer = new byte[8192];
        int bufLen = 0, bufPos = 0;
        var lineBytes = new List<byte>(1024);
        long LastLineBytes = 0;

        async Task<string?> ReadLineWrapper()
        {
            lineBytes.Clear();
            long consumed = 0;
            bool nl = false;
            while (true)
            {
                if (bufPos >= bufLen)
                {
                    bufLen = await fs.ReadAsync(buffer, 0, buffer.Length, ct);
                    bufPos = 0;
                    if (bufLen == 0)
                    {
                        if (lineBytes.Count == 0) return null;
                        break;
                    }
                }
                byte b = buffer[bufPos++];
                consumed++;
                if (b == (byte)'\n')
                {
                    nl = true;
                    break;
                }
                lineBytes.Add(b);
            }
            if (nl && lineBytes.Count > 0 && lineBytes[lineBytes.Count - 1] == (byte)'\r')
                lineBytes.RemoveAt(lineBytes.Count - 1);
            LastLineBytes = consumed;
            return Encoding.UTF8.GetString(lineBytes.ToArray());
        }

        string? line;
        while ((line = await ReadLineWrapper()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > MaxDuration) { truncated = true; nextOffset = currentOffset; break; }

            long lineStartOffset = currentOffset;
            long rawBytes = LastLineBytes;
            // Advance offset for next iteration (will be used if we continue)
            // But we need to decide nextOffset based on whether line is matched/added or skipped
            bool matches = MatchesFilters(line, q.Level, q.Keyword, from, to);
            if (!matches)
            {
                currentOffset += rawBytes;
                nextOffset = currentOffset;
                continue;
            }

            var outBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + outBytes > MaxBytes) { truncated = true; nextOffset = lineStartOffset; break; }
            if (result.Count + alreadyCollected >= q.Limit) { truncated = false; nextOffset = lineStartOffset; break; }

            result.Add(line);
            bytes += outBytes;
            currentOffset += rawBytes;
            nextOffset = currentOffset;

            if (bytes >= MaxBytes) { truncated = true; break; }
            if (sw.Elapsed > MaxDuration) { truncated = true; break; }
        }

        return (result, truncated, nextOffset, bytes);
    }

    private async Task<bool> HasMoreLinesAsync(string path, long offset, OpsLogsQuery q, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        if (offset < 0) offset = 0;
        if (offset > fs.Length) return false;
        fs.Seek(offset, SeekOrigin.Begin);
        // Use same accurate reader for consistency
        var buffer = new byte[8192];
        int bufLen = 0, bufPos = 0;
        var lineBytes = new List<byte>(1024);
        async Task<string?> ReadNext()
        {
            lineBytes.Clear();
            bool nl = false;
            while (true)
            {
                if (bufPos >= bufLen)
                {
                    bufLen = await fs.ReadAsync(buffer, 0, buffer.Length, ct);
                    bufPos = 0;
                    if (bufLen == 0)
                    {
                        if (lineBytes.Count == 0) return null;
                        break;
                    }
                }
                byte b = buffer[bufPos++];
                if (b == (byte)'\n') { nl = true; break; }
                lineBytes.Add(b);
            }
            if (nl && lineBytes.Count > 0 && lineBytes[lineBytes.Count - 1] == (byte)'\r')
                lineBytes.RemoveAt(lineBytes.Count - 1);
            return Encoding.UTF8.GetString(lineBytes.ToArray());
        }

        string? line;
        while ((line = await ReadNext()) != null)
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
        var queue = new Queue<string>(lines);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string? line;
        bool timedOut = false;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > MaxDuration) { timedOut = true; break; }
            if (!MatchesFilters(line, level, keyword, null, null)) continue;
            if (queue.Count == lines) queue.Dequeue();
            queue.Enqueue(line);
        }

        var tail = queue.ToList();
        var output = new List<string>();
        bool truncated = timedOut;
        long outBytes = 0;
        foreach (var l in tail)
        {
            if (sw.Elapsed > MaxDuration) { truncated = true; break; }
            var lb = Encoding.UTF8.GetByteCount(l) + 1;
            if (outBytes + lb > MaxBytes) { truncated = true; break; }
            output.Add(l);
            outBytes += lb;
        }
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
