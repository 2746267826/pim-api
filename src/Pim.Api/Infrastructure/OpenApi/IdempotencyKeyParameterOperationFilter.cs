using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// 契约语义 §2（docs/client-contract-semantics.md）：所有写操作接受可选的 <c>Idempotency-Key</c> 请求头。
/// 运行时由 <c>IdempotencyMiddleware</c> 实现；这里只把这条**契约承诺**写进文档，
/// 让生成的 SDK 自带该参数，而不是让客户端去读文档猜。
/// </summary>
public sealed class IdempotencyKeyParameterOperationFilter : IOperationFilter
{
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "PATCH", "DELETE",
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!WriteMethods.Contains(context.ApiDescription.HttpMethod ?? string.Empty))
            return;
        var path = "/" + (context.ApiDescription.RelativePath ?? string.Empty).TrimStart('/');
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
            return;
        operation.Parameters ??= new List<OpenApiParameter>();
        if (operation.Parameters.Any(p => string.Equals(p.Name, Pim.Api.Middleware.IdempotencyMiddleware.HeaderName, StringComparison.OrdinalIgnoreCase)))
            return;
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = Pim.Api.Middleware.IdempotencyMiddleware.HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description = "可选。同一逻辑操作重试时复用同一个值（建议 UUID）；成功后 24 小时内同 key 重放返回首次响应，"
                          + "首次仍在执行中返回 409（code=40902）。读操作忽略该头。",
            Schema = new OpenApiSchema { Type = "string", MaxLength = 128 },
        });
    }
}
