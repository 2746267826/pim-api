using System.Globalization;
using Microsoft.Extensions.Logging;
using Pim.Api.Endpoints;
using Pim.Core.Today;

namespace Pim.Api.Today;

public sealed class TodaySectionService
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly ILogger<TodaySectionService> _logger;
    private readonly IReadOnlyList<ITodaySectionProvider> _providers;

    public TodaySectionService(
        IEnumerable<ITodaySectionProvider> providers,
        ILogger<TodaySectionService> logger)
    {
        _logger = logger;
        _providers = providers
            .GroupBy(provider => provider.SectionId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(provider => provider.SectionId, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<TodaySectionRegistryDto> GetRegistryAsync(string? date, CancellationToken ct)
    {
        var query = BuildQuery(date);
        var formattedDate = FormatDate(query.Date);
        var sections = _providers
            .Select(provider => new TodaySectionRegistryItemDto(
                provider.SectionId,
                provider.Kind,
                TodaySectionStatuses.Available,
                [
                    new TodayLinkDto(
                        TodayLinkRels.Self,
                        $"{TodayEndpointPaths.Section(provider.SectionId)}?date={formattedDate}")
                ]))
            .ToArray();

        return Task.FromResult(new TodaySectionRegistryDto(
            formattedDate,
            FormatDate(query.PcBusinessDate),
            DateTimeOffset.UtcNow,
            sections));
    }

    public async Task<TodaySectionDto?> GetSectionAsync(string sectionId, string? date, CancellationToken ct)
    {
        var provider = _providers.FirstOrDefault(candidate => string.Equals(
            candidate.SectionId,
            sectionId,
            StringComparison.Ordinal));
        if (provider is null)
        {
            return null;
        }

        var query = BuildQuery(date);
        try
        {
            return await provider.BuildAsync(query, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Today section provider {SectionId} failed.", provider.SectionId);

            return new TodaySectionDto(
                provider.SectionId,
                provider.Kind,
                TodaySectionStatuses.Unavailable,
                DateTimeOffset.UtcNow,
                new { },
                [],
                new TodaySectionErrorDto(
                    "section_unavailable",
                    "此今日模块暂时不可用。"));
        }
    }

    private static TodayQuery BuildQuery(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return BuildQuery(DateTime.Now, hasExplicitTime: true);
        }

        if (DateOnly.TryParseExact(
            date,
            DateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dateOnly))
        {
            return new TodayQuery(dateOnly, dateOnly);
        }

        if (DateTime.TryParse(
            date,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var dateTime))
        {
            return BuildQuery(dateTime, HasExplicitTime(date));
        }

        throw new FormatException("今日日期无效。请使用 YYYY-MM-DD 或可解析的日期时间值。");
    }

    private static TodayQuery BuildQuery(DateTime dateTime, bool hasExplicitTime)
    {
        var todayDate = DateOnly.FromDateTime(dateTime.Date);
        var pcBusinessDate = hasExplicitTime && dateTime.Hour < 4
            ? todayDate.AddDays(-1)
            : todayDate;

        return new TodayQuery(todayDate, pcBusinessDate);
    }

    private static bool HasExplicitTime(string date)
        => date.Contains(':', StringComparison.Ordinal)
            || date.Contains(" AM", StringComparison.OrdinalIgnoreCase)
            || date.Contains(" PM", StringComparison.OrdinalIgnoreCase);

    private static string FormatDate(DateOnly date)
        => date.ToString(DateFormat, CultureInfo.InvariantCulture);
}
