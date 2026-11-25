using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Pim.Core.Common;
using Pim.Module.Mobile.DTOs;

namespace Pim.Api.Middleware;

/// <summary>
/// 诊断上报的 256 KB 上限必须在请求体绑定/反序列化之前生效。Minimal API 的端点过滤器
/// 在参数绑定之后才执行，不能用来限制真正的读取量；这里在鉴权之后、端点执行之前设置
/// Kestrel 的 MaxRequestBodySize，并对已知 Content-Length 直接拒绝。
/// </summary>
public sealed class DiagnosticRequestSizeMiddleware
{
    private const string EventsPath = "/api/v1/diagnostics/events";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;

    public DiagnosticRequestSizeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsDiagnosticUpload(context.Request))
        {
            await _next(context);
            return;
        }

        if (context.Request.ContentLength > DiagnosticLimits.MaxRequestBytes)
        {
            await WriteTooLargeAsync(context);
            return;
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = DiagnosticLimits.MaxRequestBytes;
        }

        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException ex)
            when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteTooLargeAsync(context);
        }
    }

    private static bool IsDiagnosticUpload(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return false;
        }

        var path = request.Path.Value?.TrimEnd('/');
        return string.Equals(path, EventsPath, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteTooLargeAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json";
        var response = ApiResponse<string>.Error(41301, "诊断上报请求超过 256 KB 上限。");
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}
