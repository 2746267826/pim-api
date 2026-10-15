using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// 契约批次 2：为「服务端严格校验取值集合」的请求体字符串字段补 OpenAPI enum。
/// 表中每一条都必须对应真实的服务端校验代码（见行内注释）；不在表中的字段一律不标注，
/// 避免臆造约束。仅影响 OpenAPI 文档元数据，不参与运行时绑定/校验。
/// </summary>
public sealed class StrictValueSetsSchemaFilter : ISchemaFilter
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        // AdminUserService.ChangeRoleAsync：newRole != "admin" && != "user" → 40041（无效的角色）
        ["UpdateUserRoleRequest.Role"] = ["admin", "user"],

        // AdminUserService.CreateUserAsync：role 只接受 "admin" / "user"，其余 → 40041；
        // 缺省或为空时按 "user" 处理（决策记录 §3.2）
        ["CreateUserRequest.Role"] = ["admin", "user"],

        // QuickNoteService.ValidateStatus → QuickNoteStatuses.IsValid（无效 → 4003），
        // 调用点覆盖 update/archive/process/restore 三类入口
        ["RestoreQuickNoteRequest.Status"] = ["inbox", "processed", "archived"],
        ["UpdateQuickNoteRequest.Status"] = ["inbox", "processed", "archived"],

        // DataCenterGovernanceService：batch Action 仅接受 "archive"（其余 → 02049，大小写不敏感）
        ["DataCenterBatchOperationRequest.Action"] = ["archive"],
    };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties is null || schema.Properties.Count == 0)
            return;

        foreach (var (key, values) in Map)
        {
            var dot = key.LastIndexOf('.');
            var typeName = key[..dot];
            if (!string.Equals(context.Type.Name, typeName, StringComparison.Ordinal))
                continue;

            var propName = key[(dot + 1)..];
            var camel = char.ToLowerInvariant(propName[0]) + propName[1..];
            foreach (var (pk, ps) in schema.Properties)
            {
                if (!string.Equals(pk, camel, StringComparison.Ordinal))
                    continue;
                // 只标注内联的字符串 schema（字段类型保持 string，不改反序列化行为）
                if (ps.Reference is null && ps.Type == "string")
                {
                    ps.Enum = values.Select(v => (IOpenApiAny)new OpenApiString(v)).ToList();
                }
            }
        }
    }
}
