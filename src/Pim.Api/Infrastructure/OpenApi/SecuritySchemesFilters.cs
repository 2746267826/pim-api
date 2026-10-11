using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// 契约批次 3：把端点的真实鉴权要求映射为 operation 的 security。
/// 只描述现状，不改变任何鉴权行为：
/// - 端点元数据含 IAuthorizeData 且无 IAllowAnonymous → bearerAuth（JwtBearer）；
///   路径前缀 /api/v1/ops → opsKey（OpsKeyMiddleware 按 X-PIM-Ops-Key 头校验，非 JWT）；
/// - /mcp（McpServerBootstrap 的 bearer guard 中间件）：无 Authorization: Bearer 头即 401 → bearerAuth；
/// - 匿名端点（auth 登录注册、/health、/api/version、tiles、client shell、/api/v1/mcp/verify、
///   /api/v1/pc 的 readGroup 等）→ 不标 security（照实描述）。
/// </summary>
public sealed class SecurityMetadataOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var path = context.ApiDescription?.RelativePath ?? string.Empty;

        // /mcp 的 bearer guard 是中间件(McpServerBootstrap):缺失 Bearer 头一律 401,
        // 与端点元数据无关(SDK 的 MapMcp 自带 AllowAnonymous 也要先过这道闸)。
        if (path.Equals("mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("mcp/", StringComparison.OrdinalIgnoreCase))
        {
            operation.Security = [new OpenApiSecurityRequirement
            {
                [Scheme(SecuritySchemes.BearerAuth)] = [],
            }];
            return;
        }

        if (metadata.Any(m => m is IAllowAnonymous))
            return;

        if (metadata.Any(m => m is IAuthorizeData))
        {
            operation.Security = [new OpenApiSecurityRequirement
            {
                [Scheme(SecuritySchemes.BearerAuth)] = [],
            }];
            return;
        }

        if (path.StartsWith("api/v1/ops", StringComparison.OrdinalIgnoreCase))
        {
            // OpsKeyMiddleware（src/Pim.Api/Infrastructure/Ops/OpsKeyMiddleware.cs）：
            // 仅拦截 /api/v1/ops 前缀，要求 X-PIM-Ops-Key；未配置 key 时 503，不匹配时 401。
            operation.Security = [new OpenApiSecurityRequirement
            {
                [Scheme(SecuritySchemes.OpsKey)] = [],
            }];
        }
    }

    private static OpenApiSecurityScheme Scheme(string name) => new()
    {
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = name },
    };

    internal static class SecuritySchemes
    {
        public const string BearerAuth = "bearerAuth";
        public const string OpsKey = "opsKey";
    }
}

/// <summary>契约批次 3：securitySchemes 定义（bearer JWT + Ops Key），挂 DocumentFilter 输出。</summary>
public sealed class SecuritySchemesDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SecurityMetadataOperationFilter.SecuritySchemes.BearerAuth] =
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JwtBearer 认证（Authorization: Bearer &lt;token&gt;）。/api/v1/auth/login 或 /register 获取。",
            };
        document.Components.SecuritySchemes[SecurityMetadataOperationFilter.SecuritySchemes.OpsKey] =
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-PIM-Ops-Key",
                Description = "运维端点专用（/api/v1/ops/*）：与 PIM_OPS_KEY 配置比对（可多值，逗号分隔）。",
            };
    }
}
