using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Pim.Module.Calendar.Entities;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// R04：GET /api/v1/calendar/tasks 的 <c>status</c> 是重复查询参数（OR 集合）。
/// Swashbuckle can infer the array shape from <c>string[]</c>, but not the four-value
/// element enum from the runtime validator. This operation filter records the real contract:
/// <c>style=form, explode=true</c> with the same TaskStatuses whitelist the service uses.
/// </summary>
public sealed class TaskStatusQueryOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!string.Equals(context.ApiDescription.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                context.ApiDescription.RelativePath?.TrimEnd('/'),
                "api/v1/calendar/tasks",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var status = operation.Parameters?
            .FirstOrDefault(p => p.In == ParameterLocation.Query
                && string.Equals(p.Name, "status", StringComparison.Ordinal));
        if (status is null)
            return;

        status.Description =
            "任务状态筛选。重复该参数表示 OR 集合（例如 status=NEEDS-ACTION&status=IN-PROCESS）；"
            + "逗号分隔不合法。空值/省略表示不筛选，重复状态去重，任意非法值整单 400（02057）。"
            + "Repeat status to filter by multiple statuses (OR). Comma-separated values are rejected; "
            + "empty/omitted means no status filter; duplicates are ignored; any invalid value fails the whole request with 400 (02057).";
        status.Style = ParameterStyle.Form;
        status.Explode = true;
        status.Schema = new OpenApiSchema
        {
            Type = "array",
            Items = new OpenApiSchema
            {
                Type = "string",
                Enum = TaskStatuses.All
                    .Select(value => (IOpenApiAny)new OpenApiString(value))
                    .ToList(),
            },
        };
    }
}
