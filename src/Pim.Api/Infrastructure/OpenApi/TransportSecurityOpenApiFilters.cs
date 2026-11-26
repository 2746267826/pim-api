using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Pim.Api.Infrastructure.Http;
using Pim.Core.Common;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// Q04 传输门禁由中间件而不是端点实现，Swashbuckle 无法从端点元数据推断
/// HTTP 426 / 42600 与自定义传输头。本过滤器为每个非豁免操作声明真实可能出现的
/// 拒绝响应，使契约快照和生成客户端类型与运行时一致。
/// </summary>
public sealed class TransportSecurityOperationFilter : IOperationFilter
{
    public const string HttpsRequiredStatus = "426";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (IsExempt(context.ApiDescription.RelativePath))
        {
            return;
        }

        if (operation.Responses.ContainsKey(HttpsRequiredStatus))
        {
            return;
        }

        operation.Responses[HttpsRequiredStatus] = new OpenApiResponse
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.Response,
                Id = TransportSecurityDocumentFilter.HttpsRequiredResponseId,
            },
        };
    }

    private static bool IsExempt(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var queryIndex = relativePath.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex >= 0 ? relativePath[..queryIndex] : relativePath;
        return TransportSecurityPolicy.IsExemptPath(new PathString("/" + path.TrimStart('/')));
    }
}

/// <summary>
/// OpenAPI 没有“所有响应共用头”的标准位置。这里同时提供可复用的
/// components.responses / components.headers 与稳定、可机器读取的
/// x-pim-transport-security 扩展，让“X-Pim-Request-Scheme 始终存在、
/// X-Pim-Transport 条件存在”不被藏在文档正文里。
/// </summary>
public sealed class TransportSecurityDocumentFilter : IDocumentFilter
{
    public const string ExtensionName = "x-pim-transport-security";
    public const string HttpsRequiredResponseId = "PimHttpsRequired";

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Headers ??= new Dictionary<string, OpenApiHeader>();
        document.Components.Headers[RequestSchemeHeaderMiddleware.HeaderName] =
            new OpenApiHeader
            {
                Description =
                    "Effective request scheme after trusted X-Forwarded-Proto processing. "
                    + "Present on every HTTP response, including transport rejections.",
                Schema = new OpenApiSchema
                {
                    Type = "string",
                    Enum =
                    [
                        new OpenApiString("http"),
                        new OpenApiString("https"),
                    ],
                },
            };
        document.Components.Headers[TransportSecurityMiddleware.TransportHeaderName] =
            new OpenApiHeader
            {
                Description =
                    "Transport marker: cleartext on cleartext requests while HTTPS "
                    + "enforcement is enabled, https-required on every HTTP 426 "
                    + "transport rejection.",
                Schema = new OpenApiSchema
                {
                    Type = "string",
                    Enum =
                    [
                        new OpenApiString(TransportSecurityMiddleware.CleartextValue),
                        new OpenApiString(TransportSecurityMiddleware.HttpsRequiredValue),
                    ],
                },
            };
        document.Components.Headers["Strict-Transport-Security"] = new OpenApiHeader
        {
            Description =
                "HSTS on HTTPS responses when PIM_HSTS_MAX_AGE_SECONDS is greater than 0; "
                + "defaults to 31536000 outside Development/Test.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Example = new OpenApiString(
                    $"max-age={TransportSecurityOptions.DefaultHstsMaxAgeSeconds}"),
            },
        };

        document.Components.Responses ??= new Dictionary<string, OpenApiResponse>();
        document.Components.Responses[HttpsRequiredResponseId] =
            CreateHttpsRequiredResponse(context);

        var exemptPaths = new OpenApiArray();
        foreach (var path in TransportSecurityPolicy.ExemptPaths)
        {
            exemptPaths.Add(new OpenApiString(path));
        }

        document.Extensions[ExtensionName] = new OpenApiObject
        {
            ["enforcement"] = new OpenApiString(
                "Non-Development/Test environments default to PIM_REQUIRE_HTTPS=true, "
                + "PIM_ALLOW_LOOPBACK_CLEARTEXT=false and a 31536000-second HSTS age. "
                + "Cleartext requests to non-exempt paths return HTTP 426 with envelope "
                + "code 42600 when a trusted reverse proxy does not supply "
                + "X-Forwarded-Proto: https."),
            ["requestSchemeHeader"] = new OpenApiObject
            {
                ["name"] = new OpenApiString(RequestSchemeHeaderMiddleware.HeaderName),
                ["presentOn"] = new OpenApiString("every HTTP response"),
                ["value"] = new OpenApiString(
                    "effective request scheme after trusted X-Forwarded-Proto processing "
                    + "(http or https)"),
            },
            ["transportHeader"] = new OpenApiObject
            {
                ["name"] = new OpenApiString(TransportSecurityMiddleware.TransportHeaderName),
                ["presentOn"] = new OpenApiString(
                    "cleartext requests while PIM_REQUIRE_HTTPS=true, and every HTTP 426 "
                    + "transport rejection"),
                ["values"] = new OpenApiArray
                {
                    new OpenApiString(TransportSecurityMiddleware.CleartextValue),
                    new OpenApiString(TransportSecurityMiddleware.HttpsRequiredValue),
                },
            },
            ["rejection"] = new OpenApiObject
            {
                ["status"] = new OpenApiInteger(StatusCodes.Status426UpgradeRequired),
                ["bodyCode"] = new OpenApiInteger(
                    TransportSecurityMiddleware.HttpsRequiredErrorCode),
                ["contentType"] = new OpenApiString("application/json"),
                ["headers"] = new OpenApiArray
                {
                    new OpenApiString(RequestSchemeHeaderMiddleware.HeaderName),
                    new OpenApiString(TransportSecurityMiddleware.TransportHeaderName),
                    new OpenApiString("Upgrade"),
                    new OpenApiString("Cache-Control"),
                },
            },
            ["hsts"] = new OpenApiObject
            {
                ["name"] = new OpenApiString("Strict-Transport-Security"),
                ["value"] = new OpenApiString(
                    $"max-age=<PIM_HSTS_MAX_AGE_SECONDS>; default "
                    + $"{TransportSecurityOptions.DefaultHstsMaxAgeSeconds} outside "
                    + "Development/Test, omitted when 0"),
            },
            ["exemptPaths"] = exemptPaths,
            ["exemptPathPrefixes"] = new OpenApiArray
            {
                new OpenApiString(TransportSecurityPolicy.AcmeChallengePathPrefix),
            },
        };
    }

    private static OpenApiResponse CreateHttpsRequiredResponse(
        DocumentFilterContext context)
    {
        var schema = context.SchemaGenerator.GenerateSchema(
            typeof(ApiResponse<string>),
            context.SchemaRepository);
        var response = new OpenApiResponse
        {
            Description =
                "HTTPS required. Production-like environments reject cleartext requests to "
                + "protected endpoints with HTTP 426 and envelope code 42600 unless a trusted "
                + "reverse proxy supplies X-Forwarded-Proto: https. Exempt operations are "
                + "listed in x-pim-transport-security.exemptPaths.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = schema,
                    Example = new OpenApiObject
                    {
                        ["code"] = new OpenApiInteger(
                            TransportSecurityMiddleware.HttpsRequiredErrorCode),
                        ["message"] = new OpenApiString(
                            "该接口要求 HTTPS：请通过 HTTPS 入口访问，或检查反向代理的 "
                            + "X-Forwarded-Proto 与可信代理配置。"),
                        ["data"] = new OpenApiNull(),
                        ["timestamp"] = new OpenApiString("2026-01-01T00:00:00+00:00"),
                    },
                },
            },
        };
        response.Headers[RequestSchemeHeaderMiddleware.HeaderName] = new OpenApiHeader
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.Header,
                Id = RequestSchemeHeaderMiddleware.HeaderName,
            },
        };
        response.Headers[TransportSecurityMiddleware.TransportHeaderName] = new OpenApiHeader
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.Header,
                Id = TransportSecurityMiddleware.TransportHeaderName,
            },
        };
        response.Headers["Upgrade"] = new OpenApiHeader
        {
            Description = "Upgrade hint advertised with the HTTPS-required rejection.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Example = new OpenApiString("TLS/1.2"),
            },
        };
        response.Headers["Cache-Control"] = new OpenApiHeader
        {
            Description = "Transport rejections must not be cached.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Example = new OpenApiString("no-store"),
            },
        };
        return response;
    }
}
