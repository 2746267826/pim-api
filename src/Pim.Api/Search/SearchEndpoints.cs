using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Infrastructure;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Modules;

namespace Pim.Api.Search;

public static class SearchEndpoints
{
    private const int MaxQueryLength = 200;
    private const int MaxTerms = 10;
    private const int MaxPageSize = 100;
    private static readonly string[] AllDomains = ["note", "task", "event"];
    private static readonly IReadOnlyDictionary<string, string> DomainLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["note"] = "笔记",
            ["task"] = "任务",
            ["event"] = "日程"
        };

    public static void MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/search")
            .RequireAuthorization();

        group.MapGet("/", async (
            string? q,
            string? type,
            int? limit,
            string? scope,
            string? match,
            string? sort,
            int? page,
            int? pageSize,
            IServiceScopeFactory scopeFactory,
            HttpContext http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Pim.Api.Search");
            var requestedPage = ParsePage(page);
            var requestedPageSize = ParsePageSize(pageSize, limit);
            var requestedMatch = ParseMatch(match);
            var requestedSort = ParseSort(sort);
            var domains = ResolveDomains(scope, type,
                allowLegacyFile: string.IsNullOrWhiteSpace(scope) && !string.IsNullOrWhiteSpace(type));
            var trimmed = q?.Trim() ?? string.Empty;
            if (trimmed.Length > MaxQueryLength)
                throw new DomainException(40001, $"搜索关键词最多 {MaxQueryLength} 个字符");

            var terms = SplitTerms(trimmed);
            if (terms.Count > MaxTerms)
                throw new DomainException(40001, $"搜索关键词最多 {MaxTerms} 个词");

            var generatedAt = DateTimeOffset.UtcNow;
            if (terms.Count == 0)
            {
                return Results.Ok(ApiResponse<GlobalSearchResult>.Ok(new GlobalSearchResult(
                    Array.Empty<SearchResult>(),
                    requestedPage,
                    requestedPageSize,
                    0,
                    0,
                    string.Empty,
                    FormatDomains(domains),
                    FormatMatch(requestedMatch),
                    FormatSort(requestedSort),
                    domains.Select(domain => new SearchGroup(
                        domain, DomainLabels[domain], 0, Array.Empty<SearchResult>())).ToArray(),
                    generatedAt)));
            }

            var correlationId = http.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
                ?? http.TraceIdentifier;
            var providerQuery = new SearchProviderQuery(
                trimmed,
                terms,
                requestedMatch);

            var runs = await Task.WhenAll(domains.Select(domain =>
                RunDomainAsync(domain, scopeFactory, providerQuery,
                    correlationId, logger, ct)));

            // The endpoint owns the global order and page boundary. Providers must return
            // their complete match set; truncating per domain would drop later pages.
            var allItems = runs
                .SelectMany(run => run.Response?.Items ?? Array.Empty<SearchResult>())
                .ToList();
            var ordered = SortItems(allItems, requestedSort);
            var totalCount = runs.Sum(run => run.Response?.Total ?? 0);
            var totalPages = totalCount == 0
                ? 0
                : (int)Math.Ceiling(totalCount / (double)requestedPageSize);
            var offset = ((long)requestedPage - 1) * requestedPageSize;
            var pageItems = offset >= ordered.Count
                ? new List<SearchResult>()
                : ordered.Skip((int)offset).Take(requestedPageSize).ToList();
            var groups = runs.Select(run => new SearchGroup(
                run.Domain,
                DomainLabels[run.Domain],
                run.Response?.Total ?? 0,
                pageItems.Where(item => string.Equals(item.Domain, run.Domain, StringComparison.Ordinal)).ToArray(),
                run.Failure)).ToArray();

            return Results.Ok(ApiResponse<GlobalSearchResult>.Ok(new GlobalSearchResult(
                pageItems,
                requestedPage,
                requestedPageSize,
                totalCount,
                totalPages,
                trimmed,
                FormatDomains(domains),
                FormatMatch(requestedMatch),
                FormatSort(requestedSort),
                groups,
                generatedAt)));
        })
        .Produces<ApiResponse<GlobalSearchResult>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);
    }

    private static async Task<DomainRun> RunDomainAsync(
        string domain,
        IServiceScopeFactory scopeFactory,
        SearchProviderQuery query,
        string correlationId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            // Each domain gets its own DbContext/current-user scope. A single request-scoped
            // context must not be used by concurrent providers (EF Core rejects overlapping work).
            await using var scope = scopeFactory.CreateAsyncScope();
            var providers = scope.ServiceProvider.GetServices<ISearchProvider>()
                .Where(provider => string.Equals(provider.Domain, domain,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (providers.Length == 0)
                return new DomainRun(domain,
                    new SearchProviderResponse(domain, 0, Array.Empty<SearchResult>()), null);

            var responses = new List<SearchProviderResponse>(providers.Length);
            foreach (var provider in providers)
                responses.Add(await provider.SearchAsync(query, ct));
            var items = responses
                .SelectMany(response => response.Items)
                .Select(item => item with
                {
                    Domain = domain,
                    SourceState = string.IsNullOrWhiteSpace(item.SourceState)
                        ? "server"
                        : item.SourceState
                })
                .ToArray();
            return new DomainRun(domain,
                new SearchProviderResponse(domain, responses.Sum(response => response.Total), items), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DomainException error)
        {
            logger.LogWarning(error,
                "Search domain {Domain} failed with domain error {ErrorCode}; correlation {CorrelationId}",
                domain, error.ErrorCode, correlationId);
            return new DomainRun(domain, null, new SearchGroupFailure(
                error.ErrorCode.ToString(CultureInfo.InvariantCulture), error.Message, correlationId,
                error.ErrorCode >= 50000));
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "Search domain {Domain} failed; correlation {CorrelationId}", domain, correlationId);
            return new DomainRun(domain, null, new SearchGroupFailure(
                "50001", "该领域搜索暂时不可用，请稍后重试", correlationId, true));
        }
    }

    private static List<SearchResult> SortItems(
        IReadOnlyList<SearchResult> items, SearchSort sort)
    {
        IOrderedEnumerable<SearchResult> ordered = sort switch
        {
            SearchSort.RecentEdited => items
                .OrderByDescending(item => item.UpdatedAt ?? item.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Domain, StringComparer.Ordinal),
            SearchSort.RecentCreated => items
                .OrderByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Domain, StringComparer.Ordinal),
            _ => items
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Domain, StringComparer.Ordinal)
        };

        return ordered
            .ThenBy(item => item.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.EventOccurrence?.RecurrenceId ?? string.Empty, StringComparer.Ordinal)
            .Select(item => item with { SortKey = BuildSortKey(item, sort) })
            .ToList();
    }

    private static string BuildSortKey(SearchResult item, SearchSort sort)
    {
        var primary = sort switch
        {
            SearchSort.RecentEdited => string.Join('|',
                (item.UpdatedAt ?? DateTimeOffset.MinValue).ToString("O", CultureInfo.InvariantCulture),
                (item.CreatedAt ?? DateTimeOffset.MinValue).ToString("O", CultureInfo.InvariantCulture)),
            SearchSort.RecentCreated => string.Join('|',
                (item.CreatedAt ?? DateTimeOffset.MinValue).ToString("O", CultureInfo.InvariantCulture),
                (item.UpdatedAt ?? DateTimeOffset.MinValue).ToString("O", CultureInfo.InvariantCulture)),
            _ => string.Join('|',
                item.Score.ToString("R", CultureInfo.InvariantCulture),
                (item.UpdatedAt ?? item.CreatedAt ?? DateTimeOffset.MinValue)
                    .ToString("O", CultureInfo.InvariantCulture),
                (item.CreatedAt ?? DateTimeOffset.MinValue)
                    .ToString("O", CultureInfo.InvariantCulture))
        };
        return string.Join('|', primary, item.Domain, item.Type, item.Id,
            item.EventOccurrence?.RecurrenceId ?? string.Empty);
    }

    private static int ParsePage(int? page)
    {
        var value = page ?? 1;
        if (value < 1) throw new DomainException(40001, "page 必须大于等于 1");
        return value;
    }

    private static int ParsePageSize(int? pageSize, int? legacyLimit)
    {
        if (pageSize is { } requested)
        {
            if (requested is < 1 or > MaxPageSize)
                throw new DomainException(40001, $"pageSize/limit 必须在 1 到 {MaxPageSize} 之间");
            return requested;
        }
        if (legacyLimit is { } legacy)
        {
            if (legacy < 1)
                throw new DomainException(40001, $"pageSize/limit 必须在 1 到 {MaxPageSize} 之间");
            // The legacy endpoint capped `limit` at 100 instead of rejecting it.
            return Math.Min(legacy, MaxPageSize);
        }
        return 20;
    }

    private static SearchMatchMode ParseMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            return SearchMatchMode.All;
        if (string.Equals(value.Trim(), "any", StringComparison.OrdinalIgnoreCase))
            return SearchMatchMode.Any;
        throw new DomainException(40001, "match 只能是 all 或 any");
    }

    private static SearchSort ParseSort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "relevance", StringComparison.OrdinalIgnoreCase))
            return SearchSort.Relevance;
        if (string.Equals(value.Trim(), "recent_edited", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), "recent", StringComparison.OrdinalIgnoreCase))
            return SearchSort.RecentEdited;
        if (string.Equals(value.Trim(), "recent_created", StringComparison.OrdinalIgnoreCase))
            return SearchSort.RecentCreated;
        throw new DomainException(40001, "sort 只能是 relevance、recent_edited 或 recent_created");
    }

    private static IReadOnlyList<string> ResolveDomains(string? scope, string? legacyType,
        bool allowLegacyFile)
    {
        var raw = string.IsNullOrWhiteSpace(scope) ? legacyType : scope;
        if (string.IsNullOrWhiteSpace(raw)) return AllDomains;

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var ignoredLegacyFile = false;
        var selectAll = false;
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "all":
                    selectAll = true;
                    break;
                case "note":
                case "notes":
                    selected.Add("note");
                    break;
                case "task":
                case "tasks":
                    selected.Add("task");
                    break;
                case "event":
                case "events":
                    selected.Add("event");
                    break;
                case "calendar":
                    selected.Add("task");
                    selected.Add("event");
                    break;
                case "file":
                case "files":
                    if (!allowLegacyFile)
                    {
                        throw new DomainException(40001,
                            "scope/type 只能是 all、note、task、event 或 calendar");
                    }
                    // Files keep their independent /api/v1/files/search endpoint. The former
                    // global endpoint accepted `type=file` but had no provider for it; keep that
                    // legacy call successful (and empty) instead of turning it into a 400.
                    ignoredLegacyFile = true;
                    break;
                default:
                    throw new DomainException(40001,
                        "scope/type 只能是 all、note、task、event 或 calendar");
            }
        }

        if (selectAll) return AllDomains;
        if (selected.Count == 0 && !ignoredLegacyFile)
            throw new DomainException(40001,
                "scope/type 只能是 all、note、task、event 或 calendar");
        return AllDomains.Where(selected.Contains).ToArray();
    }

    private static IReadOnlyList<string> SplitTerms(string query)
        => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string FormatDomains(IReadOnlyList<string> domains)
        => domains.Count == AllDomains.Length ? "all" : string.Join(',', domains);

    private static string FormatMatch(SearchMatchMode match)
        => match == SearchMatchMode.Any ? "any" : "all";

    private static string FormatSort(SearchSort sort) => sort switch
    {
        SearchSort.RecentEdited => "recent_edited",
        SearchSort.RecentCreated => "recent_created",
        _ => "relevance"
    };

    private sealed record DomainRun(
        string Domain,
        SearchProviderResponse? Response,
        SearchGroupFailure? Failure);
}
