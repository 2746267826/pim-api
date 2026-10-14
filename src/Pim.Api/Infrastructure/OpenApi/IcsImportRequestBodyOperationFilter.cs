using Microsoft.OpenApi.Models;
using Pim.Module.Calendar.DTOs;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// issue #417 第二节第 1 条：<c>POST /api/v1/calendar/import-ics</c> 的 handler 直接读
/// <c>HttpRequest</c> 的表单，Swashbuckle 无从推断请求体 —— 契约里该 operation
/// 此前**完全没有 requestBody**，客户端生成不出上传接口。这里只补文档。
///
/// 为什么不用 <c>.Accepts&lt;IcsImportForm&gt;("multipart/form-data")</c>：
/// <c>IAcceptsMetadata</c> 在 .NET 8 里参与**端点匹配**，加上它以后非 multipart 的请求
/// 直接匹配不上端点、落到全局兜底 404「接口不存在」；而改动前这些请求是能进 handler 的
/// （text/plain → 400「需要 multipart/form-data」，form-urlencoded 缺文件 → 400「缺少文件字段」）。
/// 也就是说 <c>.Accepts</c> 会把 handler 自己的两条 400 分支变成死代码 —— 与工单
/// 「契约说的和实现做的一致」的方向相反。所以这里用只影响文档的 IOperationFilter，
/// 运行时绑定、匹配、错误文案一个字都不动。
///
/// 对照：keystats 两个端点仍用 <c>.Accepts&lt;T&gt;</c>，因为它们的 <c>[FromBody]</c> 参数
/// 本来就推导出同一份 <c>application/json</c> 元数据（实测非 JSON 请求改动前后都是 404），
/// 加不加 Accepts 的匹配行为完全一致。
/// </summary>
public sealed class IcsImportRequestBodyOperationFilter : IOperationFilter
{
    private const string RoutePath = "api/v1/calendar/import-ics";
    private const string MediaType = "multipart/form-data";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var description = context.ApiDescription;
        if (!string.Equals(description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(description.RelativePath?.TrimStart('/'), RoutePath, StringComparison.Ordinal))
            return;

        // 真出现绑定参数时以框架推断的形状为准，不覆盖。
        if (operation.RequestBody is not null)
            return;

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                [MediaType] = new OpenApiMediaType
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(IcsImportForm), context.SchemaRepository)
                }
            }
        };
    }
}
