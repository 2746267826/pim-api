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

        // MobileUsageIngestService emits these exact wire values for 202 responses only.
        // Both are declared optional on MobileUsageIngestResult; terminal replays omit them.
        ["MobileUsageIngestResult.ProcessingState"] = ["in-flight", "retry-scheduled"],
    };

    /// <summary>
    /// **响应侧不声明闭合 enum，只写口径说明**（与维护者确认的口径）。
    ///
    /// 为什么：请求侧的四值是真校验（集合外 → 400 / 02057），所以那里标 enum 是实话；
    /// 但响应侧的值来自库里既有数据，而工单 D1.3 明确**不迁移**收紧前写入的历史值
    /// （例如 `DOING`、`in-progress`）。给响应字段标闭合 enum，就等于替历史行做了一个
    /// 契约上不成立的承诺 —— 严格生成的客户端（Kotlin/Java 枚举）会真的反序列化失败，
    /// 而「文字 description 说明例外」并不能改变 enum 的机器约束。
    /// 所以这里改成：把**当前写入侧只产生哪四个值**与**历史行可能是别的字符串**都写进
    /// description，让契约与响应现实一致（本仓原则：契约说的必须成真）。
    /// </summary>
    private static readonly Dictionary<string, string> ResponseNotes = new(StringComparer.Ordinal)
    {
        ["TaskResponse.Status"] =
            "任务状态。服务端当前只写入 NEEDS-ACTION / IN-PROCESS / COMPLETED / CANCELLED，" +
            "写入其它值会被 400（02057）拒绝；但本字段**故意不声明闭合 enum** —— " +
            "值域收紧（工单 03）之前写入的历史行仍可能是其它字符串，且不做数据迁移，" +
            "契约必须与响应现实一致。客户端请按「未知值原样展示、不要把未知值回写」处理。",

        ["TaskTimerStateResponse.AccumulatedSeconds"] =
            "累计实际执行时长（秒）= 该任务所有 active / paused / completed / cancelled 段之和，"
            + "正在计时的段按查询时刻计入，planned（尚未执行）不计；逐段四舍五入取秒。"
            + "注意它是「实际花在任务上的时间」，包含任务被取消/删除前已经发生的那段，"
            + "**不代表任务成功完成**（是否完成看 TaskStatus）。",

        ["MobileUsageIngestResult.LeaseUntilUtc"] =
            "Present only when processingState=in-flight. It is the upper bound of the active "
            + "processing lease, not an expected completion time or a polling deadline.",

        ["MobileUsageIngestResult.RetryAfterSeconds"] =
            "Earliest-retry hint in seconds (minimum 1), equal to the 202 Retry-After header. "
            + "It is a not-before hint, not an exact retry time. For in-flight, the hint is "
            + "ceil(leaseUntilUtc-now); it is not a request to poll until lease expiry.",

        ["MobileUsageIngestResult.RetryAtUtc"] =
            "Present only when processingState=retry-scheduled. The server has scheduled a later retry; "
            + "retryAfterSeconds is an earliest-retry hint, not an exact retry time.",
    };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties is null || schema.Properties.Count == 0)
            return;

        ApplyMap(schema, context, Map);
        ApplyNotes(schema, context);
        if (context.Type == typeof(Pim.Module.Calendar.DTOs.UpdateTaskRequest))
        {
            schema.Description = "Partial task update: omitted fields keep their stored values. "
                + "Explicit null clears nullable fields; status null keeps the existing status. "
                + "title, priority and percentComplete cannot be null when provided.";
            foreach (var name in new[] { "title", "priority", "percentComplete" })
            {
                schema.Properties[name].Nullable = false;
                schema.Properties[name].Description = "Optional; omission preserves the current value. Explicit null is rejected (400).";
            }
        }
    }

    /// <summary>把响应侧口径说明写进字段 description（不改变字段类型，也不声明 enum）。</summary>
    private static void ApplyNotes(OpenApiSchema schema, SchemaFilterContext context)
    {
        foreach (var (key, note) in ResponseNotes)
        {
            var dot = key.LastIndexOf('.');
            if (!string.Equals(context.Type.Name, key[..dot], StringComparison.Ordinal))
                continue;

            var propName = key[(dot + 1)..];
            var camel = char.ToLowerInvariant(propName[0]) + propName[1..];
            foreach (var (pk, ps) in schema.Properties)
            {
                if (!string.Equals(pk, camel, StringComparison.Ordinal))
                    continue;
                // 说明可以落在任何内联字段上（例如整数型的累计秒数），不限于字符串
                if (ps.Reference is null)
                    ps.Description = note;
            }
        }
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
