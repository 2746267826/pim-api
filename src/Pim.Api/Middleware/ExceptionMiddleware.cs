using System.Text.Json;
using Pim.Api.Infrastructure;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Providers.Google;

namespace Pim.Api.Middleware;

public class ExceptionMiddleware
{
    /// <summary>
    /// 与全站（Minimal API 的 <c>Results.Ok</c>、MCP、内部服务）一致的 JSON 选项：
    /// camelCase 属性名。**必须显式传**——裸的 <c>JsonSerializer.Serialize</c> 会输出
    /// PascalCase（<c>Code/Message/Data</c>），而前端只读 <c>message/detail/title</c>，
    /// 于是错误原因被吞掉、用户只看到「HTTP 400」（issue #342 次生缺陷）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException ex)
        {
            context.Response.StatusCode = ResolveDomainStatusCode(ex.ErrorCode);
            context.Response.ContentType = "application/json";
            var response = ApiResponse<string>.Error(ex.ErrorCode, ex.Message);
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
        }
        catch (OneDriveGraphException ex)
        {
            await WriteUpstreamFailureAsync(context, ex.StatusCode, ex.RetryAfterSeconds, ex);
        }
        catch (GoogleDriveException ex)
        {
            // Google 的原始 HTTP 失败（未被 adapter 归一化的部分）同样按上游语义映射，
            // 而不是落到 500：否则 Drive 的 429/403/5xx 会被报成「内部服务器错误」。
            await WriteUpstreamFailureAsync(context, ex.StatusCode, ex.RetryAfterSeconds, ex);
        }
        // ---- 归一化 provider 异常族（工单 05 / F1）----
        // 抽象层把这些异常定义成普通 Exception（不是 DomainException），因此**必须**在这里映射：
        // 少一段，上游的 404/429/502 就会退化成 500 —— 那正是「OneDrive 既有功能回退」的一种
        // （基线由 OneDriveGraphException 支路提供这些语义）。
        catch (ProviderTransientException ex)
        {
            await WriteUpstreamFailureAsync(context, ex.StatusCode, ex.RetryAfterSeconds, ex);
        }
        catch (ProviderUnauthorizedException ex)
        {
            // 上游拒绝了我们的 access token。引擎已按预算刷新重试过，走到这里说明重试也没用：
            // 对客户端而言这是上游故障（不是 PIM 的登录态问题），按基线口径给 502。
            await WriteUpstreamFailureAsync(context, StatusCodes.Status502BadGateway, null, ex);
        }
        catch (ProviderCursorExpiredException ex)
        {
            // 同步游标失效由引擎内部重置重扫；能逃到这里说明重扫也没成功。
            await WriteUpstreamFailureAsync(context, StatusCodes.Status502BadGateway, null, ex);
        }
        catch (ProviderContentTooLargeException ex)
        {
            await WriteDomainLikeErrorAsync(
                context, StatusCodes.Status400BadRequest, 5331, "文件超过处理上限，请在网盘中操作");
            _logger.LogInformation(ex, "Provider content exceeded the configured limit");
        }
        catch (ProviderConfigurationFailureException ex)
        {
            // 服务端配置/运维问题（例如第三方项目未启用 API）：按 5xx 处理并记 Error，
            // 让它进监控告警；报成 400 会让用户按「重新连接」的提示陷入死循环，
            // 同时掩盖了真正需要运维介入的故障。
            _logger.LogError(ex, "Provider configuration failure");
            await WriteDomainLikeErrorAsync(
                context,
                StatusCodes.Status502BadGateway,
                5390,
                "网盘服务配置异常，请联系管理员");
        }
        catch (ProviderStorageQuotaExceededException ex)
        {
            await WriteDomainLikeErrorAsync(
                context, StatusCodes.Status400BadRequest, 5345, "网盘空间或配额不足，请清理后重试");
            _logger.LogWarning(ex, "Provider storage quota exceeded");
        }
        catch (ProviderItemNotFoundException ex)
        {
            await WriteDomainLikeErrorAsync(context, StatusCodes.Status404NotFound, 5104, "文件不存在");
            _logger.LogInformation(ex, "Provider item not found");
        }
        catch (ProviderAuthorizationExpiredException ex)
        {
            // 同一个归一化异常同时承载两种可自愈原因：账号授权失效、或该文件/该操作权限不足。
            // 文案两种都点到，避免把「该文件没权限」说成「授权过期」而把用户引到错误的修法上。
            await WriteDomainLikeErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                5322,
                "网盘授权已失效或该文件权限不足，请到设置页重新连接该账号或检查文件权限");
            _logger.LogWarning(ex, "Provider authorization/permission rejected");
        }
        catch (ProviderCapabilityNotSupportedException ex)
        {
            await WriteDomainLikeErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                5334,
                $"该文件来源（{ex.ProviderKey}）不支持 {ex.Capability}");
        }
        catch (FileProviderAuthorizationFailedException ex)
        {
            // 含「未配置 client id/secret」这类可执行提示：**原文回给用户**（里面只有变量名，没有密钥）
            await WriteDomainLikeErrorAsync(context, StatusCodes.Status400BadRequest, 5324, ex.Message);
            _logger.LogWarning(ex, "Provider authorization failed");
        }
        catch (FileProviderAuthorizationDeniedException ex)
        {
            await WriteDomainLikeErrorAsync(context, StatusCodes.Status400BadRequest, 5325, ex.Message);
            _logger.LogInformation(ex, "Provider authorization denied by the user");
        }
        catch (FileProviderAuthorizationPendingException ex)
        {
            // 轮询未完成是正常中间态；能落到出口说明调用方用错了接口，给明确提示而不是 500
            await WriteDomainLikeErrorAsync(context, StatusCodes.Status400BadRequest, 5324, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            _logger.LogWarning(ex, "Bad HTTP request with correlation id {CorrelationId}: {Message}", correlationId, ex.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            var response = ApiResponse<string>.Error(40000, $"请求参数格式无效: {ex.Message}");
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
        }
        catch (Exception ex) when (ClientAbort.IsClientAbort(context, ex))
        {
            // 请求方已断开（如地图拖动/缩放取消在途瓦片请求，issue #299）：
            // 响应已无人接收，按 nginx 惯例记 499，且不记 Error —— 否则每次浏览地图都会
            // 向错误日志与错误率指标打入一串 5xx 脉冲，掩盖真实故障。
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            _logger.LogInformation(
                "Client aborted the request with correlation id {CorrelationId}",
                correlationId);
            await SetClientClosedRequestStatusAsync(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString();
            _logger.LogError(ex, "Unhandled exception with correlation id {CorrelationId}", correlationId);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            var response = ApiResponse<string>.Error(01001, "内部服务器错误");
            await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    /// <summary>
    /// 标记 499 并尽力结束响应。
    ///
    /// 仅当响应尚未开始时才能改状态码：若响应头已发出（例如 <c>Results.File</c> 已开始回包，
    /// 随后在写响应体途中客户端断开），此时改状态码会抛异常，只能保留原状态码。
    /// 这类"已开始响应后才中断"的请求因此不会被计成 499 —— 这是 ASP.NET Core 响应生命周期的
    /// 限制：响应开始后 Kestrel 同样无法回写新的状态码（499 本身是应用层使用的非标准码，
    /// Kestrel 不会自动设置它）。
    /// 若要统计全部中断，需要独立的 abort 计数指标，不能只依赖 HTTP 状态码。
    ///
    /// 中途不能抛出：异常处理中间件一旦二次抛异常，异常会绕过这里继续向外传播，
    /// 结果反而是更响的 500 噪音。请求已断开时写入通常不会成功（也可能抛
    /// <c>ObjectDisposedException</c>），所以整体吞掉。
    /// </summary>
    private static async Task SetClientClosedRequestStatusAsync(HttpContext context)
    {
        try
        {
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            await context.Response.CompleteAsync();
        }
        catch (Exception)
        {
            // 请求方已断开，写响应失败属预期，忽略。
        }
    }

    /// <summary>
    /// 上游（网盘）失败的统一出口：404/429 透传、其余按上游错误 502；
    /// 429 带上 <c>Retry-After</c>。**细节（含上游错误体）只进日志**，给客户端的是通用文案。
    /// </summary>
    private async Task WriteUpstreamFailureAsync(
        HttpContext context,
        int upstreamStatusCode,
        int? retryAfterSeconds,
        Exception ex)
    {
        _logger.LogWarning(ex, "Provider call failed with upstream status {StatusCode}", upstreamStatusCode);
        var status = upstreamStatusCode switch
        {
            StatusCodes.Status404NotFound => StatusCodes.Status404NotFound,
            StatusCodes.Status429TooManyRequests => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status502BadGateway,
        };

        if (status == StatusCodes.Status429TooManyRequests && retryAfterSeconds is { } retry)
        {
            context.Response.Headers.RetryAfter = retry.ToString();
        }

        var code = status == StatusCodes.Status404NotFound ? 5104 : 5390;
        var message = status == StatusCodes.Status404NotFound
            ? "文件不存在"
            : "网盘服务暂时不可用，请稍后重试";
        await WriteDomainLikeErrorAsync(context, status, code, message);
    }

    /// <summary>把错误写成与 DomainException 支路完全一致的响应体（同一个 ApiResponse 形状）。</summary>
    private static async Task WriteDomainLikeErrorAsync(
        HttpContext context,
        int statusCode,
        int errorCode,
        string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var response = ApiResponse<string>.Error(errorCode, message);
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static int ResolveDomainStatusCode(int errorCode) => errorCode switch
    {
        40401 or 4004 or 4006 or 5104 or 5300 or 5304 or 5305 => StatusCodes.Status404NotFound,
        40101 => StatusCodes.Status401Unauthorized,
        40301 or 40302 or 40303 => StatusCodes.Status403Forbidden,
        40901 => StatusCodes.Status409Conflict,
        41301 => StatusCodes.Status413PayloadTooLarge,
        42901 => StatusCodes.Status429TooManyRequests,
        50301 => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };
}
