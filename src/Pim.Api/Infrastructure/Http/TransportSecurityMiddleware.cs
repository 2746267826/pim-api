using Microsoft.AspNetCore.Http;
using Pim.Core.Common;

namespace Pim.Api.Infrastructure.Http;

/// <summary>
/// Q04：在可信 X-Forwarded-Proto 已生效后，拒绝来自非回环地址的明文数据请求，
/// 并给所有 HTTPS 响应补 HSTS。安全配置错误（例如反代漏传转发头）会以 426 明确失败，
/// 而不是把登录密码或笔记正文降级到 HTTP。
/// </summary>
public sealed class TransportSecurityMiddleware(
    RequestDelegate next,
    TransportSecurityOptions options,
    ILogger<TransportSecurityMiddleware> logger)
{
    public const string TransportHeaderName = "X-Pim-Transport";
    public const string CleartextValue = "cleartext";
    public const string HttpsRequiredValue = "https-required";
    public const int HttpsRequiredErrorCode = 42600;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            if (options.EnableHsts
                && !context.Response.Headers.ContainsKey("Strict-Transport-Security"))
            {
                context.Response.Headers["Strict-Transport-Security"] =
                    $"max-age={options.HstsMaxAgeSeconds}";
            }
        }
        else if (options.EnforceHttps)
        {
            context.Response.Headers[TransportHeaderName] = CleartextValue;
        }

        if (!TransportSecurityPolicy.ShouldRejectCleartext(options, context))
        {
            await next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        logger.LogWarning(
            "Rejected cleartext HTTP request for protected path {Path} from {RemoteIp}; "
            + "expected HTTPS via a trusted reverse proxy",
            context.Request.Path.Value,
            remoteIp);

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.Headers[TransportHeaderName] = HttpsRequiredValue;
        context.Response.Headers["Upgrade"] = "TLS/1.2";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsJsonAsync(
            ApiResponse<string>.Error(
                HttpsRequiredErrorCode,
                "该接口要求 HTTPS：请通过 HTTPS 入口访问，或检查反向代理的 X-Forwarded-Proto 与可信代理配置。"),
            cancellationToken: context.RequestAborted);
    }
}
