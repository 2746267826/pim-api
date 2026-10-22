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

    /// <summary>同时存在业务 409 时追加到描述里的幂等说明。</summary>
    private const string ConflictNote =
        "若同一 Idempotency-Key 的首次请求仍在执行中，服务端也用 409，但响应体是 StringApiResponse 形状的 {code:40902}；客户端应按 body 的 code 区分后两种 409。";

    /// <summary>Idempotency-Key 超长（&gt; 128 字符）时的 400 说明。</summary>
    private const string KeyTooLongNote =
        "请求头 Idempotency-Key 超过 128 个字符时，服务端返回 400，响应体是 StringApiResponse 形状的 {code:40010}。";

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
                AddKeyTooLongResponse(operation);
                AddReplayHeader(operation);
            }
        }
    }

    private static bool IsEligible(string path, OpenApiOperation operation)
    {
        if (string.Equals(path, Pim.Api.Middleware.IdempotencyKey.CatchAllRoute, StringComparison.Ordinal))
            return false;
        if (Pim.Api.Middleware.IdempotencyMiddleware.IsNonReplayableRoute(path))
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
        if (operation.Responses.TryGetValue("409", out var existing))
        {
            // 端点本来就有业务 409（例如 Outlook writeback）：中间件的「执行中」复用同一个状态码，
            // 但 body 是 {code:40902,...} 的 StringApiResponse。生成的客户端按单一 schema 反序列化
            // 第二种形态会失败，所以 schema 也要声明成联合（review P3-3），描述再写明按 code 区分。
            UnionWithStringApiResponse(existing, ConflictNote);
            return;
        }
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

    /// <summary>
    /// 幂等中间件在 key 超长时返回 400/40010 —— 对所有参与幂等的 operation 都成立。
    /// 端点原本没有 400 时补一个；已有业务 400 时把 schema 声明成联合，避免客户端按单一 schema
    /// 解析 40010 形态时失败（review P3-2）。
    /// </summary>
    private static void AddKeyTooLongResponse(OpenApiOperation operation)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.TryGetValue("400", out var existing))
        {
            // 已有 400 且本来就是 StringApiResponse：不需要改 schema，也不必重复描述
            // （40010 属于同类「参数非法」，全局说明见 docs/client-contract-semantics.md §2）。
            if (HasStringApiResponseSchema(existing))
                return;
            UnionWithStringApiResponse(existing, KeyTooLongNote);
            return;
        }
        operation.Responses["400"] = new OpenApiResponse
        {
            Description = KeyTooLongNote,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType { Schema = StringApiResponseSchema() },
            },
        };
    }

    /// <summary>把响应的 JSON schema 变成「原 schema 或 StringApiResponse」的联合，并追加说明。</summary>
    private static void UnionWithStringApiResponse(OpenApiResponse response, string note)
    {
        response.Description = string.IsNullOrEmpty(response.Description) ? note : $"{response.Description} {note}";
        response.Content ??= new Dictionary<string, OpenApiMediaType>();
        if (!response.Content.TryGetValue("application/json", out var media))
        {
            media = new OpenApiMediaType();
            response.Content["application/json"] = media;
        }
        if (media.Schema is null)
        {
            media.Schema = StringApiResponseSchema();
            return;
        }
        // 响应体本来就是 StringApiResponse：不需要联合（否则会写出 oneOf: [S, S]）。
        if (IsStringApiResponseReference(media.Schema) || IsStringApiResponseUnion(media.Schema))
            return;
        media.Schema = new OpenApiSchema { OneOf = [media.Schema, StringApiResponseSchema()] };
    }

    private static bool HasStringApiResponseSchema(OpenApiResponse response)
        => response.Content is { Count: > 0 } content
           && content.TryGetValue("application/json", out var media)
           && media.Schema is not null
           && (IsStringApiResponseReference(media.Schema) || IsStringApiResponseUnion(media.Schema));

    private static bool IsStringApiResponseUnion(OpenApiSchema schema)
        => schema.OneOf is { Count: > 0 } alternatives
           && alternatives.Any(IsStringApiResponseReference);

    private static bool IsStringApiResponseReference(OpenApiSchema schema)
        => string.Equals(schema.Reference?.Id, "StringApiResponse", StringComparison.Ordinal);

    private static OpenApiSchema StringApiResponseSchema()
        => new()
        {
            Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "StringApiResponse" },
        };

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
