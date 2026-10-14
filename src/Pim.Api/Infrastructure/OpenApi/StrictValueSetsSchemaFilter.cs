using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Pim.Module.Calendar.Entities;
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

        // 工单 03 D1：任务状态受控值域。三个**请求**入口共用同一份白名单，
        // 服务端校验都在 CalendarService：CreateTaskAsync / UpdateTaskAsync /
        // BatchUpdateTasksAsync 都调用 ValidateTaskStatus → 集合外 02057（HTTP 400）。
        // 取值来自唯一真值来源 TaskStatuses.All，与运行时校验同源。
        ["CreateTaskRequest.Status"] = TaskStatuses.All,
        ["UpdateTaskRequest.Status"] = TaskStatuses.All,
        ["BatchTaskUpdateRequest.Status"] = TaskStatuses.All,
    };

    /// <summary>
    /// **响应取值域**（与上面的请求表分开）：
    ///
    /// 上面的表要求「每一条都必须对应真实的服务端校验代码」。响应侧不存在这样的对应物 ——
    /// <c>TaskResponse.status</c> 的值是服务端自己写出来的（<c>tasks.status</c>），
    /// 没有「客户端传错 → 400」的校验可言。把它硬塞进同一张表会破坏该表自己声明的规则，
    /// 所以这里另开一区：进这一区的字段，依据是**服务端写入侧的值域已受控**
    /// （<c>CalendarService.ValidateTaskStatus</c> 是 tasks.status 的唯一写入口），
    /// 因此响应里不可能出现集合外的值。工单 03 AC-2 要求请求侧与响应侧分别给出 enum 片段。
    ///
    /// ⚠️ 历史数据例外：收紧之前写入的集合外值不会被迁移（工单 D1.3「不得丢数据、不得批量改写语义」），
    /// 所以**旧行**的 status 仍可能不在 enum 里；enum 描述的是「新写入与本次改动后的服务端行为」。
    /// </summary>
    private static readonly Dictionary<string, string[]> ResponseMap = new(StringComparer.Ordinal)
    {
        // 写入口唯一：CalendarService.CreateTaskAsync / UpdateTaskAsync / BatchUpdateTasksAsync
        ["TaskResponse.Status"] = TaskStatuses.All,
    };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties is null || schema.Properties.Count == 0)
            return;

        ApplyMap(schema, context, Map);
        ApplyMap(schema, context, ResponseMap);
    }

    private static void ApplyMap(
        OpenApiSchema schema,
        SchemaFilterContext context,
        Dictionary<string, string[]> map)
    {
        foreach (var (key, values) in map)
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
