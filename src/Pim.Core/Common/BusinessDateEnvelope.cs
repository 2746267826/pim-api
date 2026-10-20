namespace Pim.Core.Common;

/// <summary>
/// 按日端点的业务日声明（契约语义 <c>docs/client-contract-semantics.md</c> §1）。
/// <para>
/// 客户端**不得**自行推算业务日：凡是按日聚合/查询的响应，都带上服务端认定的业务日与口径。
/// 本类型只在序列化层使用（见 <c>BusinessDateEnvelopeOperationFilter</c>），不改变各端点的 DTO 形状 ——
/// 客户端可以忽略这三个字段；但契约会声明它们，新的原生客户端必须使用它们。
/// </para>
/// </summary>
public static class BusinessDateEnvelope
{
    /// <summary>IANA 时区 ID（与 <see cref="BusinessDay"/> 同源）。</summary>
    public static string TimeZoneId { get; } = ResolveIanaId();

    /// <summary>业务日起算小时。</summary>
    public const int StartHour = BusinessDay.StartHour;

    public static string Format(DateOnly date) => BusinessDay.FormatDate(date);

    private static string ResolveIanaId()
    {
        // BusinessDay.TimeZone 在 Windows 上可能解析成 "China Standard Time"；对客户端一律对外给出 IANA ID。
        var id = BusinessDay.TimeZone.Id;
        return id == "Asia/Shanghai" ? id : "Asia/Shanghai";
    }
}
