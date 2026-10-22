using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// 契约语义 §1（docs/client-contract-semantics.md）：按日端点必须在响应里声明业务日口径。
///
/// <para>
/// 用 <see cref="IDocumentFilter"/> 而不是 OperationFilter：文档过滤器在所有 schema 都生成完之后才执行，
/// 此时可以安全地按 <c>$ref</c> 找到信封 schema，并顺着 <c>data</c> 字段找到真正的载荷类型。
/// （OperationFilter 阶段组件 schema 可能尚未登记，写进去的字段会落到一个并不存在的定义上。）
/// </para>
/// <para>
/// 字段由运行时真实下发（<c>TodaySectionRegistryDto</c> / <c>TodaySectionDto</c> /
/// <c>PcSummaryResponse</c> / <c>MobileUsageSummaryResponse</c> 上同名属性）；这里的过滤器只负责
/// 「契约里一定有」这件事 —— 因此端点/schema 对不上时**直接抛异常**让 swagger 生成失败，
/// 而不是静默跳过（静默跳过等于给客户端一个悄悄消失的字段）。
/// </para>
/// </summary>
public sealed class BusinessDayFieldsDocumentFilter : IDocumentFilter
{
    /// <summary>
    /// 按日端点清单（与 docs/client-contract-semantics.md §1 保持一致）。
    /// StartHour 为 null 表示「按分区而定」，description 不写死 4（review P3-1）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Note, int? StartHour, string? BusinessDateDescription)> Endpoints =
        new Dictionary<string, (string Note, int? StartHour, string? BusinessDateDescription)>(StringComparer.OrdinalIgnoreCase)
        {
            ["/api/v1/today/sections"] = ("今日分区注册表（日历日）", 0, null),
            ["/api/v1/today/sections/{sectionId}"] =
                ("单个今日分区（pc.activity / pc.quality 为 4，其余为 0）", null, null),
            ["/api/v1/pc/summary"] = ("PC 当日汇总", Pim.Core.Common.BusinessDateEnvelope.StartHour, null),
            // 手机汇总在**不传日期/范围**时不过滤时间（返回历史全量），businessDate 只是「今天」的标签。
            // 写成「该响应覆盖的业务日」会让客户端把全量数据当成今天的数据（review P3-3）。
            ["/api/v1/mobile/summary"] = ("手机使用汇总", Pim.Core.Common.BusinessDateEnvelope.StartHour,
                "业务日标签（yyyy-MM-dd）：传 date 时取其所在业务日；传 rangeStart 时取起点所在业务日；"
                + "都未传时为当前业务日。注意：未传日期或范围时汇总不做时间过滤，该字段不表示数据只覆盖这个窗口。"),
        };

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        foreach (var (path, (note, startHour, businessDateDescription)) in Endpoints)
        {
            if (!document.Paths.TryGetValue(path, out var item) || item.Operations is null)
                throw new InvalidOperationException($"业务日契约：契约里找不到路径 {path}（是否改名/删除？）");
            if (!item.Operations.TryGetValue(OperationType.Get, out var op))
                throw new InvalidOperationException($"业务日契约：{path} 不再有 GET 操作");
            if (op.Responses is null || !op.Responses.TryGetValue("200", out var ok) || ok?.Content is null)
                throw new InvalidOperationException($"业务日契约：{path} 的 200 响应没有 content");
            foreach (var media in ok.Content.Values)
            {
                var payload = ResolvePayloadSchema(document, media.Schema)
                    ?? throw new InvalidOperationException($"业务日契约：{path} 的 200 响应 schema 无法解析");
                payload.Properties ??= new Dictionary<string, OpenApiSchema>();
                Upsert(payload, "businessDate", "string", "date",
                    businessDateDescription
                    ?? $"该响应覆盖的业务日（yyyy-MM-dd）。服务端权威口径：{note}。客户端不得自行推算。",
                    example: null);
                Upsert(payload, "businessTimeZone", "string", null,
                    $"业务日时区（IANA）：{Pim.Core.Common.BusinessDateEnvelope.TimeZoneId}",
                    example: new OpenApiString(Pim.Core.Common.BusinessDateEnvelope.TimeZoneId));
                var startHourDescription = startHour is int fixedHour
                    ? $"业务日起算小时（本地时间）：{fixedHour}。业务日 D = [D {fixedHour:00}:00, D+1 {fixedHour:00}:00)。"
                    : "业务日起算小时（本地时间）：0 或 4，按分区而定（pc.activity / pc.quality 为 4，其余为 0）。"
                      + "业务日 D = [D 该时刻, D+1 该时刻)。";
                Upsert(payload, "businessDayStartHour", "integer", "int32",
                    startHourDescription,
                    example: startHour is int hour ? new OpenApiInteger(hour) : null);
                payload.Extensions["x-business-day"] = new OpenApiBoolean(true);
                op.Extensions["x-business-day"] = new OpenApiBoolean(true);
            }
        }
    }

    /// <summary>
    /// 顺 <c>$ref</c> 找到最终载荷 schema：信封（<c>*ApiResponse</c>）有 <c>data</c> 字段，就继续往下取一层。
    /// 只看一层信封，避免把 <c>data</c> 内部嵌套对象也全加上业务日字段。
    /// </summary>
    private static OpenApiSchema? ResolvePayloadSchema(OpenApiDocument document, OpenApiSchema? schema)
    {
        var current = Deref(document, schema);
        if (current is null)
            return null;
        if (current.Properties is not null
            && current.Properties.TryGetValue("data", out var data)
            && Deref(document, data) is { } inner
            && inner.Properties is not null)
            return inner;
        return current;
    }

    private static OpenApiSchema? Deref(OpenApiDocument document, OpenApiSchema? schema)
    {
        var guard = 0;
        while (schema?.Reference is not null && guard++ < 4)
        {
            var id = schema.Reference.Id;
            if (!document.Components.Schemas.TryGetValue(id, out var target))
                return null;
            schema = target;
        }
        return schema;
    }

    /// <summary>
    /// 属性来自运行时 DTO（类型已由 Swashbuckle 生成），这里只**补齐语义**：描述、格式、示例。
    /// 属性不存在时才新建 —— 否则会把 serializer 推导出的类型信息覆盖掉。
    /// </summary>
    private static void Upsert(
        OpenApiSchema schema, string name, string type, string? format, string description, IOpenApiAny? example)
    {
        if (!schema.Properties!.TryGetValue(name, out var field))
        {
            field = new OpenApiSchema { Type = type };
            schema.Properties[name] = field;
        }
        field.Type ??= type;
        field.Format ??= format;
        field.Description ??= description;
        if (field.Example is null && example is not null)
            field.Example = example;
    }
}
