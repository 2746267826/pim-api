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
/// - 可空（string? / Guid?）与 oblivious（Unknown）一律不标。
/// 仅影响 OpenAPI 文档元数据，不参与运行时绑定/校验。
/// </summary>
public sealed class NullabilityRequiredSchemaFilter : ISchemaFilter
{
    private static readonly NullabilityInfoContext Nullability = new();

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var type = context.Type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type.IsGenericType)
            return;
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
            if (!schema.Properties.ContainsKey(camel))
                continue;

            var nullability = Nullability.Create(prop);
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

    private static string ToCamelCase(string name)
        => char.ToLowerInvariant(name[0]) + name[1..];
}
