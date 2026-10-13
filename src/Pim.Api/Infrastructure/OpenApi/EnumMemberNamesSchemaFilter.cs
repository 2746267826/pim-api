using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// issue #417 第二节第 3 条：整数枚举只有取值、没有成员名（<c>PimHealthStatus</c> 只有
/// <c>[0,1,2,3]</c>），客户端生成器拿不到「0 是什么」——<c>pim-android</c> 的生成器
/// 因此把整数枚举降级成 <c>typealias X = Int</c>，只能写出魔法数字。
///
/// 这里补 <c>x-enum-varnames</c>（openapi-generator 的既有约定：与 <c>enum</c> 取值
/// 一一对应的成员名数组）。**不改 <c>type</c>/<c>enum</c>**：线上仍是整数，
/// <c>JsonStringEnumConverter</c> 之外的枚举序列化行为一个字都没变，
/// 这是纯文档元数据。
///
/// 取值与成员名对不上时（例如 <c>[Flags]</c> 的组合值、或契约里出现无对应成员的数）
/// 一律不写这个扩展 —— 宁可不给名字，也不编造名字。
/// </summary>
public sealed class EnumMemberNamesSchemaFilter : ISchemaFilter
{
    /// <summary>与 <c>enum</c> 取值同序的成员名数组。</summary>
    private const string ExtensionName = "x-enum-varnames";

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        if (!type.IsEnum)
            return;
        if (schema.Enum is null || schema.Enum.Count == 0)
            return;
        if (schema.Extensions.ContainsKey(ExtensionName))
            return;

        var names = new OpenApiArray();
        foreach (var value in schema.Enum)
        {
            // 字符串枚举（JsonStringEnumConverter）本身已经带名字，不重复标注。
            if (value is not OpenApiInteger number)
                return;

            var name = Enum.GetName(type, number.Value);
            if (name is null)
                return;

            names.Add(new OpenApiString(name));
        }

        schema.Extensions[ExtensionName] = names;
    }
}
