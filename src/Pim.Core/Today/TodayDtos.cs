namespace Pim.Core.Today;

public static class TodaySectionStatuses
{
    public const string Available = "available";
    public const string Normal = "normal";
    public const string Empty = "empty";
    public const string Warning = "warning";
    public const string Critical = "critical";
    public const string Unavailable = "unavailable";
}

public static class TodayLinkRels
{
    public const string Self = "self";
    public const string Details = "details";
    public const string Api = "api";
}

public sealed record TodayQuery(DateOnly Date, DateOnly PcBusinessDate);

public sealed record TodayLinkDto(string Rel, string Href);

public sealed record TodaySectionErrorDto(string Code, string Message);

public sealed record TodaySectionRegistryItemDto(
    string Id,
    string Kind,
    string Status,
    IReadOnlyList<TodayLinkDto> Links);

public sealed record TodaySectionRegistryDto(
    string Date,
    string PcBusinessDate,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<TodaySectionRegistryItemDto> Sections);

public sealed record TodaySectionDto(
    string Id,
    string Kind,
    string Status,
    DateTimeOffset GeneratedAt,
    object Data,
    IReadOnlyList<TodayLinkDto> Links,
    TodaySectionErrorDto? Error);

public interface ITodaySectionProvider
{
    string SectionId { get; }

    string Kind { get; }

    Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct);
}
