using Pim.Core.Common;

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
    IReadOnlyList<TodaySectionRegistryItemDto> Sections)
{
    /// <summary>该响应覆盖的业务日（yyyy-MM-dd）。客户端不得自行推算（契约语义 §1）。</summary>
    public string BusinessDate { get; init; } = string.Empty;

    /// <summary>业务日时区（IANA，恒为 Asia/Shanghai）。</summary>
    public string BusinessTimeZone { get; init; } = BusinessDateEnvelope.TimeZoneId;

    /// <summary>业务日起算小时（恒为 4）：业务日 D = [D 04:00, D+1 04:00)。</summary>
    public int BusinessDayStartHour { get; init; } = BusinessDateEnvelope.StartHour;
}

public sealed record TodaySectionDto(
    string Id,
    string Kind,
    string Status,
    DateTimeOffset GeneratedAt,
    object Data,
    IReadOnlyList<TodayLinkDto> Links,
    TodaySectionErrorDto? Error)
{
    /// <summary>该响应覆盖的业务日（yyyy-MM-dd）。客户端不得自行推算（契约语义 §1）。</summary>
    public string BusinessDate { get; init; } = string.Empty;

    /// <summary>业务日时区（IANA，恒为 Asia/Shanghai）。</summary>
    public string BusinessTimeZone { get; init; } = BusinessDateEnvelope.TimeZoneId;

    /// <summary>业务日起算小时（恒为 4）：业务日 D = [D 04:00, D+1 04:00)。</summary>
    public int BusinessDayStartHour { get; init; } = BusinessDateEnvelope.StartHour;
}

public interface ITodaySectionProvider
{
    string SectionId { get; }

    string Kind { get; }

    Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct);
}
