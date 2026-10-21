namespace Pim.Core.Common;

/// <summary>
/// 按日端点的业务日口径（契约语义 <c>docs/client-contract-semantics.md</c> §1）。
///
/// <para>
/// 客户端**不得**自行推算业务日：按日聚合/查询的响应会在载荷上带
/// <c>businessDate</c> / <c>businessTimeZone</c> / <c>businessDayStartHour</c> 三个字段
/// （由各 DTO 上的同名属性下发，见 <c>TodaySectionRegistryDto</c>、<c>TodaySectionDto</c>、
/// <c>PcSummaryResponse</c>、<c>MobileUsageSummaryResponse</c>）。
/// </para>
/// <para>
/// 这里只放**口径常量**，与 <see cref="BusinessDay"/> 同源：时区 Asia/Shanghai、业务日起算 04:00，
/// 即业务日 D = [D 04:00, D+1 04:00)。
/// </para>
/// </summary>
public static class BusinessDateEnvelope
{
    /// <summary>对外给出的 IANA 时区 ID（Windows 上 <see cref="BusinessDay.TimeZone"/> 的 Id 可能是 "China Standard Time"）。</summary>
    public const string TimeZoneId = "Asia/Shanghai";

    /// <summary>业务日起算小时。</summary>
    public const int StartHour = BusinessDay.StartHour;

    public static string Format(DateOnly date) => BusinessDay.FormatDate(date);
}
