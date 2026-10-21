using System.Globalization;
using Microsoft.Extensions.Logging;
using Pim.Api.Endpoints;
using Pim.Core.Common;
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
            sections)
        {
            BusinessDate = formattedDate,
            // registry 的 businessDate 是日历日（与 date 字段一致）；pcBusinessDate 才是 04:00 切日。
            // 明确写 0，避免客户端按「恒为 4」推出一个不存在的窗口（review P2-3）。
            BusinessDayStartHour = 0,
        });
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
        // pc.* 分区取的是 PcBusinessDate（04:00 切日）的数据，信封上的业务日必须与载荷一致；
        // 其余分区用 query.Date（日历日）。否则在 00:00–04:00 会出现信封 businessDate 与 data.businessDate 打架。
        // startHour 必须跟着业务日一起给：日历日配 4 会让客户端推出一个与数据窗口错开 4 小时的区间。
        var usesPcBusinessDate = UsesPcBusinessDate(provider.SectionId);
        var businessDate = FormatDate(usesPcBusinessDate ? query.PcBusinessDate : query.Date);
        var startHour = usesPcBusinessDate ? BusinessDay.StartHour : 0;
        try
        {
            // 业务日由服务端权威给出：provider 只负责载荷，口径由这里统一贴上去。
            return (await provider.BuildAsync(query, ct)) with
            {
                BusinessDate = businessDate,
                BusinessDayStartHour = startHour,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Today section provider {SectionId} failed.", provider.SectionId);

            // 响应只暴露脱敏后的异常类型（不含异常消息，避免泄漏内部细节），
            // 让用户/排查者能区分故障类别；完整原因仍在日志中（issue #313）。
            return new TodaySectionDto(
                provider.SectionId,
                provider.Kind,
                TodaySectionStatuses.Unavailable,
                DateTimeOffset.UtcNow,
                new { },
                [],
                new TodaySectionErrorDto(
                    "section_unavailable",
                    $"此今日模块暂时不可用。（异常类型：{ex.GetType().Name}）"))
            {
                BusinessDate = businessDate,
                BusinessDayStartHour = startHour,
            };
        }
    }

    /// <summary>
    /// 恰好是这两个 provider 用 <see cref="TodayQuery.PcBusinessDate"/>（04:00 切日）取数，
    /// 信封上的 <c>businessDate</c> 必须跟着载荷走；其余分区（包括 pc.classification_suggestions，
    /// 它不按日取数）用 <see cref="TodayQuery.Date"/>。
    /// </summary>
    private static bool UsesPcBusinessDate(string sectionId)
        => sectionId is "pc.activity" or "pc.quality";

    /// <summary>
    /// 当前墙钟（Asia/Shanghai）。**不能**用 <c>DateTime.Now</c>：宿主时区是 UTC 时
    /// （容器默认未设 TZ），16:00–24:00 UTC 期间算出来的日期会比上海早一天。
    /// </summary>
    private static DateTime NowInBusinessTimeZone()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, BusinessDay.TimeZone).DateTime;

    private static TodayQuery BuildQuery(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return BuildQuery(NowInBusinessTimeZone(), hasExplicitTime: true);
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
