namespace Pim.Api.Infrastructure.Http;

/// <summary>
/// 暴露 API 实际看到的请求协议（已应用可信代理的 X-Forwarded-Proto）。
/// 部署侧公网 HTTPS 探测用它验证反代与信任配置，而不是把公网探测塞进容器健康检查。
/// </summary>
public sealed class RequestSchemeHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Pim-Request-Scheme";

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers[HeaderName] = context.Request.Scheme;
        await next(context);
    }
}
