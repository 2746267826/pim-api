using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// 把 <see cref="IdempotencyKeyParameterOperationFilter"/> 加上的 <c>Idempotency-Key</c> 参数
/// 收敛到**运行时真的会生效**的端点，并补上运行时确实会返回的 409 与重放标记。
///
/// <para>
/// 为什么要用 DocumentFilter：operation filter 阶段还看不到 security（那是由
/// <see cref="SecurityMetadataOperationFilter"/> 在稍后写入的），也没有全局的路径视图。
/// 文档过滤器在所有 operation 都装配完之后运行，正好可以按「security 是否为 bearerAuth」判断
/// 该端点是否真有 JWT 用户 —— 只有这类请求才会进入 <c>IdempotencyMiddleware</c> 的幂等作用域。
/// </para>
/// <para>
/// 被剔除的三类：
/// 1. 匿名端点（security 为空）与 OpsKey 端点（security=opsKey）：没有 JWT 用户，运行时跳过；
/// 2. JSON 404 兜底 <c>/api/{path}</c>：不是业务端点；
/// 3. <see cref="Pim.Api.Middleware.IdempotencyMiddleware.NonReplayableRoutes"/>：响应含一次性凭据，不能缓存。
/// </para>
/// </summary>
public sealed class IdempotencyKeyDocumentFilter : IDocumentFilter
{
    private static readonly string[] SuccessCodes = ["200", "201", "202", "204"];

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        foreach (var (path, item) in document.Paths)
        {
            if (item.Operations is null) continue;
            foreach (var operation in item.Operations.Values)
            {
                if (operation.Parameters is null) continue;
                var parameter = operation.Parameters.FirstOrDefault(p =>
                    string.Equals(p.Name, Pim.Api.Middleware.IdempotencyMiddleware.HeaderName, StringComparison.OrdinalIgnoreCase));
                if (parameter is null) continue;

                if (!IsEligible(path, operation))
                {
                    operation.Parameters.Remove(parameter);
                    continue;
                }

                AddConflictResponse(operation);
                AddReplayHeader(operation);
            }
        }
    }

    private static bool IsEligible(string path, OpenApiOperation operation)
    {
        if (string.Equals(path, Pim.Api.Middleware.IdempotencyKey.CatchAllRoute, StringComparison.Ordinal))
            return false;
        if (Pim.Api.Middleware.IdempotencyMiddleware.NonReplayableRoutes.Contains(path))
            return false;
        // 只有 JWT 用户才有幂等作用域（见 IdempotencyMiddleware.InvokeAsync 的 userId 判定）。
        return operation.Security is { Count: > 0 } requirements
               && requirements.Any(requirement => requirement.Keys.Any(scheme =>
                   string.Equals(scheme.Reference?.Id,
                       SecurityMetadataOperationFilter.SecuritySchemes.BearerAuth,
                       StringComparison.Ordinal)));
    }

    private static void AddConflictResponse(OpenApiOperation operation)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.ContainsKey("409")) return;
        operation.Responses["409"] = new OpenApiResponse
        {
            Description = "同一 Idempotency-Key 的首次请求仍在执行中（业务码 40902）。客户端应稍后重试。",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "StringApiResponse" },
                    },
                },
            },
        };
    }

    private static void AddReplayHeader(OpenApiOperation operation)
    {
        const string headerName = Pim.Api.Middleware.IdempotencyMiddleware.ReplayedHeader;
        foreach (var code in SuccessCodes)
        {
            if (operation.Responses is null || !operation.Responses.TryGetValue(code, out var response))
                continue;
            response.Headers ??= new Dictionary<string, OpenApiHeader>();
            if (response.Headers.ContainsKey(headerName)) continue;
            response.Headers[headerName] = new OpenApiHeader
            {
                Description = "为 true 表示本次响应来自幂等缓存（未重复执行）。",
                Schema = new OpenApiSchema { Type = "string", Enum = [new Microsoft.OpenApi.Any.OpenApiString("true")] },
            };
        }
    }
}
