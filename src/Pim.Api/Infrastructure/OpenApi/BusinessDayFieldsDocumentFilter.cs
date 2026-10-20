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
/// 字段是**可选**的：客户端可能暂时拿不到运行时值，但必须按这里的口径理解业务日，不得自行推算。
/// 运行时下发的后续在 DTO 上补同名字段时，契约无需再改。
/// </para>
/// </summary>
public sealed class BusinessDayFieldsDocumentFilter : IDocumentFilter
{
    /// <summary>按日端点清单（与 docs/client-contract-semantics.md §1 保持一致，测试会校验两边一致）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["/api/v1/today/sections"] = "今日分区注册表",
        ["/api/v1/today/sections/{sectionId}"] = "单个今日分区",
        ["/api/v1/pc/summary"] = "PC 当日汇总",
        ["/api/v1/mobile/summary"] = "手机当日使用汇总",
    };

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        foreach (var (path, note) in Endpoints)
        {
            if (!document.Paths.TryGetValue(path, out var item) || item.Operations is null)
                continue;
            if (!item.Operations.TryGetValue(OperationType.Get, out var op))
                continue;
            if (op.Responses is null || !op.Responses.TryGetValue("200", out var ok) || ok?.Content is null)
                continue;
            foreach (var media in ok.Content.Values)
            {
                var payload = ResolvePayloadSchema(document, media.Schema);
                if (payload is null)
                    continue;
                payload.Properties ??= new Dictionary<string, OpenApiSchema>();
                AddIfMissing(payload, "businessDate", new OpenApiSchema
                {
                    Type = "string",
                    Format = "date",
                    Description = $"该响应覆盖的业务日（yyyy-MM-dd）。服务端权威口径：{note}。客户端不得自行推算。",
                });
                AddIfMissing(payload, "businessTimeZone", new OpenApiSchema
                {
                    Type = "string",
                    Description = $"业务日时区（IANA）：{Pim.Core.Common.BusinessDateEnvelope.TimeZoneId}",
                    Example = new OpenApiString(Pim.Core.Common.BusinessDateEnvelope.TimeZoneId),
                });
                AddIfMissing(payload, "businessDayStartHour", new OpenApiSchema
                {
                    Type = "integer",
                    Format = "int32",
                    Description = $"业务日起算小时（本地时间）：{Pim.Core.Common.BusinessDateEnvelope.StartHour}。"
                                  + "业务日 D = [D 该时刻, D+1 该时刻)。",
                    Example = new OpenApiInteger(Pim.Core.Common.BusinessDateEnvelope.StartHour),
                });
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

    private static void AddIfMissing(OpenApiSchema schema, string name, OpenApiSchema field)
    {
        if (!schema.Properties!.ContainsKey(name))
            schema.Properties[name] = field;
    }
}
