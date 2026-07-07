using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile.DTOs;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

public sealed class MobileTimelineBlockService
{
    private static readonly TimeSpan BlockMergeGap = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly TimeProvider _timeProvider;
    private readonly MobileAppClassificationService? _classificationService;

    public MobileTimelineBlockService(
        PimDbContext db,
        ICurrentUserService currentUser,
        TimeProvider timeProvider,
        MobileAppClassificationService? classificationService = null)
    {
        _db = db;
        _currentUser = currentUser;
        _timeProvider = timeProvider;
        _classificationService = classificationService;
    }

    public async Task<MobileTimelineBlockPageDto> GetBlocksAsync(
        MobileAnalyticsQueryRequest request,
        CancellationToken ct = default)
    {
        var context = Normalize(request);
        var blocks = await BuildBlocksAsync(context, ct);
        var ordered = ApplyCursor(
                blocks
                    .OrderByDescending(block => block.StartUtc)
                    .ThenBy(block => block.Id, StringComparer.Ordinal)
                    .ToList(),
                context.Cursor)
            .ToList();

        var pageRows = ordered.Take(context.PageSize + 1).ToList();
        var hasMore = pageRows.Count > context.PageSize;
        var pageItems = pageRows.Take(context.PageSize).ToList();
        var nextCursor = hasMore && pageItems.Count > 0
            ? EncodePayload(new PageCursor(pageItems[^1].StartUtc, pageItems[^1].Id))
            : null;

        return new MobileTimelineBlockPageDto(
            pageItems.Select(block => block.Dto).ToList(),
            nextCursor,
            hasMore);
    }

    public async Task<IReadOnlyList<MobileTimelineBlockSessionDto>> GetSessionsForBlockAsync(
        string blockId,
        MobileAnalyticsQueryRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blockId))
            return [];

        var context = Normalize(request);
        var block = (await BuildBlocksAsync(context, ct))
            .SingleOrDefault(candidate => string.Equals(candidate.Id, blockId, StringComparison.Ordinal));

        var items = block?.Items;
        if (items is null)
        {
            var payload = DecodePayload<BlockIdPayload>(blockId);
            if (payload is null || payload.ItemIds.Count == 0)
                return [];

            var payloadIds = payload.ItemIds.ToHashSet(StringComparer.Ordinal);
            items = (await BuildTimelineItemsAsync(context, ct))
                .Where(item => payloadIds.Contains(item.Id))
                .OrderBy(item => item.StartUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
        }

        return items
            .OrderBy(item => item.StartUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(ToSessionDto)
            .ToList();
    }

    public async Task<IReadOnlyList<MobileSessionEventDto>> GetSessionEventsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(sessionId, out var parsedSessionId))
            return [];

        var userId = MobileUserContext.RequireUserId(_currentUser);
        var session = await _db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.UserId == userId && candidate.Id == parsedSessionId, ct);
        if (session is null)
            return [];

        var endUtc = EffectiveSessionEnd(session);
        if (endUtc < session.StartUtc)
            endUtc = session.StartUtc;

        return await _db.Set<MobileUsageEventEntity>()
            .AsNoTracking()
            .Where(e => e.UserId == userId
                && e.DeviceId == session.DeviceId
                && e.PackageName == session.PackageName
                && e.EventTimestampUtc >= session.StartUtc
                && e.EventTimestampUtc <= endUtc)
            .OrderBy(e => e.EventTimestampUtc)
            .ThenBy(e => e.Id)
            .Select(e => new MobileSessionEventDto(
                e.Id.ToString("N"),
                session.Id.ToString("N"),
                e.DeviceId,
                e.PackageName,
                e.EventType,
                e.EventTimestampUtc,
                e.ClassName,
                e.RawJson))
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<ComputedBlock>> BuildBlocksAsync(
        MobileAnalyticsQueryContext context,
        CancellationToken ct)
    {
        var timeZoneInfo = new MobileAnalyticsQueryService(_timeProvider).ResolveTimezone(context.Range.Timezone);
        var items = (await BuildTimelineItemsAsync(context, ct))
            .OrderBy(item => item.StartUtc)
            .ThenBy(item => item.EndUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

        var builders = new List<BlockBuilder>();
        BlockBuilder? current = null;
        foreach (var item in items)
        {
            if (current is null || !current.CanAccept(item))
            {
                current = new BlockBuilder(item);
                builders.Add(current);
                continue;
            }

            current.Add(item);
        }

        return builders.Select(builder => builder.Build(timeZoneInfo)).ToList();
    }

    private async Task<IReadOnlyList<TimelineItem>> BuildTimelineItemsAsync(
        MobileAnalyticsQueryContext context,
        CancellationToken ct)
    {
        var userId = MobileUserContext.RequireUserId(_currentUser);
        var sessions = SourceMatches(context.Source, "events")
            ? await QuerySessions(userId, context).ToListAsync(ct)
            : [];
        var summaries = SourceMatches(context.Source, "fallback")
            ? await QueryFallbackSummaries(userId, context).ToListAsync(ct)
            : [];
        var packages = sessions
            .Select(session => session.PackageName)
            .Concat(summaries.Select(summary => summary.PackageName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var classifications = await LoadClassificationsAsync(userId, context.DeviceId, packages, ct);
        var items = new List<TimelineItem>(sessions.Count + summaries.Count);

        foreach (var session in sessions)
        {
            if (!classifications.TryGetValue(session.PackageName, out var classification))
                classification = AppClassification.Default(session.PackageName);
            if (!MatchesClassificationFilters(classification, context))
                continue;

            var startUtc = Max(session.StartUtc, context.Range.RangeStartUtc);
            var endUtc = Min(EffectiveSessionEnd(session), context.Range.RangeEndUtc);
            var durationSeconds = DurationSeconds(startUtc, endUtc);
            if (ShouldSkipDuration(durationSeconds, context.MinDurationSeconds))
                continue;

            items.Add(new TimelineItem(
                session.Id.ToString("N"),
                "session",
                session.DeviceId,
                session.PackageName,
                classification.DisplayName,
                startUtc,
                endUtc,
                durationSeconds,
                classification.LifeCategory,
                "events",
                1,
                QualityFlags(session.QualityFlagsJson),
                classification.IsSystemNoise));
        }

        foreach (var summary in summaries)
        {
            if (!classifications.TryGetValue(summary.PackageName, out var classification))
                classification = AppClassification.Default(summary.PackageName);
            if (!MatchesClassificationFilters(classification, context))
                continue;

            var startUtc = Max(summary.WindowStartUtc, context.Range.RangeStartUtc);
            var endUtc = Min(summary.WindowEndUtc, context.Range.RangeEndUtc);
            var durationSeconds = ProratedSeconds(
                summary.WindowStartUtc,
                summary.WindowEndUtc,
                startUtc,
                endUtc,
                summary.TotalTimeVisibleMs);
            if (ShouldSkipDuration(durationSeconds, context.MinDurationSeconds))
                continue;

            items.Add(new TimelineItem(
                summary.Id.ToString("N"),
                "fallback",
                summary.DeviceId,
                summary.PackageName,
                classification.DisplayName,
                startUtc,
                endUtc,
                durationSeconds,
                classification.LifeCategory,
                "fallback",
                0.6,
                QualityFlags(summary.QualityFlagsJson),
                classification.IsSystemNoise));
        }

        return items;
    }

    private IQueryable<MobileUsageSessionEntity> QuerySessions(Guid userId, MobileAnalyticsQueryContext context)
    {
        var query = _db.Set<MobileUsageSessionEntity>()
            .AsNoTracking()
            .Where(s => s.UserId == userId
                && s.StartUtc < context.Range.RangeEndUtc
                && (s.EndUtc == null || s.EndUtc > context.Range.RangeStartUtc));

        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            query = query.Where(s => s.DeviceId == context.DeviceId);
        if (!string.IsNullOrWhiteSpace(context.PackageName))
            query = query.Where(s => s.PackageName == context.PackageName);

        return query;
    }

    private IQueryable<MobileUsageSummaryEntity> QueryFallbackSummaries(
        Guid userId,
        MobileAnalyticsQueryContext context)
    {
        var query = MobileUsageQueryService
            .WhereFallbackSummaries(_db.Set<MobileUsageSummaryEntity>().AsNoTracking())
            .Where(s => s.UserId == userId
                && s.WindowStartUtc < context.Range.RangeEndUtc
                && s.WindowEndUtc > context.Range.RangeStartUtc);

        if (!string.IsNullOrWhiteSpace(context.DeviceId))
            query = query.Where(s => s.DeviceId == context.DeviceId);
        if (!string.IsNullOrWhiteSpace(context.PackageName))
            query = query.Where(s => s.PackageName == context.PackageName);

        return query;
    }

    private async Task<IReadOnlyDictionary<string, AppClassification>> LoadClassificationsAsync(
        Guid userId,
        string? deviceId,
        IReadOnlyCollection<string> packageNames,
        CancellationToken ct)
    {
        if (packageNames.Count == 0)
            return new Dictionary<string, AppClassification>(StringComparer.OrdinalIgnoreCase);

        var catalogQuery = _db.Set<MobileAppCatalogEntity>()
            .AsNoTracking()
            .Where(app => app.UserId == userId && packageNames.Contains(app.PackageName));
        if (!string.IsNullOrWhiteSpace(deviceId))
            catalogQuery = catalogQuery.Where(app => app.DeviceId == deviceId);

        var catalog = (await catalogQuery.ToListAsync(ct))
            .GroupBy(app => app.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(app => app.UpdatedAt)
                    .ThenBy(app => app.Id)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
        if (_classificationService is not null)
            return await LoadClassificationsFromServiceAsync(packageNames, catalog, ct);

        var overrides = (await _db.Set<MobileAppCatalogOverrideEntity>()
                .AsNoTracking()
                .Where(app => app.UserId == userId && packageNames.Contains(app.PackageName))
                .ToListAsync(ct))
            .ToDictionary(app => app.PackageName, StringComparer.OrdinalIgnoreCase);
        var rules = await _db.Set<MobileAppCategoryRuleEntity>()
            .AsNoTracking()
            .Where(rule => rule.UserId == userId && rule.IsEnabled)
            .OrderBy(rule => rule.Priority)
            .ThenByDescending(rule => rule.UpdatedAt)
            .ThenBy(rule => rule.Id)
            .ToListAsync(ct);

        return packageNames.ToDictionary(
            packageName => packageName,
            packageName =>
            {
                overrides.TryGetValue(packageName, out var appOverride);
                catalog.TryGetValue(packageName, out var app);
                var displayCandidate = appOverride?.DisplayNameOverride ?? app?.DisplayName ?? packageName;
                var rule = rules.FirstOrDefault(candidate => RuleMatches(candidate, packageName, displayCandidate, app?.Category));
                var displayName = FirstNonBlank(appOverride?.DisplayNameOverride, rule?.DisplayNameOverride, app?.DisplayName, packageName);
                var lifeCategory = FirstNonBlank(appOverride?.LifeCategory, rule?.LifeCategory, app?.Category, MobileLifeCategories.Uncategorized);
                var isSystemNoise = appOverride is not null
                    ? appOverride.IsSystemNoise
                    : rule?.IsSystemNoise ?? app?.IsSystemApp == true || IsSystemPackage(packageName, lifeCategory);

                return new AppClassification(displayName, lifeCategory, isSystemNoise);
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, AppClassification>> LoadClassificationsFromServiceAsync(
        IReadOnlyCollection<string> packageNames,
        IReadOnlyDictionary<string, MobileAppCatalogEntity> catalog,
        CancellationToken ct)
    {
        var results = new Dictionary<string, AppClassification>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageName in packageNames)
        {
            catalog.TryGetValue(packageName, out var app);
            var result = await _classificationService!.ClassifyAsync(new MobileAppClassificationInput(
                packageName,
                app?.DisplayName,
                app?.Category,
                app?.InstallerPackage,
                app?.IsSystemApp), ct);
            results[packageName] = new AppClassification(
                result.DisplayName,
                result.LifeCategory,
                result.IsSystemNoise);
        }

        return results;
    }

    private static bool MatchesClassificationFilters(
        AppClassification classification,
        MobileAnalyticsQueryContext context)
    {
        if (!context.IncludeSystemNoise && classification.IsSystemNoise)
            return false;

        return string.IsNullOrWhiteSpace(context.LifeCategory)
            || string.Equals(classification.LifeCategory, context.LifeCategory, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ComputedBlock> ApplyCursor(
        IReadOnlyList<ComputedBlock> blocks,
        string? cursor)
    {
        var parsed = DecodePayload<PageCursor>(cursor);
        if (parsed is null)
            return blocks;

        return blocks.Where(block => block.StartUtc < parsed.StartUtc
            || (block.StartUtc == parsed.StartUtc && string.CompareOrdinal(block.Id, parsed.Id) > 0));
    }

    private MobileAnalyticsQueryContext Normalize(MobileAnalyticsQueryRequest request)
        => new MobileAnalyticsQueryService(_timeProvider).Normalize(request);

    private DateTimeOffset EffectiveSessionEnd(MobileUsageSessionEntity session)
    {
        if (session.EndUtc is not null)
            return session.EndUtc.Value;

        if (session.DurationMs is > 0)
            return session.StartUtc.AddMilliseconds(session.DurationMs.Value);

        return _timeProvider.GetUtcNow();
    }

    private static MobileTimelineBlockSessionDto ToSessionDto(TimelineItem item)
        => new(
            item.Id,
            item.DeviceId,
            item.PackageName,
            item.DisplayName,
            item.StartUtc,
            item.EndUtc,
            item.DurationSeconds,
            item.LifeCategory,
            item.Source,
            item.Confidence,
            item.QualityFlags);

    private static IReadOnlyList<string> QualityFlags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) is { } flags
                ? flags
                    .Where(flag => !string.IsNullOrWhiteSpace(flag))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string EncodePayload<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static T? DecodePayload<T>(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (FormatException)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool SourceMatches(string? requestedSource, string itemSource)
    {
        if (string.IsNullOrWhiteSpace(requestedSource))
            return true;
        if (string.Equals(requestedSource, itemSource, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(itemSource, "fallback", StringComparison.OrdinalIgnoreCase)
            && (requestedSource.Contains("summary", StringComparison.OrdinalIgnoreCase)
                || requestedSource.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RuleMatches(
        MobileAppCategoryRuleEntity rule,
        string packageName,
        string displayName,
        string? category)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern))
            return false;

        return rule.RuleType switch
        {
            "package-prefix" => packageName.StartsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            "package-contains" => packageName.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            "display-name-contains" => displayName.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
            "category-exact" => string.Equals(category, rule.Pattern, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(packageName, rule.Pattern, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool IsSystemPackage(string packageName, string lifeCategory)
        => string.Equals(lifeCategory, MobileLifeCategories.ToolsSystem, StringComparison.OrdinalIgnoreCase)
            && (packageName.StartsWith("android", StringComparison.OrdinalIgnoreCase)
                || packageName.StartsWith("com.android.", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSkipDuration(long durationSeconds, int minDurationSeconds)
        => durationSeconds <= 0 || (minDurationSeconds > 0 && durationSeconds <= minDurationSeconds);

    private static long DurationSeconds(DateTimeOffset startUtc, DateTimeOffset endUtc)
        => Math.Max(0, Convert.ToInt64(Math.Floor((endUtc - startUtc).TotalSeconds)));

    private static long ProratedSeconds(
        DateTimeOffset sourceStartUtc,
        DateTimeOffset sourceEndUtc,
        DateTimeOffset overlapStartUtc,
        DateTimeOffset overlapEndUtc,
        long totalVisibleMs)
    {
        if (totalVisibleMs <= 0 || overlapEndUtc <= overlapStartUtc)
            return 0;

        var sourceMs = (sourceEndUtc - sourceStartUtc).TotalMilliseconds;
        var overlapMs = (overlapEndUtc - overlapStartUtc).TotalMilliseconds;
        if (sourceMs <= 0)
            return Math.Max(0, totalVisibleMs / 1000);

        var ratio = Math.Clamp(overlapMs / sourceMs, 0, 1);
        return Math.Max(0, Convert.ToInt64(Math.Floor(totalVisibleMs * ratio / 1000)));
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;

    private static string FirstNonBlank(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private sealed record TimelineItem(
        string Id,
        string Kind,
        string DeviceId,
        string PackageName,
        string DisplayName,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        long DurationSeconds,
        string LifeCategory,
        string Source,
        double Confidence,
        IReadOnlyList<string> QualityFlags,
        bool IsSystemNoise);

    private sealed record AppClassification(
        string DisplayName,
        string LifeCategory,
        bool IsSystemNoise)
    {
        public static AppClassification Default(string packageName)
            => new(packageName, MobileLifeCategories.Uncategorized, false);
    }

    private sealed record ComputedBlock(
        string Id,
        DateTimeOffset StartUtc,
        MobileTimelineBlockDto Dto,
        IReadOnlyList<TimelineItem> Items);

    private sealed record PageCursor(DateTimeOffset StartUtc, string Id);

    private sealed record BlockIdPayload(
        int Version,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string LifeCategory,
        IReadOnlyList<string> ItemIds);

    private sealed class BlockBuilder
    {
        private readonly List<TimelineItem> _items = [];

        public BlockBuilder(TimelineItem item)
        {
            _items.Add(item);
            StartUtc = item.StartUtc;
            EndUtc = item.EndUtc;
            LifeCategory = item.LifeCategory;
        }

        private DateTimeOffset StartUtc { get; }

        private DateTimeOffset EndUtc { get; set; }

        private string LifeCategory { get; }

        public bool CanAccept(TimelineItem item)
            => string.Equals(LifeCategory, item.LifeCategory, StringComparison.OrdinalIgnoreCase)
                && item.StartUtc <= EndUtc.Add(BlockMergeGap);

        public void Add(TimelineItem item)
        {
            _items.Add(item);
            if (item.EndUtc > EndUtc)
                EndUtc = item.EndUtc;
        }

        public ComputedBlock Build(TimeZoneInfo timeZoneInfo)
        {
            var itemIds = _items
                .OrderBy(item => item.StartUtc)
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.Id)
                .ToList();
            var lifeCategory = _items
                .GroupBy(item => item.LifeCategory)
                .OrderByDescending(group => group.Sum(item => item.DurationSeconds))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .First()
                .Key;
            var id = EncodePayload(new BlockIdPayload(1, StartUtc, EndUtc, lifeCategory, itemIds));
            var topApps = _items
                .GroupBy(item => new { item.PackageName, item.DisplayName })
                .Select(group => new MobileTimelineBlockAppDto(
                    group.Key.PackageName,
                    group.Key.DisplayName,
                    group.Sum(item => item.DurationSeconds)))
                .OrderByDescending(app => app.ForegroundSeconds)
                .ThenBy(app => app.PackageName, StringComparer.Ordinal)
                .Take(5)
                .ToList();
            var sourceMix = _items
                .GroupBy(item => item.Source)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.DurationSeconds), StringComparer.Ordinal);
            var qualityFlags = _items
                .SelectMany(item => item.QualityFlags)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(flag => flag, StringComparer.Ordinal)
                .ToList();
            var dto = new MobileTimelineBlockDto(
                id,
                StartUtc,
                EndUtc,
                FormatLocal(StartUtc, timeZoneInfo),
                FormatLocal(EndUtc, timeZoneInfo),
                lifeCategory,
                _items.Sum(item => item.DurationSeconds),
                _items.Count,
                _items.Select(item => item.PackageName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                topApps,
                qualityFlags,
                sourceMix,
                _items.Any(item => item.IsSystemNoise));

            return new ComputedBlock(id, StartUtc, dto, _items.ToList());
        }

        private static string FormatLocal(DateTimeOffset value, TimeZoneInfo timeZoneInfo)
            => TimeZoneInfo.ConvertTime(value, timeZoneInfo)
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
