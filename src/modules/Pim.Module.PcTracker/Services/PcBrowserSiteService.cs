using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

/// <summary>
/// 站点级浏览器数据（Time Tracker fork 通道）：实时上传、查询与历史自助导入。
/// daily 表按 (deviceId, date, host) 聚合，查询层跨设备汇总；tick 为按日时段。
/// </summary>
public sealed class PcBrowserSiteService
{
    private const int MaxSiteEventsPerUpload = 5000;
    private const int MaxHostLength = 253;
    private const long MaxSpanMs = 24 * 60 * 60 * 1000L;
    private const string DefaultImportDeviceId = "__imported__";
    private const int MaxImportContentLength = 32 * 1024 * 1024;

    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "focus", "tick", "visit", "run", "media",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PimDbContext _db;

    public PcBrowserSiteService(PimDbContext db)
    {
        _db = db;
    }

    // === 实时上传 ===

    public async Task<int> UploadAsync(SiteEventsUploadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceId))
            throw new ArgumentException("DeviceId is required.", nameof(req));
        if (req.Events.Count > MaxSiteEventsPerUpload)
            throw new ArgumentException($"Site uploads are limited to {MaxSiteEventsPerUpload} events.", nameof(req));

        var deltas = new Dictionary<(string Device, string Date, string Host), SiteDailyDelta>();
        var ticks = new List<PcBrowserSiteTickEntity>();

        foreach (var e in req.Events)
        {
            var kind = e.Kind?.Trim().ToLowerInvariant()
                ?? throw new ArgumentException("Event kind is required.", nameof(req));
            if (!AllowedKinds.Contains(kind))
                throw new ArgumentException($"Invalid site event kind '{e.Kind}'.", nameof(req));

            var host = NormalizeHost(e.Host, nameof(req));
            var date = ParseDate(e.Date, nameof(req));

            switch (kind)
            {
                case "focus":
                {
                    if (e.StartMs is not long start || e.EndMs is not long end)
                        throw new ArgumentException("focus event requires startMs and endMs.", nameof(req));
                    if (end < start) throw new ArgumentException("focus event endMs must be >= startMs.", nameof(req));
                    if (end - start > MaxSpanMs) throw new ArgumentException("focus event span exceeds 24h.", nameof(req));
                    AddDelta(deltas, req.DeviceId, date, host, focusMs: end - start);
                    break;
                }
                case "visit":
                    AddDelta(deltas, req.DeviceId, date, host, visitCount: 1);
                    break;

                case "run":
                case "media":
                {
                    if (e.DurationMs is not long duration || duration <= 0 || duration > MaxSpanMs)
                        throw new ArgumentException($"{kind} event requires 0 < durationMs <= 24h.", nameof(req));
                    if (kind == "run") AddDelta(deltas, req.DeviceId, date, host, runMs: duration);
                    else AddDelta(deltas, req.DeviceId, date, host, mediaMs: duration);
                    break;
                }

                case "tick":
                {
                    if (e.StartMs is not long tickStart)
                        throw new ArgumentException("tick event requires startMs.", nameof(req));
                    if (e.DurationMs is not long tickDuration || tickDuration <= 0 || tickDuration > MaxSpanMs)
                        throw new ArgumentException("tick event requires 0 < durationMs <= 24h.", nameof(req));
                    ticks.Add(new PcBrowserSiteTickEntity
                    {
                        DeviceId = req.DeviceId,
                        Host = host,
                        Date = date,
                        StartUtc = DateTimeOffset.FromUnixTimeMilliseconds(tickStart),
                        DurationMs = tickDuration,
                    });
                    break;
                }
            }
        }

        var touchedKeys = deltas.Keys.ToList();
        var existing = await _db.Set<PcBrowserSiteDailyEntity>()
            .Where(r => r.DeviceId == req.DeviceId)
            .ToListAsync(ct);
        var existingMap = existing
            .Where(r => deltas.ContainsKey((r.DeviceId, r.Date, r.Host)))
            .ToDictionary(r => (r.DeviceId, r.Date, r.Host));

        var now = DateTimeOffset.UtcNow;
        foreach (var (key, delta) in deltas)
        {
            if (existingMap.TryGetValue(key, out var row))
            {
                row.FocusMs += delta.FocusMs;
                row.VisitCount += delta.VisitCount;
                row.RunMs += delta.RunMs;
                row.MediaMs += delta.MediaMs;
                row.UpdatedAt = now;
            }
            else
            {
                _db.Set<PcBrowserSiteDailyEntity>().Add(new PcBrowserSiteDailyEntity
                {
                    DeviceId = key.Device,
                    Date = key.Date,
                    Host = key.Host,
                    FocusMs = delta.FocusMs,
                    VisitCount = delta.VisitCount,
                    RunMs = delta.RunMs,
                    MediaMs = delta.MediaMs,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        _db.Set<PcBrowserSiteTickEntity>().AddRange(ticks);
        await _db.SaveChangesAsync(ct);
        _ = touchedKeys;
        return req.Events.Count;
    }

    // === 查询 ===

    public async Task<List<SiteDailyRowDto>> GetDailyAsync(SiteDailyQuery query, CancellationToken ct)
    {
        var (from, to) = ResolveRange(query);
        var rows = await _db.Set<PcBrowserSiteDailyEntity>()
            .Where(r => r.Date.CompareTo(from) >= 0 && r.Date.CompareTo(to) <= 0)
            .ToListAsync(ct);

        return rows
            .GroupBy(r => (r.Date, r.Host))
            .Select(g => new SiteDailyRowDto
            {
                Date = g.Key.Date,
                Host = g.Key.Host,
                FocusMs = g.Sum(r => r.FocusMs),
                VisitCount = g.Sum(r => r.VisitCount),
                RunMs = g.Sum(r => r.RunMs),
                MediaMs = g.Sum(r => r.MediaMs),
            })
            .OrderBy(r => r.Date)
            .ThenByDescending(r => r.FocusMs)
            .ToList();
    }

    public async Task<List<SiteTimelineRowDto>> GetTimelineAsync(string date, CancellationToken ct)
    {
        var normalized = ParseDate(date, nameof(date));
        var ticks = await _db.Set<PcBrowserSiteTickEntity>()
            .Where(t => t.Date == normalized)
            .OrderBy(t => t.StartUtc)
            .ToListAsync(ct);

        return ticks
            .Select(t => new SiteTimelineRowDto
            {
                Host = t.Host,
                StartMs = t.StartUtc.ToUnixTimeMilliseconds(),
                DurationMs = t.DurationMs,
            })
            .ToList();
    }

    public async Task<SiteSummaryDto> GetSummaryAsync(string? from, string? to, CancellationToken ct)
    {
        (from, to) = ResolveRange(new SiteDailyQuery { From = from, To = to, Date = from is null && to is null ? DateTimeOffset.Now.ToString("yyyy-MM-dd") : null });
        var rows = await _db.Set<PcBrowserSiteDailyEntity>()
            .Where(r => r.Date.CompareTo(from) >= 0 && r.Date.CompareTo(to) <= 0)
            .ToListAsync(ct);
        var metas = await _db.Set<PcBrowserSiteMetaEntity>().ToListAsync(ct);
        var aliasMap = metas
            .Where(m => !string.IsNullOrWhiteSpace(m.Alias))
            .GroupBy(m => m.Host)
            .ToDictionary(g => g.Key, g => g.First().Alias);

        var byHost = rows
            .GroupBy(r => r.Host)
            .Select(g => new SiteTopHostDto
            {
                Host = g.Key,
                Alias = aliasMap.TryGetValue(g.Key, out var alias) ? alias : null,
                FocusMs = g.Sum(r => r.FocusMs),
                VisitCount = g.Sum(r => r.VisitCount),
            })
            .OrderByDescending(h => h.FocusMs)
            .ToList();

        return new SiteSummaryDto
        {
            From = from,
            To = to,
            TotalFocusMs = byHost.Sum(h => h.FocusMs),
            TotalVisits = byHost.Sum(h => h.VisitCount),
            TotalRunMs = rows.Sum(r => r.RunMs),
            TotalMediaMs = rows.Sum(r => r.MediaMs),
            SiteCount = byHost.Count,
            TopHosts = byHost.Take(10).ToList(),
        };
    }

    public async Task<List<SiteMetaDto>> GetMetasAsync(IReadOnlyCollection<string> hosts, CancellationToken ct)
    {
        if (hosts.Count == 0) return new List<SiteMetaDto>();
        var hostList = hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().Take(500).ToList();
        var metas = await _db.Set<PcBrowserSiteMetaEntity>()
            .Where(m => hostList.Contains(m.Host))
            .ToListAsync(ct);
        return metas.Select(m => new SiteMetaDto
        {
            Host = m.Host,
            Alias = m.Alias,
            IconUrl = m.IconUrl,
            Cate = m.Cate,
        }).ToList();
    }

    // === 历史自助导入 ===

    public async Task<SiteImportResultDto> ImportAsync(SiteImportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Content))
            throw new ArgumentException("Import content is empty.", nameof(req));
        if (req.Content.Length > MaxImportContentLength)
            throw new ArgumentException("Import content is too large.", nameof(req));

        var (format, rows) = ParseImportContent(req.Content);
        var deviceId = string.IsNullOrWhiteSpace(req.DeviceId) ? DefaultImportDeviceId : req.DeviceId.Trim();
        var addMode = string.Equals(req.Mode, "add", StringComparison.OrdinalIgnoreCase);
        if (!addMode && !string.Equals(req.Mode, "overwrite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Import mode must be 'overwrite' or 'add'.", nameof(req));

        var keys = rows.Select(r => (Device: deviceId, r.Date, r.Host)).Distinct().ToList();
        var existingRows = await _db.Set<PcBrowserSiteDailyEntity>()
            .Where(r => r.DeviceId == deviceId)
            .ToListAsync(ct);
        var existingMap = existingRows
            .Where(r => keys.Contains((r.DeviceId, r.Date, r.Host)))
            .ToDictionary(r => (r.DeviceId, r.Date, r.Host));

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            var key = (Device: deviceId, row.Date, row.Host);
            if (existingMap.TryGetValue(key, out var entity))
            {
                if (addMode)
                {
                    entity.FocusMs += row.FocusMs;
                    entity.VisitCount += row.VisitCount;
                    entity.RunMs += row.RunMs;
                    entity.MediaMs += row.MediaMs;
                }
                else
                {
                    entity.FocusMs = row.FocusMs;
                    entity.VisitCount = row.VisitCount;
                    entity.RunMs = row.RunMs;
                    entity.MediaMs = row.MediaMs;
                }
                entity.UpdatedAt = now;
            }
            else
            {
                var created = new PcBrowserSiteDailyEntity
                {
                    DeviceId = deviceId,
                    Date = row.Date,
                    Host = row.Host,
                    FocusMs = row.FocusMs,
                    VisitCount = row.VisitCount,
                    RunMs = row.RunMs,
                    MediaMs = row.MediaMs,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.Set<PcBrowserSiteDailyEntity>().Add(created);
                existingMap[key] = created;
            }

            if (!string.IsNullOrWhiteSpace(row.Alias) || !string.IsNullOrWhiteSpace(row.Cate))
            {
                await UpsertMetaAsync(row.Host, row.Alias, row.Cate, ct);
            }
        }

        await _db.SaveChangesAsync(ct);

        return new SiteImportResultDto
        {
            Rows = rows.Count,
            Dates = rows.Select(r => r.Date).Distinct().Count(),
            Hosts = rows.Select(r => r.Host).Distinct().Count(),
            Format = format,
        };
    }

    private async Task UpsertMetaAsync(string host, string? alias, string? cate, CancellationToken ct)
    {
        var meta = await _db.Set<PcBrowserSiteMetaEntity>().FirstOrDefaultAsync(m => m.Host == host, ct);
        if (meta is null)
        {
            _db.Set<PcBrowserSiteMetaEntity>().Add(new PcBrowserSiteMetaEntity
            {
                Host = host,
                Alias = string.IsNullOrWhiteSpace(alias) ? null : alias,
                Cate = string.IsNullOrWhiteSpace(cate) ? null : cate,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(alias)) meta.Alias = alias;
            if (!string.IsNullOrWhiteSpace(cate)) meta.Cate = cate;
            meta.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    // === 解析 ===

    internal sealed record ImportedRow(string Host, string Date, long FocusMs, long VisitCount, long RunMs, long MediaMs, string? Alias, string? Cate);

    internal static (string Format, List<ImportedRow> Rows) ParseImportContent(string content)
    {
        // 1) tt4b 备份 markdown：`<!-- {...} -->` 内嵌 JSON（meta 行 + 数据行）
        if (content.Contains("<!--"))
        {
            var rows = TryParseBackupMarkdown(content);
            if (rows is not null) return ("backup-markdown", rows);
        }

        // 2) 记录页导出 JSON 数组
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var rows = ParseRecordExportArray(doc.RootElement);
                if (rows is { Count: > 0 }) return ("record-json", rows);
            }
        }
        catch (JsonException)
        {
            // fall through to error
        }

        throw new ArgumentException("Unsupported import content: expected tt4b backup markdown or record export JSON array.", nameof(content));
    }

    private static List<ImportedRow>? TryParseBackupMarkdown(string content)
    {
        var candidates = new List<string>();
        var index = 0;
        while ((index = content.IndexOf("<!--", index, StringComparison.Ordinal)) >= 0)
        {
            var end = content.IndexOf("-->", index, StringComparison.Ordinal);
            if (end < 0) break;
            candidates.Add(content[(index + 4)..end].Trim());
            index = end + 3;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                var parsed = ParseBackupRows(doc.RootElement);
                if (parsed is { Count: > 0 }) return parsed;
            }
            catch (JsonException)
            {
                // try next comment block
            }
        }
        return null;
    }

    private static List<ImportedRow>? ParseBackupRows(JsonElement array)
    {
        var rows = new List<ImportedRow>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var host = NormalizeHost(item.TryGetProperty("host", out var h) ? h.GetString() : null, null);
            var date = item.TryGetProperty("date", out var d) ? d.GetString() : null;
            if (host is null || date is null) continue;
            if (!TryParseDate(date, out var normalizedDate)) return null;

            long focus = item.TryGetProperty("focus", out var f) && f.TryGetInt64(out var fv) ? fv : 0;
            long visit = item.TryGetProperty("time", out var t) && t.TryGetInt64(out var tv) ? tv : 0;
            long run = item.TryGetProperty("run", out var r) && r.TryGetInt64(out var rv) ? rv : 0;
            long media = item.TryGetProperty("media", out var m) && m.TryGetInt64(out var mv) ? mv : 0;
            rows.Add(new ImportedRow(host, normalizedDate, focus, visit, run, media, null, null));
        }
        return rows.Count > 0 ? rows : null;
    }

    private static List<ImportedRow> ParseRecordExportArray(JsonElement array)
    {
        var rows = new List<ImportedRow>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var host = NormalizeHost(item.TryGetProperty("host", out var h) ? h.GetString() : null, null);
            var date = item.TryGetProperty("date", out var d) ? d.GetString() : null;
            if (host is null || date is null || !TryParseDate(date, out var normalizedDate))
                continue;

            var focusMs = item.TryGetProperty("focus", out var f) ? ParseFocusValueToMs(f) : 0;
            long visit = item.TryGetProperty("time", out var t) && t.TryGetInt64(out var tv) ? tv : 0;
            var alias = item.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var cate = item.TryGetProperty("cate", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (focusMs < 0) focusMs = 0;
            if (visit < 0) visit = 0;
            rows.Add(new ImportedRow(host, normalizedDate, focusMs, visit, 0, 0, alias, cate));
        }
        return rows;
    }

    /// <summary>
    /// focus 字段兼容三种取值：数字（毫秒，备份格式）、纯数字字符串（秒，
    /// 记录页导出 format:'second'）、"H:MM:SS"/"MM:SS"（通用时段格式）。
    /// </summary>
    internal static long ParseFocusValueToMs(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt64(out var ms) ? ms : 0;
            case JsonValueKind.String:
                var s = value.GetString()?.Trim() ?? string.Empty;
                if (s.Length == 0) return 0;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                    return TimeSpan.FromSeconds(seconds).TotalMilliseconds is double d ? (long)d : 0;
                var parts = s.Split(':');
                if (parts.Length is < 2 or > 3) return 0;
                long total = 0;
                foreach (var part in parts)
                {
                    if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        return 0;
                    total = total * 60 + n;
                }
                return total * 1000;
            default:
                return 0;
        }
    }

    // === 工具 ===

    private sealed record SiteDailyDelta
    {
        public long FocusMs { get; set; }
        public long VisitCount { get; set; }
        public long RunMs { get; set; }
        public long MediaMs { get; set; }
    }

    private static void AddDelta(
        Dictionary<(string Device, string Date, string Host), SiteDailyDelta> deltas,
        string device, string date, string host,
        long focusMs = 0, long visitCount = 0, long runMs = 0, long mediaMs = 0)
    {
        var key = (device, date, host);
        if (!deltas.TryGetValue(key, out var delta))
        {
            delta = new SiteDailyDelta();
            deltas[key] = delta;
        }
        delta.FocusMs += focusMs;
        delta.VisitCount += visitCount;
        delta.RunMs += runMs;
        delta.MediaMs += mediaMs;
    }

    private static string NormalizeHost(string? host, string? paramName)
    {
        var normalized = host?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("Host is required.", paramName ?? nameof(NormalizeHost));
        if (normalized.Length > MaxHostLength)
            throw new ArgumentException($"Host exceeds {MaxHostLength} characters.", paramName ?? nameof(NormalizeHost));
        return normalized;
    }

    private static string ParseDate(string? date, string paramName)
    {
        if (!TryParseDate(date, out var normalized))
            throw new ArgumentException($"Invalid date '{date}'. Expected YYYY-MM-DD.", paramName);
        return normalized;
    }

    private static bool TryParseDate(string? date, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(date)) return false;
        if (!DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        normalized = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static (string From, string To) ResolveRange(SiteDailyQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Date))
        {
            var date = ParseDate(query.Date, nameof(query));
            return (date, date);
        }
        if (string.IsNullOrWhiteSpace(query.From) || string.IsNullOrWhiteSpace(query.To))
            throw new ArgumentException("Either date or from+to is required.");
        var from = ParseDate(query.From, nameof(query));
        var to = ParseDate(query.To, nameof(query));
        if (string.CompareOrdinal(from, to) > 0)
            throw new ArgumentException("from must be <= to.");
        return (from, to);
    }
}
