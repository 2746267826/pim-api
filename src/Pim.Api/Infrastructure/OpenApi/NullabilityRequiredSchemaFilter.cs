using System.Reflection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// 契约批次 2：按 C# 可空性为请求/响应 DTO 的非空属性补 required[]。
/// Swashbuckle 6.9 的 <c>SupportNonNullableReferenceTypes</c> 对 record 位置参数属性不生效，
/// 这里用 NullabilityInfoContext 自行推导。规则（全部来自代码事实，无臆造）：
/// - 非空引用类型属性 → required；
/// - 非空值类型属性且构造参数无默认值 → required（可空值类型/有默认值的参数视为可选）；
/// - 可空（string? / Guid?）与 oblivious（Unknown）一律不标；
/// - <b>readOnly 的属性一律不标</b>：只读（服务端产出）与必填（调用方必须给）语义矛盾，
///   典型是 C# record 里的表达式体计算属性（<c>public int ApiLevel =&gt; SdkInt;</c>）。
/// 仅影响 OpenAPI 文档元数据，不参与运行时绑定/校验。
/// </summary>
public sealed class NullabilityRequiredSchemaFilter : ISchemaFilter
{
    private static readonly NullabilityInfoContext Nullability = new();
    // Only these real request/response properties accept null when a task runs locally. OpenAPI
    // 3.0 cannot serialize "nullable" on a $ref sibling, so use allOf($ref) + nullable on the
    // property and remove it from required. The shared consent component must stay non-null.
    private static readonly HashSet<(Type Type, string Property)> NullableProperties = new()
    {
        (typeof(Pim.Core.Notes.StartNoteAsrRequest),
            nameof(Pim.Core.Notes.StartNoteAsrRequest.Consent)),
        (typeof(Pim.Core.Notes.RetryNoteAsrRequest),
            nameof(Pim.Core.Notes.RetryNoteAsrRequest.Consent)),
        (typeof(Pim.Core.Notes.NoteAsrTaskDto),
            nameof(Pim.Core.Notes.NoteAsrTaskDto.Destination)),
        (typeof(Pim.Core.Notes.ReprocessNoteAsrRequest),
            nameof(Pim.Core.Notes.ReprocessNoteAsrRequest.Consent))
    };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var type = context.Type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            return;
        ApplyNullableWrappers(schema, type, context.SchemaRepository);
        // 注意：不能整类排除泛型（issue #417）。信封 ApiResponse&lt;T&gt; / PagedResult&lt;T&gt;
        // 是泛型，早先被这里跳过，导致 194 个 *ApiResponse 信封一个 required 都没有；
        // 数组/字典/List&lt;T&gt; 的 schema 没有 properties，下面的判断已经能把它们排除掉。
        if (schema.Properties is null || schema.Properties.Count == 0)
            return;

        // record 位置参数带默认值（如 string? Kind = null）时该参数可省略；按构造函数参数判断。
        var optionalNames = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Where(p => p.HasDefaultValue)
            .Select(p => p.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var camel = ToCamelCase(prop.Name);
            if (!schema.Properties.TryGetValue(camel, out var propertySchema))
                continue;
            var nullability = Nullability.Create(prop);

            // readOnly 属性的 required 只对响应方向成立，而本项目同时用同一份 DTO 生成
            // 请求与响应契约，标 required 会让客户端以为必须传这个「服务端算出来的」字段。
            // 判据取两处并集：序列化器已经标的 readOnly，以及「没有公开 setter」这个事实。
            if (propertySchema.ReadOnly || prop.SetMethod is null || !prop.SetMethod.IsPublic)
                continue;

            if (prop.PropertyType.IsValueType)
            {
                if (Nullable.GetUnderlyingType(prop.PropertyType) is not null)
                    continue;                       // Guid? / int? → 可选
                if (optionalNames.Contains(prop.Name))
                    continue;                       // 有默认值 → 可选
            }
            else
            {
                if (nullability.WriteState != NullabilityState.NotNull)
                    continue;                       // 可空或无法断言 → 不标
                if (optionalNames.Contains(prop.Name))
                    continue;
            }

            schema.Required ??= new HashSet<string>();
            if (!schema.Required.Contains(camel))
                schema.Required.Add(camel);
        }
    }

    private static void ApplyNullableWrappers(OpenApiSchema schema, Type type, SchemaRepository schemas)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!NullableProperties.Contains((type, prop.Name)) ||
                Nullability.Create(prop).WriteState == NullabilityState.NotNull ||
                schema.Properties is null ||
                !schema.Properties.TryGetValue(ToCamelCase(prop.Name), out var property) ||
                property.Reference is not { } reference)
                continue;
            if (!schemas.Schemas.TryGetValue(reference.Id, out var component))
                throw new InvalidOperationException(
                    $"Nullable ASR property '{type.Name}.{prop.Name}' references missing '{reference.Id}'.");

            Wrap(property, reference.Id, true);
            schema.Required?.Remove(ToCamelCase(prop.Name));
        }
    }

    internal static void Wrap(OpenApiSchema property, string targetId, bool nullable)
    {
        property.Reference = null;
        property.Type = "object";
        property.Format = null;
        property.Properties = null;
        property.Required = null;
        property.AdditionalProperties = null;
        property.AdditionalPropertiesAllowed = true;
        property.Items = null;
        property.Enum = null;
        property.AllOf = [new OpenApiSchema
        {
            Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = targetId }
        }];
        property.Nullable = nullable;
    }

    private static string ToCamelCase(string name)
        => char.ToLowerInvariant(name[0]) + name[1..];
}
