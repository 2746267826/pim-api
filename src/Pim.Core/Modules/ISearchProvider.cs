namespace Pim.Core.Modules;

public interface ISearchProvider
{
    string Domain { get; }
    Task<SearchProviderResponse> SearchAsync(SearchProviderQuery query, CancellationToken ct);
}

public sealed record SearchProviderQuery(
    string RawQuery,
    IReadOnlyList<string> Terms,
    SearchMatchMode Match);

public enum SearchMatchMode
{
    All,
    Any
}

public enum SearchSort
{
    Relevance,
    RecentEdited,
    RecentCreated
}

public sealed record SearchProviderResponse(
    string Domain,
    int Total,
    IReadOnlyList<SearchResult> Items);

public record SearchResult(
    string ModuleName,
    string Type,
    string Id,
    string Title,
    string Snippet,
    string Url,
    string Domain = "",
    double Score = 0,
    IReadOnlyList<string>? MatchedFields = null,
    IReadOnlyList<SearchFieldLocation>? FieldLocations = null,
    SearchEventOccurrence? EventOccurrence = null,
    string SourceState = "server",
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? CreatedAt = null,
    string? SortKey = null
);

public sealed record SearchFieldLocation(
    string Field,
    string Label,
    int? Index = null
);

public sealed record SearchEventOccurrence(
    Guid EventId,
    string? RecurrenceId,
    Guid? SeriesMasterId,
    DateTimeOffset StartsAt
);

public sealed record SearchGroup(
    string Domain,
    string Label,
    int TotalCount,
    IReadOnlyList<SearchResult> Items,
    SearchGroupFailure? Failure = null
);

public sealed record SearchGroupFailure(
    string Code,
    string Message,
    string CorrelationId,
    bool Retryable
);

public sealed record GlobalSearchResult(
    IReadOnlyList<SearchResult> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    string Query,
    string Scope,
    string Match,
    string Sort,
    IReadOnlyList<SearchGroup> Groups,
    DateTimeOffset GeneratedAt
);
