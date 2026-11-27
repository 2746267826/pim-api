using Microsoft.OpenApi.Models;
using Pim.Module.Calendar.DTOs;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// R02：明确 GET /api/v1/calendar/events 的窗口语义。Swashbuckle 只能看到 string
/// 参数，无法表达“日期/无偏移按 UTC、显式偏移按绝对时刻”的运行时契约。
/// </summary>
public sealed class CalendarEventQueryOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!string.Equals(context.ApiDescription.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                context.ApiDescription.RelativePath?.TrimEnd('/'),
                "api/v1/calendar/events",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetQuery(
            operation,
            "start",
            "查询窗口起点（含）。ISO-8601；日期或省略偏移的时间按 UTC 解释，显式偏移（Z/±HH:MM）标识绝对时刻，最长 366 天。"
            + " Window start (inclusive). ISO 8601; dates and offset-less values are UTC, explicit offsets are absolute instants; max 366 days.",
            "date-time",
            required: true);
        SetQuery(
            operation,
            "end",
            "查询窗口终点（不含）。与 start 使用同一 UTC/显式偏移规则；start/end 必填且跨度不超过 366 天。"
            + " Window end (exclusive). Same UTC/explicit-offset rule as start; both are required and the span must not exceed 366 days.",
            "date-time",
            required: true);
        SetQuery(operation, "search", "按标题包含匹配。 / Case-sensitive title contains match.");
        SetQuery(operation, "calendarId", "仅返回该日历本下的事件；结果始终限定为当前账号。 / Restrict to one calendar owned by the current account.");
        SetQuery(operation, "page", "页码，从 1 开始；默认 1。 / Page number starting at 1; defaults to 1.");
        SetQuery(operation, "pageSize", "每页条数，1-100；默认 20。 / Page size 1-100; defaults to 20.");
    }

    private static void SetQuery(
        OpenApiOperation operation,
        string name,
        string description,
        string? format = null,
        bool required = false)
    {
        var parameter = operation.Parameters?
            .FirstOrDefault(p => p.In == ParameterLocation.Query
                && string.Equals(p.Name, name, StringComparison.Ordinal));
        if (parameter is null)
            return;

        parameter.Description = description;
        parameter.Required = required;
        if (format is null)
            return;

        parameter.Schema ??= new OpenApiSchema();
        parameter.Schema.Type = "string";
        parameter.Schema.Format = format;
    }
}

/// <summary>
/// R02：把事件时间字段的“绝对时刻 / 事件当地时间 / 北京时间 / 来源判定”写进契约，
/// 避免客户端从 dtStart 的偏移反推用户时区。
/// </summary>
public sealed class CalendarEventSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties is null || schema.Properties.Count == 0)
            return;

        if (context.Type == typeof(EventResponse))
        {
            Describe(schema, "dtStart", "绝对开始时刻（ISO-8601，含显式偏移）。 / Absolute start instant with explicit offset.");
            Describe(schema, "dtEnd", "绝对结束时刻（ISO-8601，含显式偏移）。 / Absolute end instant with explicit offset.");
            Describe(schema, "timeZoneId", "规范化后的 IANA 时区；null 表示未知，不代表 UTC。 / Normalized IANA zone; null means unknown, not UTC.");
            Describe(schema, "sourceTimeZoneId", "来源时区原值（可能是 Windows ID）；null 表示来源未提供。 / Original source zone id (may be Windows); null when unavailable.");
            Describe(schema, "timeZoneResolution", "resolved=已解析为 IANA；sourceOnly=仅保留来源值；unknown=无来源信息。 / resolved, sourceOnly, or unknown.");
            Describe(schema, "timeZoneIdType", "timeZoneId 的类型；当前为 iana。 / Type of timeZoneId; currently iana.");
            Describe(schema, "timeZoneSourceType", "来源类型：windows / iana / unknown。 / Source type: windows, iana, or unknown.");
            Describe(schema, "localStart", "事件当地时间墙钟，格式 yyyy-MM-ddTHH:mm:ss；仅 resolved 且非全天时返回，需与 timeZoneId 一起解释。 / Event-local wall clock; only when resolved and timed.");
            Describe(schema, "localEnd", "事件当地时间墙钟，格式 yyyy-MM-ddTHH:mm:ss；仅 resolved 且非全天时返回，需与 timeZoneId 一起解释。 / Event-local wall clock; only when resolved and timed.");
            Describe(schema, "displayTimeZoneId", "展示时区（当前为 Asia/Shanghai）。 / Display zone (currently Asia/Shanghai).");
            Describe(schema, "displayLocalStart", "按 displayTimeZoneId 展示的开始墙钟；全天事件为 null。 / Start wall clock in displayTimeZoneId; null for all-day events.");
            Describe(schema, "displayLocalEnd", "按 displayTimeZoneId 展示的结束墙钟；全天事件为 null。 / End wall clock in displayTimeZoneId; null for all-day events.");
            Describe(schema, "allDayStartDate", "全天事件的独立开始日期；历史全天事件可能为 null（不从 dtStart 推断）。 / Independent all-day start date; legacy rows may be null.");
            Describe(schema, "allDayEndDateExclusive", "全天事件的独立排他结束日期；历史全天事件可能为 null（不从 dtEnd 推断）。 / Independent all-day end date (exclusive); legacy rows may be null.");
            Describe(schema, "recurrenceId", "重复发生项对应原始发生时刻的 ISO-8601 值；仅例外行使用。 / ISO-8601 original occurrence instant for exception rows.");
        }

        if (context.Type == typeof(CreateEventRequest)
            || context.Type == typeof(UpdateEventRequest)
            || context.Type == typeof(OutlookEventDraft))
        {
            Describe(schema, "dtStart", "ISO-8601 绝对开始时刻，必须携带显式 UTC 偏移且不能缺失；缺失、null 或无偏移值会被 400 拒绝。 / ISO-8601 start with an explicit UTC offset; missing, null, or offset-less values are rejected.");
            Describe(schema, "dtEnd", "ISO-8601 绝对结束时刻，必须携带显式 UTC 偏移且不能缺失；缺失、null 或无偏移值会被 400 拒绝。 / ISO-8601 end with an explicit UTC offset; missing, null, or offset-less values are rejected.");
            Describe(schema, "allDayStartDate", "全天事件的独立开始日期（format=date），全天事件必填；定时事件必须省略。更新时省略表示保留既有值。 / All-day start date; required for all-day events, omitted for timed events. Omission on update preserves the existing value.");
            Describe(schema, "allDayEndDateExclusive", "全天事件的独立排他结束日期（format=date），全天事件必填；定时事件必须省略。更新时省略表示保留既有值。 / All-day end date (exclusive); required for all-day events, omitted for timed events. Omission on update preserves the existing value.");
        }

        if (context.Type == typeof(CreateEventRequest)
            || context.Type == typeof(OutlookEventDraft))
        {
            Describe(schema, "timeZoneId", "可解析的 IANA/Windows 时区 ID；响应会规范化为 IANA。省略或显式 null 表示来源未知，不代表 UTC。 / Resolvable IANA/Windows id, normalized to IANA in responses; omitted or explicit null means unknown, not UTC.");
        }

        if (context.Type == typeof(UpdateEventRequest))
        {
            Describe(schema, "timeZoneId", "可解析的 IANA/Windows 时区 ID；响应会规范化为 IANA。省略表示保留既有来源时区；显式 null 清空为 unknown，不代表 UTC。 / Resolvable IANA/Windows id; omitted preserves the existing source zone, explicit null clears it to unknown, not UTC.");
        }

        if (context.Type == typeof(CreateEventRequest)
            || context.Type == typeof(UpdateEventRequest))
        {
            Describe(schema, "recurrenceId", "重复发生项对应原始发生时刻的 ISO-8601 值；省略或 null 表示不是例外。 / ISO-8601 original occurrence instant for exception rows; omitted or null when not an exception.");
            SetFormat(schema, "recurrenceId", "date-time");
        }
    }

    private static void Describe(OpenApiSchema schema, string property, string description)
    {
        if (schema.Properties.TryGetValue(property, out var target))
            target.Description = description;
    }

    private static void SetFormat(OpenApiSchema schema, string property, string format)
    {
        if (schema.Properties.TryGetValue(property, out var target))
            target.Format = format;
    }
}
